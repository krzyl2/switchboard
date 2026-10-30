using Switchboard.Domain.Ev;
using static Switchboard.Tests.Domain.Ev.EvTestParameters;

namespace Switchboard.Tests.Domain.Ev;

/// <summary>
/// EV.md section 4 B10: the SoC between the car's readings. Why it matters: a stale SoC steers a hard trip goal. A car that repeats an old value while
/// charging must not pull a correct estimate back, and an estimate must die (not be held) when its inputs are too old or the unseen energy too large.
/// </summary>
public sealed class SocEstimatorTests
{
    private static readonly ChargerEnergy Idle = new(0, true);

    private static SocStep Step(SocEstimatorState state, DateTimeOffset now, SocReading? reading = null, ChargerEnergy? charger = null, EvParameters? p = null) =>
        SocEstimator.Step(state, reading, charger ?? Idle, now, CapacityKwh, p ?? Defaults);

    /// <summary>A state anchored at 50 % at T0.</summary>
    private static SocEstimatorState Anchored() => Step(SocEstimatorState.Empty, T0, new SocReading(50, T0)).State;

    [Fact]
    public void Without_a_reading_there_is_no_estimate()
    {
        // Why: no invented SoC; the goals stay inactive.
        var step = Step(SocEstimatorState.Empty, T0);

        Assert.False(step.Estimate.Available);
        Assert.Equal(SocEstimateGap.NoReading, step.Estimate.Gap);
    }

    [Fact]
    public void The_first_reading_is_the_estimate()
    {
        var step = Step(SocEstimatorState.Empty, T0, new SocReading(50, T0));

        Assert.Equal(50, step.Estimate.Percent);
        Assert.Equal(SocAnchorEvent.Anchored, step.Event);
    }

    [Fact]
    public void Between_readings_the_estimate_grows_from_the_energy_stored_not_the_energy_drawn()
    {
        // Why: 1 kWh drawn at 10 % loss stores 0.909 kWh = 5.05 points of 18 kWh. Ignoring the loss would overstate the SoC, and the car would leave short.
        var step = Step(Anchored(), T0.AddMinutes(10), charger: new ChargerEnergy(1.0, true));

        Assert.Equal(50 + (1.0 / 1.10 / 18 * 100), step.Estimate.Percent!.Value, 9);
        Assert.Equal(SocAnchorEvent.None, step.Event);
    }

    [Fact]
    public void Re_reporting_the_same_value_does_not_reset_the_anchor_or_the_energy()
    {
        // Why: a car that lags while charging keeps repeating its old value; treating that as a fresh reading would throw away the energy that did arrive.
        var grown = Step(Anchored(), T0.AddMinutes(10), charger: new ChargerEnergy(1.0, true));
        var again = Step(grown.State, T0.AddMinutes(20), new SocReading(50, T0.AddMinutes(20)));

        Assert.Equal(grown.Estimate.Percent!.Value, again.Estimate.Percent!.Value, 9);
        Assert.Equal(SocAnchorEvent.None, again.Event);
        Assert.Equal(T0, again.State.AnchorAt);
        Assert.Equal(1.0, again.State.DrawnKwhSinceAnchor);
    }

    [Fact]
    public void Re_reporting_the_same_value_refreshes_the_age_but_not_the_anchor()
    {
        // Why (decision 2026-10-30, replaces the earlier "does not make it younger"): an idle plugged car re-reporting 90 % proves the integration is alive and the
        // battery did not move, so it must not lose its estimate after soc_max_age. Only the age is refreshed; the anchor and the energy stay (see the test above).
        var late = T0 + Defaults.SocMaxAge - TimeSpan.FromMinutes(1);
        var repeated = Step(Anchored(), late, new SocReading(50, late));
        Assert.Equal(T0, repeated.State.AnchorAt);

        var muchLater = late + Defaults.SocMaxAge - TimeSpan.FromMinutes(1);
        Assert.True(Step(repeated.State, muchLater).Estimate.Available);

        var tooLate = late + Defaults.SocMaxAge + TimeSpan.FromSeconds(1);
        var step = Step(repeated.State, tooLate);
        Assert.False(step.Estimate.Available);
        Assert.Equal(SocEstimateGap.TooOld, step.Estimate.Gap);
    }

    [Fact]
    public void Refreshing_the_age_does_not_lift_the_drift_limit()
    {
        // Why: the repeated value says nothing about energy that reached the battery; the drift limit still drops the estimate however fresh the reports are.
        var drawn = Step(Anchored(), T0.AddMinutes(10), charger: new ChargerEnergy(Defaults.DriftLimitKwh + 0.1, true));
        var repeated = Step(drawn.State, T0.AddMinutes(20), new SocReading(50, T0.AddMinutes(20)));

        Assert.Equal(SocEstimateGap.DriftExceeded, repeated.Estimate.Gap);
    }

