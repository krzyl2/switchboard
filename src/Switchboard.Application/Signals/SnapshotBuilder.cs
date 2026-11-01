using System.Collections.Immutable;
using Switchboard.Application.Configuration;
using Switchboard.Domain.Balance;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Forecast;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Signals;

/// <summary>
/// One immutable <see cref="SignalSnapshot"/> out of the map, the source's status and the balance
/// counters at <c>now</c>. Built per snapshot and never kept: it reads and changes nothing in the store.
/// </summary>
internal sealed class SnapshotBuilder(
    SignalMap map,
    SignalSourceStatus source,
    ConfigurationResult? configurationResult,
    BalanceChecker balance,
    DateTimeOffset now)
{
    private static readonly ImmutableArray<Phase> AllPhases = [Phase.L1, Phase.L2, Phase.L3];

    public SignalSnapshot Build(long sequence, RejectionCounters rejections)
    {
        if (map.Configuration is not { } configuration)
        {
            return new SignalSnapshot(
                sequence,
                now,
                false,
                source,
                null,
                [],
                new ProtectionInputs(
                    sequence,
                    now,
                    null,
                    [.. AllPhases.Select(p => new PhaseProtection(p, false, ProtectionInvalidReason.ConfigurationInvalid, null))],
                    null,
                    null,
                    [],
                    configurationResult?.Status switch
                    {
                        null => SetupAbsence.NotRead,
                        ConfigurationStatus.Missing => SetupAbsence.Missing,
                        _ => SetupAbsence.Invalid,
                    }),
                new DispatchInputs(sequence, now, [.. AllPhases.Select(p => new PhaseDispatch(p, false, DispatchInvalidReason.ConfigurationInvalid, null, null, null, null))]),
                [],
                [],
                rejections);
        }

        var phases = configuration.Connection.Phases;
        var generation = GenerationShares.Read(map, now);
        return new SignalSnapshot(
            sequence,
            now,
            true,
            source,
            configuration.Connection,
            Readings(configuration),
            new ProtectionBuilder(map, source, now).Build(configuration, sequence),
            new DispatchInputs(sequence, now, [.. phases.Select(p => Dispatch(p, generation))]) { DevicePowers = DevicePowers(), Energy = Energy() },
            [.. phases.Select(balance.For)],
            [.. phases.Select(p => Reserve(configuration, p))],
            rejections)
        {
            DispatchSetup = map.DispatchSetup,
            Prices = Prices(),
            Forecast = Forecast(),
            Presentation = new PresentationInputs(
                configuration.Connection.Voltage,
                generation.State,
                generation.ReportedAt,
                phases.ToImmutableDictionary(p => p, p => map.WithRole(SignalRole.PhaseVoltage, p) is not null),
                configuration.Devices.Where(d => d.Name is not null).ToImmutableDictionary(d => d.Id, d => d.Name!)),
        };
    }

    /// <summary>
    /// The hourly balance's counters (BAL-01). A cumulative counter that does not change is not reported
    /// again (the export counter stands still while the house imports, and the import counter while it
    /// exports), so its age says nothing about whether it is right. The pair is therefore usable when
    /// neither counter is unavailable or refused and the meter is demonstrably alive: a counter or a
    /// phase power was reported within its max_age. The report time is the latest such report: the values
    /// are valid as of then. Null when the roles are not mapped.
    /// </summary>
    private EnergyInputs? Energy()
    {
        if (map.WithRole(SignalRole.EnergyExport) is not { } export || map.WithRole(SignalRole.EnergyImport) is not { } import)
        {
            return null;
        }

        var maxAge = export.Definition.MaxAge < import.Definition.MaxAge ? export.Definition.MaxAge : import.Definition.MaxAge;
        var counters = new[] { export, import };
        if (counters.Any(c => SignalFreshness.Classify(c, now).State is SignalState.Unavailable or SignalState.Rejected || c.Accepted is null))
        {
            return new EnergyInputs(null, maxAge);
        }

        var alive = counters.Concat(map.Signals.Where(s => s.Definition.Role is SignalRole.PhasePower or SignalRole.TotalPower))
            .Where(s => s.Accepted is not null && SignalFreshness.IsFresh(s, now))
            .Select(s => s.Accepted!.Effective)
            .ToArray();
        if (alive.Length == 0)
        {
            return new EnergyInputs(null, maxAge);
        }

        return new EnergyInputs(new EnergyCounters(export.Magnitude(), import.Magnitude(), alive.Max()), maxAge);
    }

    /// <summary>
    /// The price signals as dispatch may read them (ECO-10): the value only while the signal is fresh (a report within max_age of
    /// <c>last_reported</c>), otherwise just its state, so an unavailable, unknown, stale or wrong-unit price is simply not usable and
    /// the spec's fallback stands in. Kept apart from the dispatch inputs; null when no price signal is mapped.
    /// </summary>
    private PriceInputs? Prices()
    {
        var samples = ImmutableDictionary.CreateBuilder<SignalName, PriceSample>();
        foreach (var signal in map.Signals.Where(s => s.Definition.Kind == SignalKind.Price))
        {
            var (state, detail) = SignalFreshness.Classify(signal, now);
            samples[signal.Definition.Name] = new PriceSample(
                state,
                detail,
                state == SignalState.Fresh && signal.Accepted is not null ? signal.Magnitude() : null,
                signal.Accepted?.Effective ?? signal.Raw?.Reported);
        }

        return samples.Count == 0 ? null : new PriceInputs(samples.ToImmutable());
    }

    /// <summary>
    /// The forecast and curve signals (kinds series, energy_forecast, timestamp) as the planner may read them: the content only while
    /// the signal is fresh (a report within max_age of <c>last_reported</c>), otherwise just its state, so a stale forecast is never
    /// used and the last known curve never stands in. Coverage is the content's own business (<see cref="ForecastSample.CoversUntil"/>),
    /// separate from freshness. Kept apart from the protection and dispatch inputs; null when none is mapped.
    /// </summary>
    private ForecastInputs? Forecast()
    {
        var samples = ImmutableDictionary.CreateBuilder<SignalName, ForecastSample>();
        foreach (var signal in map.Signals.Where(s => s.Definition.Kind is SignalKind.Series or SignalKind.EnergyForecast or SignalKind.Timestamp))
        {
            var (state, detail) = SignalFreshness.Classify(signal, now);
            var value = state == SignalState.Fresh ? signal.Accepted?.Value : null;
            samples[signal.Definition.Name] = new ForecastSample(
                signal.Definition.Role,
                signal.Definition.Kind,
                state,
                detail,
                signal.Accepted?.Effective ?? signal.Raw?.Reported,
                (value as SeriesValue)?.Series,
                (value as EnergyForecastValue)?.Energy.Value,
                (value as TimestampValue)?.At);
        }

        return samples.Count == 0 ? null : new ForecastInputs(samples.ToImmutable());
    }

    private PhaseDispatch Dispatch(Phase phase, GenerationShares generation)
    {
        if (source != SignalSourceStatus.Connected)
        {
            return new PhaseDispatch(phase, false, DispatchInvalidReason.SourceDisconnected, null, null, null, null);
        }

        var power = map.WithRole(SignalRole.PhasePower, phase);
        var derived = DirectionResolver.DerivedPower(map, phase, now);
        Watts? netImport = derived?.Power
            ?? (power is not null && SignalFreshness.IsFresh(power, now) ? new Watts(power.Magnitude()) : null);
        var share = generation.For(phase);
        Watts? load = netImport is { } n && share is { } g ? n + g : null;
        var voltageSignal = map.WithRole(SignalRole.PhaseVoltage, phase);
        Volts? voltage = voltageSignal is not null && SignalFreshness.IsFresh(voltageSignal, now)
            ? new Volts(voltageSignal.Magnitude())
            : null;

        DispatchInvalidReason? reason = power is null ? DispatchInvalidReason.PowerUnmapped
            : netImport is null ? DispatchInvalidReason.PowerNotFresh
            : share is null ? DispatchInvalidReason.GenerationUnknown
            : null;
        return new PhaseDispatch(phase, reason is null, reason, netImport, share, load, voltage)
        {
            PowerReportedAt = derived?.ReportedAt ?? (netImport is null ? null : power!.Accepted!.Effective),
        };
    }

    /// <summary>The declared power draws (W), for dispatch; a draw that is not trusted carries no value.</summary>
    private ImmutableArray<DevicePowerSample> DevicePowers() =>
    [
        .. map.PowerDraws.Select(d =>
        {
            var signal = map.ByName(d.Signal);
            var trusted = SignalFreshness.DrawTrusted(source, signal, now);
            return new DevicePowerSample(d.Device, d.Phase, trusted, trusted ? new Watts(signal.Magnitude()) : null, signal.Accepted?.Effective);
        }),
    ];

    private ReserveDerivation Reserve(SwitchboardConfiguration configuration, Phase phase)
    {
        var signal = configuration.Connection.Topology == Topology.ThreePhaseSummed
            ? map.WithRole(SignalRole.TotalCurrent)!
            : map.WithRole(SignalRole.PhaseCurrent, phase)!;
        var control = configuration.Control;
        var characteristic = configuration.Connection.Characteristic;
        return MinimumSafeReserve.Compute(
            phase,
            configuration.Connection,
            control.Breaker.Tiers[characteristic],
            control.Breaker.ContinuousMultiple[BreakerCharacteristic.Unknown],
            signal.Refresh.Estimate(control.Signals.Refresh),
            signal.Definition.MaxAge,
            new Amperes(signal.Definition.Sigma ?? 0),
            control.ReserveDerivation);
    }

    private ImmutableArray<SignalReading> Readings(SwitchboardConfiguration configuration)
    {
        var readings = ImmutableArray.CreateBuilder<SignalReading>();
        var refreshParameters = configuration.Control.Signals.Refresh;
        foreach (var signal in map.Signals)
        {
            var definition = signal.Definition;
            var (state, detail) = SignalFreshness.Classify(signal, now);
            var direction = signal.Accepted is not null && definition.Kind is SignalKind.Current or SignalKind.Power
                ? DirectionResolver.Resolve(map, signal, now)
                : null;
            readings.Add(new SignalReading(
                definition.Name,
                definition.Kind,
                definition.Role,
                definition.Phase,
                state,
                detail,
                state == SignalState.Unavailable ? null : DisplayValue(signal, direction),
                signal.Raw?.State,
                signal.Raw?.Unit,
                signal.Accepted?.Effective ?? signal.Raw?.Reported,
                definition.MaxAge,
                signal.Refresh.Estimate(refreshParameters),
                signal.Refresh.Count,
                direction,
                signal.RejectedCount,
                map.Dependents.GetValueOrDefault(definition.Name, [])));
        }

        foreach (var (role, phase) in UnmappedSlots(configuration))
        {
            var kind = role switch
            {
                SignalRole.PhaseCurrent or SignalRole.InverterPhaseCurrent => SignalKind.Current,
                SignalRole.PhaseVoltage => SignalKind.Voltage,
                _ => SignalKind.Power,
            };
            readings.Add(new SignalReading(null, kind, role, phase, SignalState.Unmapped, SignalDetail.None, null, null, null, null, null, null, 0, null, 0, []));
        }

        return readings.ToImmutable();
    }

    /// <summary>Optional role slots of the topology that no declared signal fills, so the user sees what could be mapped.</summary>
    private IEnumerable<(SignalRole Role, Phase? Phase)> UnmappedSlots(SwitchboardConfiguration configuration)
    {
        var phases = configuration.Connection.Phases;
        var perPhase = configuration.Connection.Topology == Topology.ThreePhaseSummed
            ? new[] { SignalRole.PhaseCurrent, SignalRole.PhasePower, SignalRole.PhaseVoltage }
            : [SignalRole.PhasePower, SignalRole.PhaseVoltage];
        foreach (var role in perPhase)
        {
            foreach (var phase in phases.Where(p => map.WithRole(role, p) is null))
            {
                yield return (role, phase);
            }
        }

        if (configuration.Connection.Topology == Topology.ThreePhaseSummed && map.WithRole(SignalRole.TotalPower) is null)
        {
            yield return (SignalRole.TotalPower, null);
        }

        if (map.WithRole(SignalRole.InverterPower) is null)
        {
            yield return (SignalRole.InverterPower, null);
        }
        else if (phases.All(p => map.WithRole(SignalRole.InverterPhaseCurrent, p) is null))
        {
            foreach (var phase in phases)
            {
                yield return (SignalRole.InverterPhaseCurrent, phase);
            }
        }
    }

    private static SignalValue? DisplayValue(SignalRuntime signal, DirectionInfo? direction)
    {
        if (signal.Accepted?.Value is not { } value || direction is null || signal.Definition.Sign != SignConvention.Unsigned)
        {
            return signal.Accepted?.Value;
        }

        var signed = DirectionResolver.SignedValue(signal, direction);
        return value switch
        {
            CurrentValue => new CurrentValue(new Amperes(signed)),
            PowerValue => new PowerValue(new Watts(signed)),
            _ => value,
        };
    }
}
