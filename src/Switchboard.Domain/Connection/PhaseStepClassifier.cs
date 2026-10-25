using System.Collections.Immutable;
using Switchboard.Domain.Signals;

namespace Switchboard.Domain.Connection;

/// <summary>The outcome of one passive phase-assignment observation (CONN-05).</summary>
public enum PhaseStepVerdict
{
    /// <summary>The declared phase, and only it, moved by about the device's rating.</summary>
    Confirmed,

    /// <summary>Another phase, and only it, moved by about the device's rating: the declaration or the meter's phase entities are swapped.</summary>
    Mismatch,

    /// <summary>Nothing can be concluded; never shown as a pass.</summary>
    Inconclusive,
}

/// <summary>Why an observation proves nothing.</summary>
public enum InconclusiveReason
{
    /// <summary>A phase did not report around the switching (or its refresh is not known yet).</summary>
    MeterSilent,

    /// <summary>Another declared device switched, or a draw changed, within the window.</summary>
    NotIsolated,

    /// <summary>A phase was already moving before the switching.</summary>
    UnsettledBefore,

    /// <summary>The device is too small to tell from the meter's noise.</summary>
    StepBelowNoise,

    /// <summary>A phase moved against the switching (an unsigned current under export looks like this).</summary>
    WrongDirection,

    /// <summary>Nothing moved: a heater whose thermostat is open draws nothing.</summary>
    NoStep,

    /// <summary>Several phases moved, or one moved by an amount that is neither quiet nor a step.</summary>
    ForeignStep,

    /// <summary>One phase stepped, by an amount far from the device's rating.</summary>
    RatingDiffers,

    /// <summary>The watch ended without a switching.</summary>
    TimedOut,

    /// <summary>Confirmations and mismatches of the same device both exist: a coincidence, never a verdict.</summary>
    Conflicting,

    /// <summary>
    /// Too few agreeing switchings so far (fewer than min_concordant, or not both a switch on and a
    /// switch off): one step can be a coincident load, so it confirms nothing and refuses nothing.
    /// </summary>
    Unconfirmed,
}

/// <summary>
/// One phase around a switching at t0, import positive (a magnitude for an unsigned current).
/// <see cref="Previous"/> is the value one window before t0, <see cref="Before"/> the last report
/// at or before t0, <see cref="After"/> the report after t0 that is late enough to include the
/// meter's own lag; a null means the phase did not report there.
/// </summary>
public sealed record PhaseStepMeasurement(double? Previous, double? Before, double? After, double Sigma);

/// <param name="Direction">+1 when the device was switched on, −1 when it was switched off.</param>
/// <param name="ExpectedStep">The device's rating in the measurement's unit (W, or A when the meter maps current only).</param>
/// <param name="Isolated">No other declared device switched and no draw changed within the window.</param>
/// <param name="Unsigned">The values are magnitudes of an unsigned current.</param>
public sealed record PhaseStepInput(
    Phase ExpectedPhase,
    int Direction,
    double ExpectedStep,
    ImmutableDictionary<Phase, PhaseStepMeasurement> Phases,
    bool Isolated,
    bool Unsigned);

/// <summary>Thresholds of the classification; all come from the shipped setup defaults.</summary>
/// <param name="StepMinFraction">A phase moved when its change reaches this fraction of the rating (and k·σ).</param>
/// <param name="QuietMaxFraction">A phase is quiet while its change stays within this fraction of the rating (or k·σ).</param>
/// <param name="RatingTolerance">A single step farther than this fraction from the rating is not the device.</param>
/// <param name="SigmaMultiplier">k: how many standard deviations of measurement noise a change must exceed.</param>
/// <param name="Dominance">When confirmations and mismatches of one device both exist, the side with at least this many times the other's count decides; otherwise the result is conflicting.</param>
/// <param name="MinConcordant">A verdict needs at least this many agreeing observations, among them a switch on and a switch off.</param>
public sealed record PhaseStepParameters(double StepMinFraction, double QuietMaxFraction, double RatingTolerance, double SigmaMultiplier, double Dominance, int MinConcordant);

