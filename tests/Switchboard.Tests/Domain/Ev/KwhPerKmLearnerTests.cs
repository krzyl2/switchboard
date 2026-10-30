using System.Collections.Immutable;
using Switchboard.Domain.Ev;
using static Switchboard.Tests.Domain.Ev.EvTestParameters;

namespace Switchboard.Tests.Domain.Ev;

/// <summary>
/// EV-04: kWh/km learned from the car's own SoC and electric range. Why it matters: the target SoC of every trip is km x kWh/km, so one poisoned sample
/// (a PHEV reporting range 0 on a depleted buffer, SoC and range from different moments, a pair taken while charging) moves every future target.
/// </summary>
public sealed class KwhPerKmLearnerTests
{
    private static readonly EvLearnParameters Learn = Defaults.Learn;

    /// <summary>80 % of 18 kWh over 72 km is 0.20 kWh/km. Each rejection test changes exactly one thing of this.</summary>
    private static EvConsumptionObservation Valid(double soc = 80, double range = 72, DateTimeOffset? at = null, DateTimeOffset? rangeAt = null, bool charging = false) =>
        new(soc, at ?? T0, range, rangeAt ?? at ?? T0, charging);

    private static LearnOutcome Run(KwhPerKmState state, EvConsumptionObservation observation, EvLearnParameters? learn = null) =>
        KwhPerKmLearner.Learn(state, observation, CapacityKwh, learn ?? Learn);

    [Fact]
    public void The_first_sample_is_energy_in_the_battery_over_the_electric_range()
    {
        // Why: the definition. SoC x capacity / range; a flipped ratio or a forgotten percent scale gives 5 or 0.002, not 0.20.
        var outcome = Run(KwhPerKmState.Empty, Valid());

        Assert.Null(outcome.Rejection);
        Assert.Equal(0.20, outcome.Accepted!.Value, 9);
        Assert.Equal(0.20, outcome.State.Smoothed!.Value, 9);
        Assert.Single(outcome.State.Samples);
    }

    [Fact]
    public void A_second_sample_moves_the_smoothed_value_by_the_configured_weight()
    {
        // Why: smoothing comes from control.ev.learn.smoothing. 0.30 against 0.20 at weight 0.2 gives 0.22; with a different weight, or none, it would not.
        var first = Run(KwhPerKmState.Empty, Valid());
        var second = Run(first.State, Valid(soc: 60, range: 36, at: T0.AddHours(1)));

        Assert.Equal(0.30, second.Accepted!.Value, 9);
        Assert.Equal(0.22, second.State.Smoothed!.Value, 9);

        var heavier = Run(first.State, Valid(soc: 60, range: 36, at: T0.AddHours(1)), Learn with { Smoothing = 0.5 });
        Assert.Equal(0.25, heavier.State.Smoothed!.Value, 9);
    }

    [Fact]
    public void Nothing_learned_means_the_configured_fallback()
    {
        // Why: until a sample passes every check the plan must still have a consumption; it is the configured one, not a literal.
        Assert.Equal(0.20, KwhPerKmLearner.Current(KwhPerKmState.Empty, 0.20, Learn));
        Assert.Equal(0.25, KwhPerKmLearner.Current(KwhPerKmState.Empty, 0.25, Learn));
    }

    [Fact]
    public void A_learned_value_replaces_the_fallback_unless_configuration_has_since_excluded_it()
    {
        // Why: restart keeps the learned value, but if the plausibility bounds were narrowed afterwards a stored value outside them must not steer a plan.
        var learned = Run(KwhPerKmState.Empty, Valid(soc: 60, range: 36)).State;

        Assert.Equal(0.30, KwhPerKmLearner.Current(learned, 0.20, Learn), 9);
        Assert.Equal(0.20, KwhPerKmLearner.Current(learned, 0.20, Learn with { MaxKwhPerKm = 0.25 }));
    }