    [Fact]
    public void A_changed_value_resets_the_anchor_and_drops_the_accumulated_energy()
    {
        // Why: a fresh reading is the truth; the estimate restarts from it with zero energy since.
        var grown = Step(Anchored(), T0.AddMinutes(10), charger: new ChargerEnergy(1.0, true));
        var changed = Step(grown.State, T0.AddMinutes(20), new SocReading(55, T0.AddMinutes(20)));

        Assert.Equal(55, changed.Estimate.Percent);
        Assert.Equal(0, changed.State.DrawnKwhSinceAnchor);
        Assert.Equal(T0.AddMinutes(20), changed.State.AnchorAt);
        Assert.Equal(SocAnchorEvent.Changed, changed.Event);
    }

    [Fact]
    public void A_value_far_from_the_estimate_is_a_jump_and_resets_it()
    {
        // Why: the car was charged away from home (or driven) outside what the add-on saw; the estimate must follow the car, not the energy counter.
        var grown = Step(Anchored(), T0.AddMinutes(10), charger: new ChargerEnergy(1.0, true));
        var jump = Step(grown.State, T0.AddMinutes(20), new SocReading(95, T0.AddMinutes(20)));

        Assert.Equal(SocAnchorEvent.Jump, jump.Event);
        Assert.Equal(95, jump.Estimate.Percent);
        Assert.Equal(0, jump.State.DrawnKwhSinceAnchor);
    }

    [Fact]
    public void A_small_difference_is_a_change_not_a_jump_and_the_threshold_is_the_drift_limit()
    {
        // Why: the jump bound is the configured drift limit in points (1.5 kWh of 18 = 8.33), not a literal: the same difference is a jump once the limit is tighter.
        var small = Step(Anchored(), T0.AddMinutes(10), new SocReading(55, T0.AddMinutes(10)));
        Assert.Equal(SocAnchorEvent.Changed, small.Event);

        var tight = Step(Anchored(), T0.AddMinutes(10), new SocReading(55, T0.AddMinutes(10)), p: Defaults with { DriftLimitKwh = 0.5 });
        Assert.Equal(SocAnchorEvent.Jump, tight.Event);
    }

    [Fact]
    public void An_older_reading_than_the_last_one_is_ignored()
    {
        // Why: out-of-order delivery must not move the anchor back to a value the car no longer has.
        var newer = Step(Anchored(), T0.AddMinutes(30), new SocReading(60, T0.AddMinutes(30)));
        var older = Step(newer.State, T0.AddMinutes(31), new SocReading(40, T0.AddMinutes(10)));

        Assert.Equal(60, older.Estimate.Percent);
        Assert.Equal(SocAnchorEvent.None, older.Event);
    }

    [Fact]
    public void Beyond_the_drift_limit_the_estimate_is_unavailable_and_the_limit_itself_still_counts()
    {
        // Why: the energy that reached the car unseen (air conditioning) cannot be told from charging, so past ev.drift_limit_kwh the estimate is dropped, never held.
        var atLimit = Step(Anchored(), T0.AddMinutes(10), charger: new ChargerEnergy(Defaults.DriftLimitKwh, true));
        Assert.True(atLimit.Estimate.Available);

        var over = Step(atLimit.State, T0.AddMinutes(11), charger: new ChargerEnergy(0.01, true));
        Assert.False(over.Estimate.Available);
        Assert.Equal(SocEstimateGap.DriftExceeded, over.Estimate.Gap);

        var wider = Step(atLimit.State, T0.AddMinutes(11), charger: new ChargerEnergy(0.01, true), p: Defaults with { DriftLimitKwh = 3 });
        Assert.True(wider.Estimate.Available);
    }

    [Fact]
    public void Beyond_the_max_age_the_estimate_is_unavailable_and_the_age_itself_still_counts()
    {
        // Why: B10 "beyond them the goals are inactive (never the last value)". Exactly soc_max_age is allowed, one second more is not.
        Assert.True(Step(Anchored(), T0 + Defaults.SocMaxAge).Estimate.Available);

        var step = Step(Anchored(), T0 + Defaults.SocMaxAge + TimeSpan.FromSeconds(1));

        Assert.False(step.Estimate.Available);
        Assert.Equal(SocEstimateGap.TooOld, step.Estimate.Gap);
        Assert.Null(step.Estimate.Percent);
    }

