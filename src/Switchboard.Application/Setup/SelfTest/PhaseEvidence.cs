using System.Collections.Immutable;
using System.Globalization;
using Switchboard.Application.Signals;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Setup.SelfTest;

/// <summary>One time-stamped value of a series, import positive (a magnitude for an unsigned current).</summary>
public readonly record struct ReportPoint(DateTimeOffset At, double Value);

/// <summary>A meter entity as the observation read it: what decides whether it still applies to a draft.</summary>
/// <param name="Sign">positive_import, positive_export or unsigned, as switchboard.yaml spells it.</param>
public sealed record MeterTerm(EntityId Entity, string Unit, string Sign);

public enum ObservationSource
{
    /// <summary>A guided watch: the user switched the device while the wizard watched.</summary>
    Watch,

    /// <summary>A switching the running add-on saw (someone else switched a declared device).</summary>
    Recorded,

    /// <summary>A switching of the active phase self-test: Switchboard switched the device on and off on the user's request (CONN-05).</summary>
    Active,
}

/// <summary>
/// A raw phase observation around one switching: per-phase reports, not a verdict. The verdict is
/// computed again against whatever the draft declares (<see cref="PhaseEvidence.Judge"/>), so fixing
/// a device's phase clears an old mismatch, and an observation measured with another meter mapping
/// (other entities, units or signs per phase) never counts for a draft whose meter rows changed.
/// </summary>
/// <param name="Direction">+1 switched on, −1 switched off.</param>
/// <param name="Unit">W (phase power or house load) or A (phase current when no power is mapped).</param>
/// <param name="HouseLoad">The values are the phase's house load (net import plus its share of fresh generation), so a cloud edge does not count.</param>
/// <param name="Note">Why the phases are missing (refresh not detected yet); null otherwise.</param>
public sealed record PhaseObservation(
    DateTimeOffset At,
    int Direction,
    EntityId StateEntity,
    ObservationSource Source,
    string Unit,
    bool Unsigned,
    bool HouseLoad,
    ImmutableDictionary<Phase, MeterTerm> Meter,
    ImmutableDictionary<Phase, PhaseStepMeasurement> Phases,
    bool Isolated,
    string? Note);

/// <summary>An observation with the verdict it gets for one declaration.</summary>
public sealed record JudgedObservation(PhaseObservation Observation, PhaseStepResult Result);

/// <summary>Every observation of one switched entity, with the verdict over them.</summary>
public sealed record PhaseJudgement(PhaseStepSummary Summary, ImmutableArray<JudgedObservation> Observations);

/// <summary>The window around a switching and how each phase is read in it.</summary>
public static class StepWindow
{
    /// <summary>
    /// The longer of the isolation time and (2 + current_lag_polls) refresh intervals of the meter,
    /// so a meter polled every 30 s is still classifiable; null while its refresh is not known.
    /// </summary>
    public static TimeSpan? Window(TimeSpan isolation, int lagPolls, TimeSpan? refresh) =>
        refresh is { } interval ? (interval * (2 + lagPolls) > isolation ? interval * (2 + lagPolls) : isolation) : null;

    /// <summary>
    /// Previous is the value in force one window before t0, Before the last report at or before t0,
    /// After the (1 + lag)-th report after t0 (the meter may lag by a poll), only within the window.
    /// </summary>
    public static PhaseStepMeasurement Measure(IReadOnlyList<ReportPoint> series, DateTimeOffset t0, TimeSpan window, int lagPolls, double sigma)
    {
        ArgumentNullException.ThrowIfNull(series);
        double? previous = null, before = null, after = null;
        var seen = 0;
        foreach (var point in series)
        {
            if (point.At <= t0 - window)
            {
                previous = point.Value;
            }

            if (point.At <= t0)
            {
                before = point.Value;
            }
            else if (point.At <= t0 + window && after is null && ++seen == 1 + lagPolls)
            {
                after = point.Value;
            }
        }

        return new PhaseStepMeasurement(previous, before, after, sigma);
    }

    /// <summary>Whether a draw (or any other series) changed by more than <paramref name="threshold"/> within the window.</summary>
    public static bool Changed(IReadOnlyList<ReportPoint> series, DateTimeOffset t0, TimeSpan window, double threshold)
    {
        ArgumentNullException.ThrowIfNull(series);
        var values = series.Where(p => p.At > t0 - window && p.At <= t0 + window).Select(p => p.Value).ToList();
        var inForce = series.LastOrDefault(p => p.At <= t0 - window);
        if (inForce != default)
        {
            values.Add(inForce.Value);
        }

        return values.Count > 1 && values.Max() - values.Min() > threshold;
    }
}

/// <summary>The import-positive value of a raw Home Assistant state for a mapped meter term; null when not a number.</summary>
public static class MeterValues
{
    public static double? Read(string? state, MeterTerm term)
    {
        ArgumentNullException.ThrowIfNull(term);
        if (state is null || !double.TryParse(state, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw) || !double.IsFinite(raw))
        {
            return null;
        }

        double factor;
        try
        {
            factor = SignalUnits.Factor(term.Unit);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }

        var value = raw * factor;
        return term.Sign switch
        {
            "positive_export" => -value,
            "unsigned" => Math.Abs(value),
            _ => value,
        };
    }

