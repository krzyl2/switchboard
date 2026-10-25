using System.Collections.Immutable;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Signals;

namespace Switchboard.Tests.Domain;

/// <summary>
/// CONN-05, passive: a swapped phase assignment makes protection shed on the phase that is not
/// overloaded (07-PITFALLS, the worst misconfiguration), so the self-test must name the phase that
/// really rose, and must never call anything a pass that it cannot tell apart from a coincidence.
/// Parameters as shipped in setup-defaults.yaml: step 0.5, quiet 0.25, rating ±0.25, k = 3,
/// dominance 3, min_concordant 2.
/// </summary>
public sealed class PhaseStepClassifierTests
{
    private static readonly PhaseStepParameters Parameters = new(0.5, 0.25, 0.25, 3, 3, 2);
    private const double Heater = 1700;
    private const double Sigma = 25;

    [Fact]
    public void Declared_phase_step_matching_rating_confirms()
    {
        var result = Classify(Phase.L2, l2: 1690);

        Assert.Equal((PhaseStepVerdict.Confirmed, Phase.L2), (result.Verdict, result.Observed));
        Assert.Equal(1690 / Heater, result.RatingRatio!.Value, 6);
    }

    [Fact]
    public void Declared_L2_step_on_L3_is_mismatch_naming_L3()
    {
        // The swapped L2/L3 case: the device is declared on L2, the meter's L3 rose by its rating.
        var result = Classify(Phase.L2, l3: 1705);

        Assert.Equal((PhaseStepVerdict.Mismatch, Phase.L3), (result.Verdict, result.Observed));
    }

    [Fact]
    public void Switching_off_steps_down_on_the_same_phase()
    {
        Assert.Equal(PhaseStepVerdict.Confirmed, Classify(Phase.L1, direction: -1, l1: -1680).Verdict);
    }

    [Fact]
    public void No_step_is_inconclusive_never_pass()
    {
        // A heater whose thermostat is open draws nothing when switched on.
        var result = Classify(Phase.L2);

        Assert.Equal((PhaseStepVerdict.Inconclusive, InconclusiveReason.NoStep), (result.Verdict, result.Reason));
    }

    [Fact]
    public void Two_phases_moving_is_foreign_step()
    {
        // A kettle switched on elsewhere in the same window: which step is the device's is unknown.
        Assert.Equal(InconclusiveReason.ForeignStep, Classify(Phase.L2, l2: 1700, l1: 2000).Reason);
    }

    [Fact]
    public void A_change_between_quiet_and_step_is_foreign_too()
    {
        Assert.Equal(InconclusiveReason.ForeignStep, Classify(Phase.L2, l2: 1700, l1: 600).Reason);
    }

    [Fact]
    public void Step_below_k_sigma_inconclusive()
    {
        // 150 W: half of it (75 W) does not exceed 3 sigma of a 25 W meter noise.
        var result = Classify(Phase.L2, expected: 150, l2: 150);

        Assert.Equal(InconclusiveReason.StepBelowNoise, result.Reason);
    }

    [Fact]
    public void Rating_far_off_inconclusive_not_confirmed()
    {
        // One phase stepped by 3 kW for a 1.7 kW heater: something else, even on the declared phase.
        var result = Classify(Phase.L2, l2: 3000);

        Assert.Equal((PhaseStepVerdict.Inconclusive, InconclusiveReason.RatingDiffers, Phase.L2), (result.Verdict, result.Reason, result.Observed));
    }

    [Fact]
    public void Unsigned_current_falling_under_export_inconclusive()
    {
        // While the phase exports, a load switched on makes the magnitude of the net current fall:
        // without a sign it would read as a step of the wrong direction, never as a confirmation.
        var phases = Phases(0.1, (Phase.L3, 6.96, 0.43));
        var result = PhaseStepClassifier.Classify(new PhaseStepInput(Phase.L3, 1, 7.4, phases, true, true), Parameters);

        Assert.Equal(InconclusiveReason.WrongDirection, result.Reason);
    }