    [Fact]
    public void A_gap_in_charger_data_makes_the_estimate_unavailable_until_a_changed_reading()
    {
        // Why: energy that arrives while the charger's data is stale is invisible; if the gap were forgotten the moment data returned, the estimate would
        // silently miss it and the car would leave short. Stale data adds nothing, the gap is remembered, and only a changed reading (the truth) ends it.
        var idleStale = Step(Anchored(), T0.AddMinutes(5), charger: new ChargerEnergy(2.0, false));
        Assert.Equal(0, idleStale.State.DrawnKwhSinceAnchor);
        Assert.Equal(SocEstimateGap.ChargerStale, idleStale.Estimate.Gap);

        var back = Step(idleStale.State, T0.AddMinutes(10), charger: new ChargerEnergy(0, true));
        Assert.Equal(SocEstimateGap.ChargerStale, back.Estimate.Gap);

        var repeated = Step(back.State, T0.AddMinutes(11), new SocReading(50, T0.AddMinutes(11)), new ChargerEnergy(0, true));
        Assert.Equal(SocEstimateGap.ChargerStale, repeated.Estimate.Gap);

        var changed = Step(repeated.State, T0.AddMinutes(12), new SocReading(52, T0.AddMinutes(12)), new ChargerEnergy(0, true));
        Assert.Equal(52, changed.Estimate.Percent);
        Assert.False(changed.State.ChargerGap);
    }

    [Fact]
    public void A_gap_after_energy_was_counted_is_also_sticky()
    {
        var grown = Step(Anchored(), T0.AddMinutes(5), charger: new ChargerEnergy(1.0, true));
        var stale = Step(grown.State, T0.AddMinutes(10), charger: new ChargerEnergy(0, false));
        Assert.Equal(SocEstimateGap.ChargerStale, stale.Estimate.Gap);

        Assert.False(Step(stale.State, T0.AddMinutes(15), charger: new ChargerEnergy(0, true)).Estimate.Available);
    }

    [Fact]
    public void A_stale_charger_before_any_anchor_is_no_gap()
    {
        // Why: the gap is about energy since the anchor; before one there is nothing to miss.
        var first = Step(SocEstimatorState.Empty, T0, new SocReading(50, T0), new ChargerEnergy(0, false));

        Assert.Equal(50, first.Estimate.Percent);
    }

    [Fact]
    public void The_estimate_never_exceeds_a_full_battery()
    {
        // Why: charger energy past 100 % (taper, losses larger than assumed) must not produce a 104 % that makes the goal look met.
        var near = Step(SocEstimatorState.Empty, T0, new SocReading(98, T0)).State;

        Assert.Equal(100, Step(near, T0.AddMinutes(5), charger: new ChargerEnergy(1.4, true)).Estimate.Percent);
    }

    [Fact]
    public void A_reading_out_of_scale_is_not_an_anchor()
    {
        // Why: a unit slip (0.5 as a fraction, 5000) must not become the SoC.
        Assert.Equal(SocEstimateGap.NoReading, Step(SocEstimatorState.Empty, T0, new SocReading(120, T0)).Estimate.Gap);
        Assert.Equal(SocEstimateGap.NoReading, Step(SocEstimatorState.Empty, T0, new SocReading(double.NaN, T0)).Estimate.Gap);
    }

    [Fact]
    public void A_new_value_exactly_the_drift_limit_away_is_a_change_and_one_step_more_is_a_jump()
    {
        // Why: the boundary of the jump rule. 4.5 kWh of 18 is exactly 25 points.
        var p = Defaults with { DriftLimitKwh = 4.5 };

        Assert.Equal(SocAnchorEvent.Changed, Step(Anchored(), T0.AddMinutes(1), new SocReading(75, T0.AddMinutes(1)), p: p).Event);
        Assert.Equal(SocAnchorEvent.Jump, Step(Anchored(), T0.AddMinutes(1), new SocReading(75.5, T0.AddMinutes(1)), p: p).Event);
        Assert.Equal(SocAnchorEvent.Changed, Step(Anchored(), T0.AddMinutes(1), new SocReading(25, T0.AddMinutes(1)), p: p).Event);
        Assert.Equal(SocAnchorEvent.Jump, Step(Anchored(), T0.AddMinutes(1), new SocReading(24.5, T0.AddMinutes(1)), p: p).Event);
    }

    [Fact]
    public void A_full_battery_reading_is_an_anchor_and_a_reading_of_zero_too()
    {
        // Why: 0 and 100 are the ends of the scale, not out of it.
        var full = Step(SocEstimatorState.Empty, T0, new SocReading(100, T0));
        Assert.Equal(100, full.Estimate.Percent);
        Assert.Equal(100, Step(full.State, T0.AddMinutes(5), charger: new ChargerEnergy(1, true)).Estimate.Percent);

        Assert.Equal(0, Step(SocEstimatorState.Empty, T0, new SocReading(0, T0)).Estimate.Percent);
        Assert.Equal(SocEstimateGap.NoReading, Step(SocEstimatorState.Empty, T0, new SocReading(100.001, T0)).Estimate.Gap);
        Assert.Equal(SocEstimateGap.NoReading, Step(SocEstimatorState.Empty, T0, new SocReading(-0.001, T0)).Estimate.Gap);
    }