    public static string SignName(SignConvention? sign) => sign switch
    {
        SignConvention.PositiveExport => "positive_export",
        SignConvention.Unsigned => "unsigned",
        _ => "positive_import",
    };
}

/// <summary>
/// The observations, bounded per switched entity: the guided watch's, the running add-on's own and
/// the active self-test's land here. Persisted in /data (phase-evidence.json) by a writer of its own
/// (<see cref="Changed"/> wakes it; never a file write on the guard thread), so evidence survives a
/// restart; loaded before the first step.
/// </summary>
public sealed class PhaseEvidenceStore(int perEntity)
{
    private readonly object _gate = new();
    private readonly Dictionary<EntityId, List<PhaseObservation>> _observations = [];

    /// <summary>Raised after an observation was added (outside the lock); handlers must only note it, never write inline.</summary>
    public event EventHandler? Changed;

    public void Add(PhaseObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (_gate)
        {
            if (!_observations.TryGetValue(observation.StateEntity, out var list))
            {
                list = [];
                _observations[observation.StateEntity] = list;
            }

            list.Add(observation);
            if (list.Count > perEntity)
            {
                list.RemoveRange(0, list.Count - perEntity);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Every observation now, per switched entity, oldest first: what the evidence file holds.</summary>
    public ImmutableDictionary<EntityId, ImmutableArray<PhaseObservation>> Snapshot()
    {
        lock (_gate)
        {
            return _observations.ToImmutableDictionary(o => o.Key, o => o.Value.ToImmutableArray());
        }
    }

    /// <summary>The observations persisted at the last stop, applied before anything is added; bounded like new ones.</summary>
    public void Load(IReadOnlyDictionary<EntityId, ImmutableArray<PhaseObservation>> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        lock (_gate)
        {
            foreach (var (entity, list) in observations)
            {
                var kept = list.Where(o => o.StateEntity == entity).OrderBy(o => o.At).ToList();
                if (kept.Count > perEntity)
                {
                    kept.RemoveRange(0, kept.Count - perEntity);
                }

                _observations[entity] = kept;
            }
        }
    }

    public ImmutableArray<PhaseObservation> For(EntityId stateEntity)
    {
        lock (_gate)
        {
            return _observations.TryGetValue(stateEntity, out var list) ? [.. list] : [];
        }
    }
}

public static class PhaseEvidence
{
    /// <summary>
    /// The verdict for one declaration: the observations of its state entity that were measured with
    /// this meter mapping, each classified against the declared phase and rating, then summarized.
    /// </summary>
    /// <param name="meter">The draft's meter terms per phase, power and current; an observation applies only when its terms equal these.</param>
    public static PhaseJudgement Judge(
        IEnumerable<PhaseObservation> observations,
        Phase expected,
        double? ratedPowerW,
        double? ratedCurrentA,
        ImmutableDictionary<Phase, MeterTerm>? powerTerms,
        ImmutableDictionary<Phase, MeterTerm>? currentTerms,
        PhaseStepParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var judged = ImmutableArray.CreateBuilder<JudgedObservation>();
        foreach (var observation in observations)
        {
            var (terms, rating) = observation.Unit == "W" ? (powerTerms, ratedPowerW) : (currentTerms, ratedCurrentA);
            if (terms is null || rating is not { } step || !Same(terms, observation.Meter))
            {
                continue;
            }

            var result = PhaseStepClassifier.Classify(
                new PhaseStepInput(expected, observation.Direction, step, observation.Phases, observation.Isolated, observation.Unsigned),
                parameters);
            judged.Add(new JudgedObservation(observation, result));
        }

        return new PhaseJudgement(PhaseStepClassifier.Summarize([.. judged.Select(j => j.Result)], parameters), judged.ToImmutable());
    }

    public static bool Same(ImmutableDictionary<Phase, MeterTerm> a, ImmutableDictionary<Phase, MeterTerm> b) =>
        a.Count == b.Count && a.All(t => b.TryGetValue(t.Key, out var other) && other == t.Value);

    /// <summary>The meter terms a configuration maps for one role on every phase (power, or current); null when a phase has none.</summary>
    public static ImmutableDictionary<Phase, MeterTerm>? Terms(Configuration.SwitchboardConfiguration configuration, SignalRole role)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var builder = ImmutableDictionary.CreateBuilder<Phase, MeterTerm>();
        foreach (var phase in configuration.Connection.Phases)
        {
            if (configuration.Signals.FirstOrDefault(s => s.Role == role && s.Phase == phase) is not { } signal)
            {
                return null;
            }

            builder[phase] = new MeterTerm(signal.Entity, signal.Unit ?? string.Empty, MeterValues.SignName(signal.Sign));
        }

        return builder.ToImmutable();
    }
}