    [Fact]
    public void Unsettled_before_inconclusive()
    {
        var phases = Phases(Sigma, (Phase.L2, 400, 2100)).SetItem(Phase.L1, new PhaseStepMeasurement(500, 1500, 1500, Sigma));
        var result = PhaseStepClassifier.Classify(new PhaseStepInput(Phase.L2, 1, Heater, phases, true, false), Parameters);

        Assert.Equal(InconclusiveReason.UnsettledBefore, result.Reason);
    }

    [Fact]
    public void Meter_silent_inconclusive()
    {
        var phases = Phases(Sigma, (Phase.L2, 400, 2100)).SetItem(Phase.L3, new PhaseStepMeasurement(600, 600, null, Sigma));
        var result = PhaseStepClassifier.Classify(new PhaseStepInput(Phase.L2, 1, Heater, phases, true, false), Parameters);

        Assert.Equal(InconclusiveReason.MeterSilent, result.Reason);
    }

    [Fact]
    public void Not_isolated_inconclusive()
    {
        var phases = Phases(Sigma, (Phase.L2, 400, 2100));
        Assert.Equal(InconclusiveReason.NotIsolated, PhaseStepClassifier.Classify(new PhaseStepInput(Phase.L2, 1, Heater, phases, false, false), Parameters).Reason);
    }

    [Fact]
    public void A_mismatch_seen_on_and_off_outranks_inconclusive_observations()
    {
        var summary = PhaseStepClassifier.Summarize(
            [Classify(Phase.L2), Classify(Phase.L2, l3: 1700), Classify(Phase.L2, direction: -1, l3: -1690), Classify(Phase.L2, l2: 1700, l1: 2000)],
            Parameters);

        Assert.Equal((PhaseStepVerdict.Mismatch, Phase.L3, 2), (summary.Verdict, summary.Observed, summary.Mismatched));
    }

    [Fact]
    public void A_single_mismatch_is_unconfirmed_never_a_refusal()
    {
        // The 72 h recording: switch.grzalka_1 switched off with its thermostat open while a 1.3 kW
        // load dropped on L1 reads as one mismatch. One step can be a coincident load, so it points
        // at L1 but refuses nothing; following it would write the wrong phase.
        var summary = PhaseStepClassifier.Summarize([Classify(Phase.L2), Classify(Phase.L2, direction: -1, l1: -1500)], Parameters);

        Assert.Equal((PhaseStepVerdict.Inconclusive, InconclusiveReason.Unconfirmed, Phase.L1), (summary.Verdict, summary.Reason, summary.Observed));
    }

    [Fact]
    public void Agreement_in_one_direction_only_is_unconfirmed()
    {
        // A heater switched on twice while a kettle starts on its declared phase both times is still
        // a coincidence: a real assignment also steps down when the device is switched off.
        var twiceOn = PhaseStepClassifier.Summarize([Classify(Phase.L2, l2: 1700), Classify(Phase.L2, l2: 1710)], Parameters);
        var onAndOff = PhaseStepClassifier.Summarize([Classify(Phase.L2, l2: 1700), Classify(Phase.L2, direction: -1, l2: -1710)], Parameters);

        Assert.Equal((PhaseStepVerdict.Inconclusive, InconclusiveReason.Unconfirmed), (twiceOn.Verdict, twiceOn.Reason));
        Assert.Equal(PhaseStepVerdict.Confirmed, onAndOff.Verdict);
    }

    [Fact]
    public void Mismatches_on_different_phases_do_not_agree()
    {
        var summary = PhaseStepClassifier.Summarize([Classify(Phase.L2, l3: 1700), Classify(Phase.L2, direction: -1, l1: -1700)], Parameters);

        Assert.Equal((PhaseStepVerdict.Inconclusive, InconclusiveReason.Unconfirmed), (summary.Verdict, summary.Reason));
    }