    [Fact]
    public void A_report_time_in_the_future_is_read_as_now_and_does_not_freeze_the_estimator()
    {
        // Why: a source clock ahead of ours would otherwise make every honest reading look older than the last one, and the estimator would ignore the car.
        var ahead = Step(SocEstimatorState.Empty, T0, new SocReading(50, T0.AddHours(1)));
        Assert.Equal(T0, ahead.State.LastReadingAt);

        var next = Step(ahead.State, T0.AddMinutes(1), new SocReading(55, T0.AddMinutes(1)));

        Assert.Equal(55, next.Estimate.Percent);
        Assert.Equal(SocAnchorEvent.Changed, next.Event);
    }

    [Fact]
    public void A_clock_that_steps_back_does_not_freeze_the_estimator()
    {
        // Why: stored times are capped at now each step, so after a step back a changed reading stamped by the new clock is not "older than the last one".
        var anchored = Step(SocEstimatorState.Empty, T0.AddHours(1), new SocReading(50, T0.AddHours(1)));

        var back = Step(anchored.State, T0, new SocReading(55, T0));

        Assert.Equal(55, back.Estimate.Percent);
        Assert.Equal(SocAnchorEvent.Changed, back.Event);
    }

    [Fact]
    public void A_changed_reading_is_ignored_only_when_it_was_reported_before_the_last_accepted_one()
    {
        // Why: the documented rule. Equal times are accepted (two reports in one instant), strictly earlier ones are out-of-order deliveries.
        var latest = Step(Anchored(), T0.AddMinutes(30), new SocReading(60, T0.AddMinutes(30)));

        Assert.Equal(55, Step(latest.State, T0.AddMinutes(31), new SocReading(55, T0.AddMinutes(30))).Estimate.Percent);
        Assert.Equal(60, Step(latest.State, T0.AddMinutes(31), new SocReading(55, T0.AddMinutes(30) - TimeSpan.FromTicks(1))).Estimate.Percent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-18)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_capacity_that_is_not_positive_gives_no_estimate_rather_than_nan(double capacity)
    {
        // Why: percent = energy / capacity; a zero would be Infinity or NaN steering a goal.
        var step = SocEstimator.Step(SocEstimatorState.Empty, new SocReading(50, T0), Idle, T0, capacity, Defaults);

        Assert.Null(step.Estimate.Percent);
        Assert.Equal(SocEstimateGap.InvalidCapacity, step.Estimate.Gap);
        Assert.Same(SocEstimatorState.Empty, step.State);
    }

    [Fact]
    public void A_parked_unplugged_car_keeps_its_estimate_when_the_charger_entity_goes_stale()
    {
        // Why: the known gap. The go-e blips or reports rarely; with no way for energy to flow (car not plugged in) a stale tick hides nothing, and the
        // estimate used to die for hours, leaving the trip goal inactive for a car that sat still.
        var parked = new ChargerEnergy(0, Fresh: false, CouldFlow: false);
        var step = Step(Anchored(), T0.AddMinutes(10), charger: parked);
        step = Step(step.State, T0.AddMinutes(20), charger: parked);

        Assert.Equal(50, step.Estimate.Percent);
        Assert.False(step.State.ChargerGap);
    }

    [Fact]
    public void A_stale_charger_while_energy_could_flow_is_still_a_gap()
    {
        // Why: the conservative side. Plugged (or not known) and the charger silent may have hidden energy: no estimate until the car reports a changed value.
        var step = Step(Anchored(), T0.AddMinutes(10), charger: new ChargerEnergy(0, Fresh: false, CouldFlow: true));

        Assert.Equal(SocEstimateGap.ChargerStale, step.Estimate.Gap);
        Assert.Equal(SocEstimateGap.ChargerStale, Step(Anchored(), T0.AddMinutes(10), charger: new ChargerEnergy(0, false)).Estimate.Gap);
    }

    [Fact]
    public void A_gap_while_plugged_is_not_healed_by_a_later_unplugged_tick()
    {
        // Why: the gap is sticky by design (energy during it was never seen); only a changed reading resets it, whatever the plug says afterwards.
        var gap = Step(Anchored(), T0.AddMinutes(10), charger: new ChargerEnergy(0, false, true));
        var later = Step(gap.State, T0.AddMinutes(20), charger: new ChargerEnergy(0, false, false));

        Assert.Equal(SocEstimateGap.ChargerStale, later.Estimate.Gap);
    }
}