/// <param name="Observed">The phase that stepped, when exactly one did.</param>
/// <param name="Deltas">After − Before per phase that reported, import positive.</param>
/// <param name="RatingRatio">|step| / rating of the phase that stepped.</param>
/// <param name="Direction">+1 for a switch on, −1 for a switch off: a verdict needs both.</param>
public sealed record PhaseStepResult(
    PhaseStepVerdict Verdict,
    Phase? Observed,
    InconclusiveReason? Reason,
    ImmutableDictionary<Phase, double> Deltas,
    double? RatingRatio,
    int Direction);

/// <summary>The verdict over every observation of one device.</summary>
/// <param name="Observed">The phase the device really moves, for a mismatch; for <see cref="InconclusiveReason.Unconfirmed"/> the phase the agreeing observations point to.</param>
public sealed record PhaseStepSummary(
    PhaseStepVerdict Verdict,
    Phase? Observed,
    InconclusiveReason? Reason,
    int Confirmed,
    int Mismatched,
    int Inconclusive);

/// <summary>
/// The passive phase self-test (CONN-05): someone switches one declared device (Switchboard never
/// does), and the meter's per-phase step around that moment says which phase it loads. Pure: the
/// caller resolves the reports around the switching.
/// <code>
/// T_step  = max(step_min_fraction · E, k · σ_p)
/// T_quiet = max(quiet_max_fraction · E, k · σ_p)
/// 1. a phase without Previous, Before or After      → MeterSilent
/// 2. not isolated                                     → NotIsolated
/// 3. |Before − Previous| &gt; T_quiet on any phase      → UnsettledBefore
/// 4. step_min_fraction · E ≤ k · σ on the declared phase → StepBelowNoise
/// 5. any phase moving against the switching by ≥ T_step  → WrongDirection
/// 6. nothing moved, every phase quiet                 → NoStep
/// 7. exactly one phase moved, the others quiet:
///      |ratio − 1| &gt; rating_tolerance → RatingDiffers, else Confirmed (declared) or Mismatch (other)
/// 8. anything else                                    → ForeignStep
/// </code>
/// An inconclusive observation is never a pass.
/// </summary>
public static class PhaseStepClassifier
{
    public static PhaseStepResult Classify(PhaseStepInput input, PhaseStepParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(parameters);
        var deltas = input.Phases
            .Where(p => p.Value.Before is not null && p.Value.After is not null)
            .ToImmutableDictionary(p => p.Key, p => p.Value.After!.Value - p.Value.Before!.Value);
        var direction = input.Direction >= 0 ? 1 : -1;
        PhaseStepResult Inconclusive(InconclusiveReason reason) => new(PhaseStepVerdict.Inconclusive, null, reason, deltas, null, direction);

        if (input.Phases.IsEmpty || input.Phases.Values.Any(m => m.Previous is null || m.Before is null || m.After is null)
            || !input.Phases.ContainsKey(input.ExpectedPhase))
        {
            return Inconclusive(InconclusiveReason.MeterSilent);
        }

        if (!input.Isolated)
        {
            return Inconclusive(InconclusiveReason.NotIsolated);
        }

        var expected = Math.Abs(input.ExpectedStep);
        double StepThreshold(PhaseStepMeasurement m) => Math.Max(parameters.StepMinFraction * expected, parameters.SigmaMultiplier * m.Sigma);
        double QuietThreshold(PhaseStepMeasurement m) => Math.Max(parameters.QuietMaxFraction * expected, parameters.SigmaMultiplier * m.Sigma);

        if (input.Phases.Values.Any(m => Math.Abs(m.Before!.Value - m.Previous!.Value) > QuietThreshold(m)))
        {
            return Inconclusive(InconclusiveReason.UnsettledBefore);
        }

        if (parameters.StepMinFraction * expected <= parameters.SigmaMultiplier * input.Phases[input.ExpectedPhase].Sigma)
        {
            return Inconclusive(InconclusiveReason.StepBelowNoise);
        }

        if (input.Phases.Any(p => direction * deltas[p.Key] <= -StepThreshold(p.Value)))
        {
            return Inconclusive(InconclusiveReason.WrongDirection);
        }

        var moved = input.Phases.Where(p => direction * deltas[p.Key] >= StepThreshold(p.Value)).Select(p => p.Key).ToList();
        var quiet = input.Phases.Count(p => Math.Abs(deltas[p.Key]) <= QuietThreshold(p.Value));
        if (moved.Count == 0 && quiet == input.Phases.Count)
        {
            return Inconclusive(InconclusiveReason.NoStep);
        }

        if (moved.Count == 1 && quiet == input.Phases.Count - 1)
        {
            var observed = moved[0];
            var ratio = Math.Abs(deltas[observed]) / expected;
            if (Math.Abs(ratio - 1) > parameters.RatingTolerance)
            {
                return new PhaseStepResult(PhaseStepVerdict.Inconclusive, observed, InconclusiveReason.RatingDiffers, deltas, ratio, direction);
            }

            return new PhaseStepResult(
                observed == input.ExpectedPhase ? PhaseStepVerdict.Confirmed : PhaseStepVerdict.Mismatch,
                observed,
                null,
                deltas,
                ratio,
                direction);
        }

        return Inconclusive(InconclusiveReason.ForeignStep);
    }

