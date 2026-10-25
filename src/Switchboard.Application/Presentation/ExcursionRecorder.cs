using System.Collections.Immutable;
using Switchboard.Application.Decisions;
using Switchboard.Application.Guard;
using Switchboard.Application.Options;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Presentation;

/// <summary>One phase current sample as the meter reported it (signed, import positive).</summary>
public sealed record ExcursionSample(DateTimeOffset At, Amperes Current);

/// <summary>A decision recorded around an excursion: a shed, a reserve request, a foreign load event.</summary>
public sealed record ExcursionDecision(string Id, DateTimeOffset At, DecisionAction Action, ReasonCode Reason, DeviceName? Device, bool Commanded);

/// <summary>
/// A recorded excursion above a phase's reserve threshold (or an export above its continuous
/// current): the samples around it and the decisions taken. <see cref="Decimated"/> says the
/// samples were thinned to the cap (minimum and maximum per time slot, the peak and every decision
/// instant always kept).
/// </summary>
public sealed record Excursion(
    Phase Phase,
    DateTimeOffset Start,
    DateTimeOffset? End,
    Amperes Peak,
    DateTimeOffset PeakAt,
    Amperes ReserveThreshold,
    Amperes Rating,
    Amperes Continuous,
    bool Decimated,
    ImmutableArray<ExcursionSample> Samples,
    ImmutableArray<ExcursionDecision> Decisions)
{
    public bool Active => End is null;

    /// <summary>The peak as a multiple of the breaker rating: what makes one excursion worse than another.</summary>
    public double PeakMultiple => Rating.Value > 0 ? Peak.Value / Rating.Value : 0;
}

/// <summary>A phase's most recent (or ongoing) excursion, and its worst within the retention window.</summary>
public sealed record PhaseExcursions(Phase Phase, Excursion? Last, Excursion? Worst);

/// <summary>What excursions.json holds.</summary>
public sealed record ExcursionState(DateTimeOffset? SavedAt, ImmutableArray<PhaseExcursions> Phases);

/// <param name="Version">Grows with every change; the writer saves when it moved.</param>
public sealed record ExcursionsSnapshot(ImmutableArray<PhaseExcursions> Phases, long Version)
{
    public static ExcursionsSnapshot Empty { get; } = new([], 0);

    public ExcursionState ToState(DateTimeOffset savedAt) => new(savedAt, Phases);
}

/// <summary>
/// Records, per phase, the excursion above the reserve threshold for the Limits chart: it starts on
/// a new sample whose signed measured current is above the reserve threshold (or whose magnitude is
/// above the continuous current: an export overload), carries the samples of the pre-roll before
/// it, and ends once the current stayed at or below for the post-roll. Samples are taken only on a
/// new report of the phase current. Shed decisions on the phase (switch off, reduce to minimum, a
/// lowered setpoint, a deferred reduction, for a tier, a low budget or the reserve threshold) and the
/// phase's reserve requests, increase stops, export refusals, safe states and foreign load events
/// are attached, those of the pre-roll included. Two records are kept per phase: the last (or
/// ongoing) one and the worst within the retention window, so routine excursions never displace the
/// one that matters. Not thread-safe: the hub calls it under its lock.
/// </summary>
public sealed class ExcursionRecorder
{
    private static readonly ImmutableHashSet<DecisionAction> ShedActions =
        [DecisionAction.SwitchOff, DecisionAction.ReduceToMinimum, DecisionAction.SetSetpoint, DecisionAction.Deferred];

    private static readonly ImmutableHashSet<ReasonCode> ShedReasons =
        [ReasonCode.GuardTierExceeded, ReasonCode.GuardBudgetLow, ReasonCode.ReserveThreshold, ReasonCode.ForeignLoad];

    private static readonly ImmutableHashSet<DecisionAction> PhaseActions =
        [DecisionAction.RequestReduction, DecisionAction.StopIncreases, DecisionAction.CannotReduceExporting, DecisionAction.SafeStateEntered, DecisionAction.ForeignLoadDetected];

