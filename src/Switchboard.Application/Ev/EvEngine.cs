using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Switchboard.Application.Configuration;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Options;
using Switchboard.Application.Planning;
using Switchboard.Application.Presentation;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Ev;
using Switchboard.Domain.Planning;

namespace Switchboard.Application.Ev;

/// <summary>
/// Computes the EV module's offer (EV-06, docs/EV.md S5): reads the EV inputs, the charger's power and the planner's notes, keeps the SoC estimate, the learned
/// consumption and the frozen trip target, persists what must survive a restart in <c>ev.json</c> and asks <see cref="EvComposer"/> for the goals. It holds no
/// clock (the caller passes <c>now</c>) and no command port, override or guard type; it is called from one loop (<see cref="EvSnapshotKeeper"/>), never from the
/// guard's thread. A failure throws to that loop, which publishes it as a failure (the goals are then missing, not stale).
/// </summary>
public sealed class EvEngine(
    IEvInputSource inputs,
    IPresentationSource presentation,
    PlanHolder plans,
    IPresentationStore<EvState> store,
    Func<DeviceName, bool> manualGoalActive,
    SignalClock clock,
    IOptions<StatisticsOptions> options,
    ILogger<EvEngine> logger,
    EvManualState? manualState = null)
{
    private readonly TimeSpan _maxGap = options.Value.MaxIntegrationGap;
    private readonly EvManualState _manual = manualState ?? new EvManualState();
    private EvState? _state;
    private SocEstimatorState _estimator = SocEstimatorState.Empty;
    private (DateTimeOffset At, double Watts)? _held;
    private Trip? _lastTrip;
    private bool _saveFailing;

    /// <summary>The offer at <paramref name="now"/>; null without a valid configuration that holds an <c>ev:</c> section.</summary>
    public EvSnapshot? Compute(DateTimeOffset now)
    {
        var view = inputs.Current;
        if (view.Configuration?.Configuration is not { Ev: { } ev } configuration)
        {
            Forget();
            return null;
        }

        var p = configuration.Control.Ev;
        var state = _state ??= Restore();
        var ev1 = view.Inputs;

        // The user's side (mode, charge-now) lives in EvManualState, where the API and the MQTT entities change it; this loop is the one writer of ev.json, so it
        // takes the change here and persists it below. A charge-now that ran out, or whose manual goal has ended (target reached, unplugged, cancelled), ends.
        _manual.Seed(state.Mode, state.ChargeNow);
        // The manual goal is read inside the atomic update, never before it: a charge-now that begins (goal first, then the state) between a read here and the
        // update must not be cleared on the strength of a goal that did not exist yet.
        _manual.Update(m => m.ChargeNow is { } running && (now >= running.ExpiresAt || !manualGoalActive(ev.Device)) ? m with { ChargeNow = null } : m);
        var manualGoal = manualGoalActive(ev.Device);
        var manual = _manual.Current;
        var dirty = false;
        if (manual.Mode != state.Mode || manual.ChargeNow != state.ChargeNow)
        {
            state = state with { Mode = manual.Mode, ChargeNow = manual.ChargeNow };
            dirty = true;
        }

        var plugged = !ev1.Plugged.Configured ? true : ev1.IsPlugged;
        var charger = ChargerStep(presentation.Power, now, ev.Device, plugged != false);

        var reading = ev1.SocPercent is { } percent && ev1.Soc.ReadAt is { } reportedAt ? new SocReading(percent, reportedAt) : null;
        var step = SocEstimator.Step(_estimator, reading, charger.Energy, now, ev.CapacityKwh, p);
        _estimator = step.State;

        var learned = state.Learned;
        var charging = plugged != false && !(charger.Energy.Fresh && charger.Watts <= 0);
        var outcome = KwhPerKmLearner.Learn(
            learned,
            new EvConsumptionObservation(ev1.SocPercent, ev1.Soc.ReadAt, ev1.RangeKm, ev1.Range.ReadAt, charging),
            ev.CapacityKwh,
            p.Learn);
        if (outcome.Accepted is not null)
        {
            state = state with { Learned = outcome.State };
            dirty = true;
        }

        var kwhPerKm = KwhPerKmLearner.Current(state.Learned, p.FallbackKwhPerKm, p.Learn);
        var next = NextTrip(ev1, now, p.MaxTripKm);
        var planner = configuration.Planner;
        var economics = configuration.Economics;
        var holidays = economics?.Holidays ?? HolidayCalendar.Empty;
        var composition = EvComposer.Compose(new EvComposerInput
        {
            Now = now,
            Zone = clock.Time.LocalTimeZone,
            Device = ev.Device,
            CapacityKwh = ev.CapacityKwh,
            Parameters = p,
            Mode = state.Mode,
            ManualGoalActive = manualGoal,
            Plugged = plugged,
            SocPercent = step.Estimate.Percent,
            KwhPerKm = kwhPerKm,
            NextTrip = next,
            Frozen = state.LastTrip?.ToFrozen(),
            LastFullAt = state.LastFullAt,
            IsHoliday = holidays.IsHoliday,
            DeviceSources = configuration.Devices.FirstOrDefault(d => d.Id == ev.Device)?.Sources,
            CheapZone = CheapZone(ev, economics),
            CalendarUnavailable = ev1.Calendar.Configured && !ev1.Calendar.IsFresh,
            RangeUnusable = ev1.Range.Configured && ev1.RangeKm is not > 0,
            ForecastMissing = planner is not null && plans.Current.Notes.Contains(PlanNote.PvForecastMissing),
            DispatchOff = planner is null || planner.Dispatch == PlannerDispatchMode.Off,
        });

        if (composition.Frozen is { } frozen && (state.LastTrip is null || EvLastTrip.From(frozen) != state.LastTrip))
        {
            state = state with { LastTrip = EvLastTrip.From(frozen) };
            dirty = true;
        }

        if (composition.WeeklyFullReached)
        {
            state = state with { LastFullAt = now };
            dirty = true;

            // A requested full charge is one-off: it is done once the car is full, and the module is back to its own rules.
            if (state.Mode == EvMode.FullCharge)
            {
                _manual.Update(m => m.Mode == EvMode.FullCharge ? m with { Mode = EvMode.Auto } : m);
                state = state with { Mode = EvMode.Auto };
            }
        }

        _state = state;
        if (dirty)
        {
            Save(state);
        }

        return new EvSnapshot(
            now,
            now + p.SnapshotMaxAge,
            ev.Device,
            composition,
            step.Estimate.Percent,
            step.Estimate.Gap,
            kwhPerKm,
            next,
            state.Mode,
            ev1.Plugged.Configured && ev1.IsPlugged is null,
            state.LastFullAt,
            plugged);
    }

    private Trip? NextTrip(EvInputs ev, DateTimeOffset now, double maxTripKm)
    {
        if (!ev.Calendar.Configured)
        {
            _lastTrip = null;
            return null;
        }

        if (ev.Calendar.IsFresh)
        {
            _lastTrip = TripParser.Assess(ev.CalendarEvent, now, maxTripKm).Next;
            return _lastTrip;
        }

        // Calendar unavailable: the last parsed trip stands until its start, and no new one is learned.
        return _lastTrip is { } trip && trip.Start > now ? trip : null;
    }

    /// <summary>The lowest zone price of the tariff unless the section names a zone; zone names are configuration, never code.</summary>
    private static string? CheapZone(EvDefinition ev, EconomicsDefinition? economics) =>
        ev.CheapZone ?? (economics?.Tariff as ZonedPrice)?.Zones.OrderBy(z => z.Price).ThenBy(z => z.Id, StringComparer.Ordinal).Select(z => z.Id).FirstOrDefault();

    /// <summary>
    /// The energy the charger drew since the previous step, at the power that step held (as the goal ledger counts), and whether its data is usable. A step
    /// older than the integration gap, a power of unknown source and an invalid power picture are not data: nothing is guessed.
    /// </summary>
    private (ChargerEnergy Energy, double Watts) ChargerStep(PowerView? view, DateTimeOffset now, DeviceName device, bool couldFlow)
    {
        var power = view is { DataValid: true } ? view.Devices.FirstOrDefault(d => d.Device == device) : null;
        if (view is null || power is not { Total: { } total, Source: not PowerSource.Unknown } || now - view.EvaluatedAt > _maxGap)
        {
            _held = null;
            return (new ChargerEnergy(0, false, couldFlow), 0);
        }

        var watts = Math.Max(0, total.Value);
        if (_held is not { } held)
        {
            _held = (view.EvaluatedAt, watts);
            return (new ChargerEnergy(0, true, couldFlow), watts);
        }

        if (view.EvaluatedAt <= held.At)
        {
            return (new ChargerEnergy(0, true, couldFlow), watts);
        }

        var elapsed = view.EvaluatedAt - held.At;
        _held = (view.EvaluatedAt, watts);
        return elapsed > _maxGap
            ? (new ChargerEnergy(0, false, couldFlow), watts)
            : (new ChargerEnergy(held.Watts * elapsed.TotalHours / 1000, true, couldFlow), watts);
    }

    private EvState Restore()
    {
        try
        {
            return store.Load().State ?? EvState.Default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "ev.json could not be read; the EV module starts from its defaults");
            return EvState.Default;
        }
    }

    private void Save(EvState state)
    {
        try
        {
            store.Save(state);
            _saveFailing = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_saveFailing)
            {
                logger.LogWarning(ex, "ev.json could not be written; what the EV module learned is kept in memory only");
                _saveFailing = true;
            }
        }
    }

    // Without an ev: section nothing is estimated; the persisted state is kept for when the section returns.
    private void Forget()
    {
        _estimator = SocEstimatorState.Empty;
        _held = null;
        _lastTrip = null;
    }
}