    /// <summary>
    /// Over all observations of one device. A coincidence (a kettle of a similar size on another
    /// phase during one switching) can produce a single mismatch among confirmations, or a single
    /// confirmation among the mismatches of a real swap. So when both exist, the side with at least
    /// <see cref="PhaseStepParameters.Dominance"/> times the other's count decides, and anything
    /// closer is conflicting (inconclusive, never a pass and never a refusal). The deciding side then
    /// needs <see cref="PhaseStepParameters.MinConcordant"/> agreeing observations (a mismatch: on
    /// the same phase) including a switch on and a switch off: a real assignment steps both ways,
    /// while a coincident load almost never mirrors both. Fewer is unconfirmed, still inconclusive.
    /// </summary>
    public static PhaseStepSummary Summarize(IReadOnlyCollection<PhaseStepResult> results, PhaseStepParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(parameters);
        var confirmed = results.Where(r => r.Verdict == PhaseStepVerdict.Confirmed).ToList();
        var mismatched = results.Where(r => r.Verdict == PhaseStepVerdict.Mismatch).ToList();
        var inconclusive = results.Count - confirmed.Count - mismatched.Count;
        PhaseStepSummary Decide(PhaseStepVerdict verdict, IReadOnlyList<PhaseStepResult> agreeing, Phase? observed) =>
            agreeing.Count >= parameters.MinConcordant && agreeing.Any(r => r.Direction > 0) && agreeing.Any(r => r.Direction < 0)
                ? new PhaseStepSummary(verdict, verdict == PhaseStepVerdict.Mismatch ? observed : null, null, confirmed.Count, mismatched.Count, inconclusive)
                : new PhaseStepSummary(PhaseStepVerdict.Inconclusive, observed, InconclusiveReason.Unconfirmed, confirmed.Count, mismatched.Count, inconclusive);

        if (mismatched.Count > 0 && confirmed.Count > 0
            && mismatched.Count < parameters.Dominance * confirmed.Count && confirmed.Count < parameters.Dominance * mismatched.Count)
        {
            return new PhaseStepSummary(PhaseStepVerdict.Inconclusive, null, InconclusiveReason.Conflicting, confirmed.Count, mismatched.Count, inconclusive);
        }

        if (confirmed.Count > mismatched.Count)
        {
            return Decide(PhaseStepVerdict.Confirmed, confirmed, confirmed[0].Observed);
        }

        if (mismatched.Count > 0)
        {
            var observed = mismatched.GroupBy(r => r.Observed).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
            return Decide(PhaseStepVerdict.Mismatch, [.. mismatched.Where(r => r.Observed == observed)], observed);
        }

        var reason = results.Count == 0
            ? InconclusiveReason.TimedOut
            : results.GroupBy(r => r.Reason).OrderByDescending(g => g.Count()).First().Key ?? InconclusiveReason.ForeignStep;
        return new PhaseStepSummary(PhaseStepVerdict.Inconclusive, null, reason, 0, 0, inconclusive);
    }
}