    public static TheoryData<string, EvConsumptionObservation, EvSampleRejection> Rejections => new()
    {
        { "soc missing", new EvConsumptionObservation(null, T0, 72, T0, false), EvSampleRejection.ValueMissing },
        { "range missing", new EvConsumptionObservation(80, T0, null, T0, false), EvSampleRejection.ValueMissing },
        { "soc time missing", new EvConsumptionObservation(80, null, 72, T0, false), EvSampleRejection.ValueMissing },
        { "range not a number", new EvConsumptionObservation(80, T0, double.NaN, T0, false), EvSampleRejection.ValueMissing },
        { "charging or unknown", Valid(charging: true), EvSampleRejection.Charging },
        { "different reports", Valid(at: T0, rangeAt: T0.AddSeconds(61)), EvSampleRejection.ReportsDiffer },
        { "soc above 100", Valid(soc: 101), EvSampleRejection.SocOutOfScale },
        { "soc below the minimum", Valid(soc: 19.9, range: 20), EvSampleRejection.SocBelowMinimum },
        { "range zero", Valid(range: 0), EvSampleRejection.RangeNotPositive },
        { "range negative", Valid(range: -5), EvSampleRejection.RangeNotPositive },
        { "consumption too low", Valid(soc: 80, range: 130), EvSampleRejection.Implausible },
        { "consumption too high", Valid(soc: 80, range: 20), EvSampleRejection.Implausible },
    };

    [Theory]
    [MemberData(nameof(Rejections))]
    public void Every_unusable_pair_is_rejected_for_its_own_reason_and_changes_nothing(string why, EvConsumptionObservation observation, EvSampleRejection expected)
    {
        // Why: each case is a way the predecessor's learning could be poisoned (EV.md risk 7); a rejected pair must leave state and smoothed value untouched.
        var before = Run(KwhPerKmState.Empty, Valid(at: T0.AddHours(-1))).State;

        var outcome = Run(before, observation);

        Assert.True(expected == outcome.Rejection, why);
        Assert.Null(outcome.Accepted);
        Assert.Same(before, outcome.State);
    }

    [Fact]
    public void The_minimum_soc_is_inclusive_and_comes_from_configuration()
    {
        // Why: "SoC >= learn.min_soc": exactly the minimum counts, and the limit is the configured one.
        Assert.Null(Run(KwhPerKmState.Empty, Valid(soc: 20, range: 30)).Rejection);
        Assert.Equal(EvSampleRejection.SocBelowMinimum, Run(KwhPerKmState.Empty, Valid(soc: 40, range: 60), Learn with { MinSoc = 50 }).Rejection);
    }

    [Fact]
    public void A_report_that_arrives_twice_is_one_sample()
    {
        // Why: the signal layer re-delivers the same report; counting it again would weight one moment in the smoothing several times.
        var once = Run(KwhPerKmState.Empty, Valid());
        var twice = Run(once.State, Valid());

        Assert.Equal(EvSampleRejection.AlreadySampled, twice.Rejection);
        Assert.Single(twice.State.Samples);
    }

    [Fact]
    public void The_sample_history_is_bounded_and_keeps_the_newest()
    {
        // Why: the history goes into ev.json, whose size is bounded by learn.max_samples; the smoothed value still saw every sample.
        var learn = Learn with { MaxSamples = 3 };
        var state = KwhPerKmState.Empty;
        for (var i = 0; i < 5; i++)
        {
            state = Run(state, Valid(at: T0.AddHours(i)), learn).State;
        }

        Assert.Equal(3, state.Samples.Length);
        Assert.Equal([T0.AddHours(2), T0.AddHours(3), T0.AddHours(4)], state.Samples.Select(s => s.At));
    }

    [Fact]
    public void A_state_over_a_lowered_limit_shrinks_on_the_next_sample()
    {
        // Why: configuration may lower max_samples after the file was written; the next accepted sample brings the history under the new limit.
        var samples = Enumerable.Range(0, 10).Select(i => new KwhPerKmSample(T0.AddHours(i - 20), 0.2)).ToImmutableArray();

        var state = Run(new KwhPerKmState(0.2, samples), Valid(), Learn with { MaxSamples = 4 }).State;

        Assert.Equal(4, state.Samples.Length);
        Assert.Equal(T0, state.Samples[^1].At);
    }