    [Fact]
    public void Min_concordant_above_two_needs_that_many_agreeing()
    {
        var three = Parameters with { MinConcordant = 3 };
        var on = Classify(Phase.L2, l3: 1700);
        var off = Classify(Phase.L2, direction: -1, l3: -1700);

        Assert.Equal(PhaseStepVerdict.Inconclusive, PhaseStepClassifier.Summarize([on, off], three).Verdict);
        Assert.Equal(PhaseStepVerdict.Mismatch, PhaseStepClassifier.Summarize([on, off, on], three).Verdict);
    }

    [Fact]
    public void One_coincident_mismatch_among_confirmations_is_conflicting_not_blocking()
    {
        // A switching of a heater with an open thermostat while a 2 kW kettle starts on L3 looks like
        // a mismatch (ratio 1.18). A real swap never produces a confirmation, so both together are a
        // coincidence: inconclusive (conflicting), not a refusal.
        var summary = PhaseStepClassifier.Summarize([Classify(Phase.L2, l2: 1700), Classify(Phase.L2, l2: 1690), Classify(Phase.L2, l3: 2000)], Parameters);

        Assert.Equal((PhaseStepVerdict.Inconclusive, InconclusiveReason.Conflicting), (summary.Verdict, summary.Reason));
    }

    [Fact]
    public void A_dominant_side_decides_over_a_single_coincidence()
    {
        // The 72 h recording holds both cases: a real swap seen five times with one coincidental
        // confirmation must still be a mismatch, and five confirmations with one coincidental
        // mismatch a confirmation (dominance 3).
        var mismatch = Classify(Phase.L2, l3: 1700);
        var mismatchOff = Classify(Phase.L2, direction: -1, l3: -1700);
        var confirmation = Classify(Phase.L2, l2: 1700);
        var confirmationOff = Classify(Phase.L2, direction: -1, l2: -1700);

        var swapped = PhaseStepClassifier.Summarize([mismatch, mismatchOff, mismatch, mismatchOff, mismatch, confirmation], Parameters);
        var right = PhaseStepClassifier.Summarize([confirmation, confirmationOff, confirmation, mismatch], Parameters);

        Assert.Equal((PhaseStepVerdict.Mismatch, Phase.L3), (swapped.Verdict, swapped.Observed));
        Assert.Equal(PhaseStepVerdict.Confirmed, right.Verdict);
    }

    [Fact]
    public void Nothing_observed_is_inconclusive_timed_out()
    {
        var summary = PhaseStepClassifier.Summarize([], Parameters);

        Assert.Equal((PhaseStepVerdict.Inconclusive, InconclusiveReason.TimedOut), (summary.Verdict, summary.Reason));
    }

    private static PhaseStepResult Classify(Phase declared, int direction = 1, double expected = Heater, double l1 = 0, double l2 = 0, double l3 = 0)
    {
        var phases = Phases(Sigma, (Phase.L1, 500, 500 + l1), (Phase.L2, 400, 400 + l2), (Phase.L3, 600, 600 + l3));
        return PhaseStepClassifier.Classify(new PhaseStepInput(declared, direction, expected, phases, true, false), Parameters);
    }

    private static ImmutableDictionary<Phase, PhaseStepMeasurement> Phases(double sigma, params (Phase Phase, double Before, double After)[] phases)
    {
        var all = new Dictionary<Phase, PhaseStepMeasurement>
        {
            [Phase.L1] = new(500, 500, 500, sigma),
            [Phase.L2] = new(400, 400, 400, sigma),
            [Phase.L3] = new(600, 600, 600, sigma),
        };
        foreach (var (phase, before, after) in phases)
        {
            all[phase] = new PhaseStepMeasurement(before, before, after, sigma);
        }

        return all.ToImmutableDictionary();
    }
}