    private readonly StatisticsOptions _options;
    private readonly Dictionary<Phase, PhaseState> _phases = [];
    private ExcursionsSnapshot _snapshot = ExcursionsSnapshot.Empty;
    private long _version;

    public ExcursionRecorder(StatisticsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Raised (under the caller's lock) when an excursion ends: worth a save at once.</summary>
    public bool EndedSinceLastAsk { get; private set; }

    /// <summary>The ongoing excursion of a phase, if any.</summary>
    public Excursion? Active(Phase phase) => _phases.TryGetValue(phase, out var state) && state.Open is not null ? state.Last : null;

    public void Observe(GuardStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var now = step.Evaluation.At;
        var status = step.Evaluation.Status;
        var setup = step.Evaluation.Setup;
        if (status.Breaker is not { } breaker)
        {
            return;
        }

        var changed = false;
        foreach (var guard in status.Phases)
        {
            // A phase seen for the first time is listed at once (with no excursion yet).
            changed |= !_phases.ContainsKey(guard.Phase);
            var state = Get(guard.Phase);
            var decisions = step.Decisions.Where(d => Belongs(d, guard.Phase, setup)).Select(ToRecord).ToList();
            ExcursionSample? sample = guard.Current is { } current && guard.ReportedAt is { } reported && reported != state.LastReported
                ? new ExcursionSample(reported, current)
                : null;
            if (sample is not null)
            {
                state.LastReported = sample.At;
            }

            changed |= state.Observe(sample, decisions, guard, breaker.Rating, now, _options);
            if (state.EndedNow)
            {
                EndedSinceLastAsk = true;
                state.EndedNow = false;
            }

            changed |= state.ForgetOldWorst(now - _options.Retention);
        }

        if (changed)
        {
            Publish();
        }
    }

    public ExcursionsSnapshot Snapshot() => _snapshot;

    /// <summary>Clears the "an excursion ended" flag and returns whether it was set.</summary>
    public bool TakeEnded()
    {
        var ended = EndedSinceLastAsk;
        EndedSinceLastAsk = false;
        return ended;
    }

    /// <summary>
    /// Takes over what excursions.json held. An excursion that was still open at the last save ended
    /// with that run (nothing of it can be continued); a worst one older than the retention window is dropped.
    /// </summary>
    public void Restore(ExcursionState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        foreach (var phase in state.Phases)
        {
            var target = Get(phase.Phase);
            target.Last = Closed(phase.Last);
            target.Worst = Closed(phase.Worst);
            target.ForgetOldWorst(now - _options.Retention);
        }

        Publish();
    }

    private static Excursion? Closed(Excursion? excursion) =>
        excursion is { End: null } open ? open with { End = open.Samples.IsEmpty ? open.Start : open.Samples[^1].At } : excursion;

    private static ExcursionDecision ToRecord(Decision decision) =>
        new(decision.Id, decision.At, decision.Action, decision.Reason, decision.Device, decision.Commanded);

    /// <summary>A decision about this phase: a shed of a device on it, or one of the phase's own events (never a circuit group's).</summary>
    private static bool Belongs(Decision decision, Phase phase, Domain.Guard.ProtectionSetup? setup)
    {
        if (decision.Scope == DecisionScope.Device && decision.Device is { } device)
        {
            var on = setup?.Device(device) is { } spec ? spec.IsOn(phase) : decision.Phase == phase;
            return on && ShedActions.Contains(decision.Action) && ShedReasons.Contains(decision.Reason);
        }

        return decision.Scope == DecisionScope.Phase && decision.Phase == phase && PhaseActions.Contains(decision.Action);
    }

    private PhaseState Get(Phase phase)
    {
        if (!_phases.TryGetValue(phase, out var state))
        {
            state = new PhaseState(phase);
            _phases[phase] = state;
        }

        return state;
    }

    private void Publish()
    {
        _version++;
        _snapshot = new ExcursionsSnapshot([.. _phases.Values.OrderBy(p => p.Phase).Select(p => new PhaseExcursions(p.Phase, p.Last, p.Worst))], _version);
    }

    /// <summary>
    /// Thins samples to at most <paramref name="max"/>: per equal time slot the lowest and the highest
    /// sample, plus the peak and the sample nearest each decision instant, which are never dropped.
    /// </summary>
    public static List<ExcursionSample> Decimate(List<ExcursionSample> samples, IReadOnlyCollection<DateTimeOffset> decisionTimes, int max)
    {
        if (samples.Count <= max)
        {
            return samples;
        }

        var peak = samples.MaxBy(s => s.Current.Abs().Value)!;
        var pinned = new HashSet<ExcursionSample> { peak };
        foreach (var time in decisionTimes)
        {
            pinned.Add(samples.MinBy(s => (s.At - time).Duration())!);
        }

        if (pinned.Count > max / 2)
        {
            // More decision instants than room: keep the peak and an even spread of the others.
            var others = pinned.Where(p => p != peak).OrderBy(p => p.At).ToList();
            var keep = Math.Max(0, (max / 2) - 1);
            pinned = [peak, .. Enumerable.Range(0, keep).Select(i => others[(int)((long)i * others.Count / Math.Max(1, keep))])];
        }

        var slots = Math.Max(1, (max - pinned.Count) / 2);
        var first = samples[0].At;
        var span = Math.Max(1, (samples[^1].At - first).Ticks);
        var kept = new HashSet<ExcursionSample>(pinned);
        foreach (var slot in samples.GroupBy(s => Math.Min(slots - 1, (int)((s.At - first).Ticks * slots / span))))
        {
            kept.Add(slot.MinBy(s => s.Current.Value)!);
            kept.Add(slot.MaxBy(s => s.Current.Value)!);
        }

        var result = samples.Where(kept.Contains).ToList();
        while (result.Count > max)
        {
            // Rounding left a few too many: drop every other sample that is not pinned.
            var removable = result.Where(s => !pinned.Contains(s)).Where((_, i) => i % 2 == 1).ToHashSet();
            if (removable.Count == 0)
            {
                break;
            }

            result = [.. result.Where(s => !removable.Contains(s))];
        }

        return result;
    }

    private sealed class PhaseState(Phase phase)
    {
        private readonly List<ExcursionSample> _preSamples = [];
        private readonly List<ExcursionDecision> _preDecisions = [];

        public Phase Phase => phase;

        public DateTimeOffset? LastReported { get; set; }

        public Excursion? Last { get; set; }

        public Excursion? Worst { get; set; }

        public OpenExcursion? Open { get; private set; }

        public bool EndedNow { get; set; }

        public bool Observe(ExcursionSample? sample, List<ExcursionDecision> decisions, PhaseGuardStatus guard, Amperes rating, DateTimeOffset now, StatisticsOptions options)
        {
            var over = sample is not null && guard.DataValid && (sample.Current > guard.ReserveThreshold || sample.Current.Abs() > guard.Continuous);
            if (Open is null)
            {
                if (sample is not null)
                {
                    _preSamples.Add(sample);
                }

                _preDecisions.AddRange(decisions);
                _preSamples.RemoveAll(s => s.At < (sample?.At ?? now) - options.ExcursionPreRoll);
                _preDecisions.RemoveAll(d => d.At < now - options.ExcursionPreRoll);
                if (!over)
                {
                    return false;
                }

                Open = new OpenExcursion(sample!.At, guard.ReserveThreshold, rating, guard.Continuous, [.. _preSamples], [.. _preDecisions], sample.At);
                _preSamples.Clear();
                _preDecisions.Clear();
                Open.Trim(options);
                Rebuild();
                return true;
            }

            var changed = false;
            if (sample is not null)
            {
                Open.Samples.Add(sample);
                Open.LastOver = over ? sample.At : Open.LastOver;
                Open.FirstBelow = over ? null : Open.FirstBelow ?? sample.At;
                changed = true;
            }

            if (decisions.Count > 0)
            {
                Open.Decisions.AddRange(decisions);
                changed = true;
            }

            if (changed)
            {
                Open.Trim(options);
            }

            if (now - Open.LastOver >= options.ExcursionPostRoll && Open.FirstBelow is not null)
            {
                Rebuild(end: Open.FirstBelow);
                Open = null;
                EndedNow = true;
                return true;
            }

            if (changed)
            {
                Rebuild();
            }

            return changed;
        }

        /// <summary>Drops a worst excursion that ended before the retention window; the last one then stands in.</summary>
        public bool ForgetOldWorst(DateTimeOffset cutoff)
        {
            if (Worst is { End: { } end } && end < cutoff)
            {
                Worst = Last is { } last && (last.End is null || last.End >= cutoff) ? last : null;
                return true;
            }

            return false;
        }

        private void Rebuild(DateTimeOffset? end = null)
        {
            var open = Open!;
            var peak = open.Samples.Where(s => s.At >= open.Start).DefaultIfEmpty(open.Samples[^1]).MaxBy(s => s.Current.Abs().Value)!;
            var built = new Excursion(
                Phase,
                open.Start,
                end,
                peak.Current.Abs(),
                peak.At,
                open.ReserveThreshold,
                open.Rating,
                open.Continuous,
                open.Decimated,
                [.. open.Samples],
                [.. open.Decisions]);
            var sameAsWorst = Worst is { } worst && worst.Start == built.Start;
            Last = built;
            if (Worst is null || sameAsWorst || built.PeakMultiple > Worst.PeakMultiple)
            {
                Worst = built;
            }
        }
    }

    private sealed class OpenExcursion(
        DateTimeOffset start,
        Amperes reserveThreshold,
        Amperes rating,
        Amperes continuous,
        List<ExcursionSample> samples,
        List<ExcursionDecision> decisions,
        DateTimeOffset lastOver)
    {
        public DateTimeOffset Start => start;

        public Amperes ReserveThreshold => reserveThreshold;

        public Amperes Rating => rating;

        public Amperes Continuous => continuous;

        public List<ExcursionSample> Samples { get; private set; } = samples;

        public List<ExcursionDecision> Decisions => decisions;

        public DateTimeOffset LastOver { get; set; } = lastOver;

        public DateTimeOffset? FirstBelow { get; set; }

        public bool Decimated { get; private set; }

        public void Trim(StatisticsOptions options)
        {
            if (Decisions.Count > options.ExcursionMaxDecisions)
            {
                KeepDecisions(options.ExcursionMaxDecisions);
            }

            if (Samples.Count > options.ExcursionMaxSamples)
            {
                // Thinned to three quarters of the cap, so it is not redone on every new sample.
                Samples = Decimate(Samples, [.. Decisions.Select(d => d.At)], options.ExcursionMaxSamples - (options.ExcursionMaxSamples / 4));
                Decimated = true;
            }
        }

        /// <summary>
        /// Over the cap, never just the oldest: the decisions taken on the peak sample (from the peak
        /// until the next sample) always stay, since that is the moment the chart annotates; of the
        /// rest the first and the newest stay, half each, and the middle goes.
        /// </summary>
        private void KeepDecisions(int max)
        {
            var peak = Samples.Where(s => s.At >= Start).DefaultIfEmpty(Samples[^1]).MaxBy(s => s.Current.Abs().Value)!;
            var next = Samples.FirstOrDefault(s => s.At > peak.At)?.At;
            var atPeak = Decisions.Where(d => d.At >= peak.At && (next is null || d.At < next)).Take(max).ToHashSet();
            var rest = Decisions.Where(d => !atPeak.Contains(d)).ToList();
            var room = max - atPeak.Count;
            var newest = room / 2;
            var kept = new HashSet<ExcursionDecision>(atPeak);
            kept.UnionWith(rest.Take(room - newest));
            kept.UnionWith(rest.Skip(rest.Count - newest));
            var result = Decisions.Where(kept.Contains).ToList();
            Decisions.Clear();
            Decisions.AddRange(result);
        }
    }
}