    [Fact]
    public void A_soc_and_a_range_reported_within_the_tolerance_are_one_report_and_the_boundary_is_inclusive()
    {
        // Why: Home Assistant stamps the two entities of one car update a moment apart, so exact equality would reject nearly every pair and nothing would
        // ever be learned. Exactly pair_tolerance apart still counts (either order), a tick more does not.
        var tolerance = Learn.PairTolerance;

        Assert.Null(Run(KwhPerKmState.Empty, Valid(at: T0, rangeAt: T0 + tolerance)).Rejection);
        Assert.Null(Run(KwhPerKmState.Empty, Valid(at: T0 + tolerance, rangeAt: T0)).Rejection);
        Assert.Equal(EvSampleRejection.ReportsDiffer, Run(KwhPerKmState.Empty, Valid(at: T0, rangeAt: T0 + tolerance + TimeSpan.FromTicks(1))).Rejection);
        Assert.Equal(EvSampleRejection.ReportsDiffer, Run(KwhPerKmState.Empty, Valid(at: T0 + tolerance + TimeSpan.FromTicks(1), rangeAt: T0)).Rejection);
    }

    [Fact]
    public void The_tolerance_is_the_configured_one()
    {
        // Why: control.ev.learn.pair_tolerance, not a literal: zero demands the same instant, a wider one accepts a wider gap.
        var exact = Learn with { PairTolerance = TimeSpan.Zero };
        Assert.Null(Run(KwhPerKmState.Empty, Valid(), exact).Rejection);
        Assert.Equal(EvSampleRejection.ReportsDiffer, Run(KwhPerKmState.Empty, Valid(at: T0, rangeAt: T0.AddTicks(1)), exact).Rejection);

        var wide = Learn with { PairTolerance = TimeSpan.FromMinutes(10) };
        Assert.Null(Run(KwhPerKmState.Empty, Valid(at: T0, rangeAt: T0.AddMinutes(10)), wide).Rejection);
    }

    [Fact]
    public void A_pair_is_stamped_and_deduplicated_by_the_later_of_its_two_times()
    {
        // Why: the same pair re-delivered (either entity first) is one sample; a new SoC against the same range is a different report and a new sample.
        var first = Run(KwhPerKmState.Empty, Valid(at: T0, rangeAt: T0.AddSeconds(30)));
        Assert.Equal(T0.AddSeconds(30), first.State.Samples[0].At);

        Assert.Equal(EvSampleRejection.AlreadySampled, Run(first.State, Valid(at: T0.AddSeconds(30), rangeAt: T0)).Rejection);
        Assert.Equal(EvSampleRejection.AlreadySampled, Run(first.State, Valid(at: T0, rangeAt: T0.AddSeconds(30))).Rejection);
        Assert.Null(Run(first.State, Valid(at: T0.AddSeconds(50), rangeAt: T0.AddSeconds(30))).Rejection);
    }

    [Fact]
    public void A_sample_exactly_on_a_plausibility_bound_is_accepted_and_just_beyond_it_is_not()
    {
        // Why: "within bounds" is inclusive. 50 % of 18 kWh over 75 km is exactly 0.12; 100 % over 30 km is exactly 0.60 (and a full battery is a valid SoC).
        Assert.Null(Run(KwhPerKmState.Empty, Valid(soc: 50, range: 75)).Rejection);
        Assert.Null(Run(KwhPerKmState.Empty, Valid(soc: 100, range: 30)).Rejection);
        Assert.Equal(EvSampleRejection.Implausible, Run(KwhPerKmState.Empty, Valid(soc: 50, range: 75.001)).Rejection);
        Assert.Equal(EvSampleRejection.Implausible, Run(KwhPerKmState.Empty, Valid(soc: 100, range: 29.99)).Rejection);
    }

    [Fact]
    public void The_ends_of_the_soc_scale_are_judged_by_their_own_rules()
    {
        // Why: 100 is on the scale, 100.5 and -1 are not; 0 is on the scale but under any minimum SoC.
        Assert.Equal(EvSampleRejection.SocOutOfScale, Run(KwhPerKmState.Empty, Valid(soc: 100.5, range: 30)).Rejection);
        Assert.Equal(EvSampleRejection.SocOutOfScale, Run(KwhPerKmState.Empty, Valid(soc: -1, range: 30)).Rejection);
        Assert.Equal(EvSampleRejection.SocBelowMinimum, Run(KwhPerKmState.Empty, Valid(soc: 0, range: 30)).Rejection);
        Assert.Equal(EvSampleRejection.Implausible, Run(KwhPerKmState.Empty, Valid(soc: 0, range: 30), Learn with { MinSoc = 0 }).Rejection);
    }
}

