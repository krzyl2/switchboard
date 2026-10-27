using Switchboard.Domain.Balance;
using Switchboard.Domain.Units;

namespace Switchboard.Tests.Domain;

/// <summary>
/// Projection, band and allowance rule of the hourly balance (BAL-02, BAL-03, BAL-04,
/// 13-HOURLY-BALANCE). The rule decides whether energy is wanted at the next :00: ending an hour
/// below the floor is billed as import, ending it far above sells the surplus cheaply, so a start is
/// wanted only while the projection stays above the band after it, and the band collapses to the floor
/// over the last minutes (the burn-down). The numbers are the maintainer's starting values.
/// </summary>
public sealed class HourlyBalanceProjectionTests
{
    private static readonly HourlyBalanceParameters Parameters = new(
        FloorWh: 150,
        CeilingWh: 525,
        BurnWindow: TimeSpan.FromMinutes(15),
        SmoothingWindow: TimeSpan.FromMinutes(5),
        MinRemaining: TimeSpan.FromMinutes(1),
        MaxAge: TimeSpan.FromSeconds(60));

    private static readonly DateTimeOffset T0 = new(2026, 10, 26, 14, 0, 0, TimeSpan.FromHours(1));

    private static TimeSpan Min(double minutes) => TimeSpan.FromMinutes(minutes);

    private static Watts W(double value) => new(value);

    [Theory]
    [InlineData(3, 6, BalanceWant.Hold)]   // 3 min left, min_on 6 min: 3 min (75 Wh at 1.5 kW) fall in the next hour
    [InlineData(3, 3, BalanceWant.Start)]  // min_on ends exactly at the :00: no tail
    [InlineData(3, 0, BalanceWant.Start)]  // no lockout: no tail
    [InlineData(8, 6, BalanceWant.Start)]  // min_on ends before the :00: no tail
    public void A_start_is_charged_for_the_energy_its_min_on_holds_past_the_next_hour(double minutesLeft, double minOnMinutes, BalanceWant expected)
    {
        // The live 09:57 start: 120 Wh, 2.4 kW free, 1.5 kW candidate. Projection after it: 165 Wh at 3 min left
        // (above the 150 Wh floor) and 150 Wh at 8 min left. The tail is a charge on the start only, never a reason to stop.
        var want = HourlyBalanceRule.Decide(120, W(2400), W(0), W(1500), Min(minutesLeft), Parameters, Min(minOnMinutes));

        Assert.Equal(expected, want);
    }

    [Fact]
    public void The_tail_never_asks_for_a_stop_and_never_helps_a_start()
    {
        // A tail can only withhold: with the projection inside the band the answer stays Hold, and a start that
        // already fails without a tail fails with one.
        Assert.Equal(BalanceWant.Hold, HourlyBalanceRule.Decide(300, W(0), W(0), W(1500), Min(30), Parameters, Min(6)));
        Assert.Equal(BalanceWant.Hold, HourlyBalanceRule.Decide(0, W(1500), W(0), W(1500), Min(30), Parameters, Min(6)));
        Assert.Equal(0, HourlyBalanceRule.Tail(W(1500), Min(6), Min(30)));
        Assert.Equal(75, HourlyBalanceRule.Tail(W(1500), Min(6), Min(3)), 9);
    }

    [Fact]
    public void Projection_adds_the_free_power_not_taken_by_the_load_over_the_time_left()
    {
        // 100 Wh now, 3000 W free, 1000 W taken, half an hour left: 100 + 2000 W * 0.5 h.
        Assert.Equal(1100, HourlyBalanceRule.Project(100, W(3000), W(1000), Min(30), Parameters), 6);
    }

    [Fact]
    public void Projection_of_a_load_above_the_free_power_falls()
    {
        Assert.Equal(-200, HourlyBalanceRule.Project(50, W(1000), W(1500), Min(30), Parameters), 6);
    }

    [Fact]
    public void Projection_floors_the_time_left_at_min_remaining()
    {
        // 10 s to :00 is projected as 1 min: 0 + (1200 - 0) W * 1/60 h.
        Assert.Equal(20, HourlyBalanceRule.Project(0, W(1200), Watts.Zero, TimeSpan.FromSeconds(10), Parameters), 6);
        Assert.Equal(Min(1), HourlyBalanceRule.Remaining(TimeSpan.Zero, Parameters));
    }

    [Fact]
    public void Band_is_flat_at_the_ceiling_until_the_burn_window_starts()
    {
        var band = HourlyBalanceRule.Band(Min(40), Parameters);

        Assert.Equal((150, 525), (band.LoWh, band.HiWh));
        Assert.False(band.Burning);
        Assert.Equal(525, HourlyBalanceRule.Band(Min(15), Parameters).HiWh, 9);
    }

    [Fact]
    public void Band_ceiling_ramps_linearly_down_to_the_floor_over_the_burn_window()
    {
        var half = HourlyBalanceRule.Band(Min(7.5), Parameters);

        Assert.Equal(150, half.LoWh);
        Assert.Equal(337.5, half.HiWh, 6);
        Assert.True(half.Burning);
    }

    [Fact]
    public void Band_in_the_last_second_is_the_ramp_at_min_remaining_never_below_the_floor()
    {
        var band = HourlyBalanceRule.Band(TimeSpan.Zero, Parameters);

        Assert.Equal(150 + 375.0 / 15, band.HiWh, 6);
        Assert.True(band.HiWh >= band.LoWh);
    }

    [Fact]
    public void Start_when_the_projection_is_above_the_band_and_stays_above_the_floor_after_it()
    {
        // 3000 W free, nothing running, a 1650 W heater next: 1500 > 525 and 675 >= 150.
        Assert.Equal(BalanceWant.Start, HourlyBalanceRule.Decide(0, W(3000), Watts.Zero, W(1650), Min(30), Parameters));
    }

    [Fact]
    public void Start_is_refused_when_the_candidate_would_push_the_projection_below_the_floor()
    {
        // 1900 W free: 950 > 525, but with the heater 0 + 250 W * 0.5 h = 125 < 150.
        Assert.Equal(BalanceWant.Hold, HourlyBalanceRule.Decide(0, W(1900), Watts.Zero, W(1650), Min(30), Parameters));
        // 2000 W free lands exactly on 175 >= 150.
        Assert.Equal(BalanceWant.Start, HourlyBalanceRule.Decide(0, W(2000), Watts.Zero, W(1650), Min(30), Parameters));
    }

    [Fact]
    public void Hold_inside_the_band()
    {
        // 400 Wh projected is between 150 and 525.
        Assert.Equal(BalanceWant.Hold, HourlyBalanceRule.Decide(400, W(1650), W(1650), W(1650), Min(30), Parameters));
    }

    [Fact]
    public void The_edges_of_the_band_are_hold_for_a_stop_and_a_start()
    {
        // proj == hi (525) is not above it; proj == lo (150) is not below it.
        Assert.Equal(BalanceWant.Hold, HourlyBalanceRule.Decide(525, W(0), Watts.Zero, W(100), Min(30), Parameters));
        Assert.Equal(BalanceWant.Hold, HourlyBalanceRule.Decide(150, W(0), Watts.Zero, W(100), Min(30), Parameters));
    }

    [Fact]
    public void Stop_when_the_projection_falls_below_the_floor()
    {
        // The running heater takes 650 W more than is free: 0 - 650 W * 0.5 h = -325 < 150.
        Assert.Equal(BalanceWant.Stop, HourlyBalanceRule.Decide(0, W(1000), W(1650), W(1650), Min(30), Parameters));
    }

    [Fact]
    public void In_the_burn_window_a_stop_is_withheld_while_the_projection_is_not_negative()
    {
        // 10 min left, 100 Wh, 150 W short: 100 - 150 W * 1/6 h = 75: below the floor but not negative.
        Assert.Equal(BalanceWant.Hold, HourlyBalanceRule.Decide(100, W(1500), W(1650), W(1650), Min(10), Parameters));
        // The same shortfall a half hour before the :00 is a stop: 100 - 75 = 25 < 150.
        Assert.Equal(BalanceWant.Stop, HourlyBalanceRule.Decide(100, W(1500), W(1650), W(1650), Min(30), Parameters));
    }

    [Fact]
    public void In_the_burn_window_a_projection_below_zero_is_still_a_stop()
    {
        // 0 - 150 W * 1/6 h = -25.
        Assert.Equal(BalanceWant.Stop, HourlyBalanceRule.Decide(0, W(1500), W(1650), W(1650), Min(10), Parameters));
    }

    [Fact]
    public void In_the_burn_window_a_start_needs_only_the_collapsed_ceiling()
    {
        // 6 min left: hi = 150 + 375 * 0.4 = 300. 400 Wh with the load already covered projects to 400 > 300 and starts,
        // where the same 400 at 30 min left sits inside the band (hi 525).
        Assert.Equal(BalanceWant.Start, HourlyBalanceRule.Decide(400, W(1650), W(1650), W(100), Min(6), Parameters));
        Assert.Equal(BalanceWant.Hold, HourlyBalanceRule.Decide(400, W(1650), W(1650), W(100), Min(30), Parameters));
    }

    [Fact]
    public void An_unknown_balance_or_free_power_is_zero_allowance_whatever_the_rest_says()
    {
        Assert.Equal(BalanceWant.Unknown, HourlyBalanceRule.Decide(null, W(5000), Watts.Zero, W(1650), Min(30), Parameters));
        Assert.Equal(BalanceWant.Unknown, HourlyBalanceRule.Decide(5000, null, Watts.Zero, W(1650), Min(30), Parameters));
        Assert.Equal(BalanceWant.Unknown, HourlyBalanceRule.Decide(double.NaN, W(5000), Watts.Zero, W(1650), Min(30), Parameters));
        Assert.Equal(BalanceWant.Unknown, HourlyBalanceRule.Decide(0, W(double.PositiveInfinity), Watts.Zero, W(1650), Min(30), Parameters));
    }

    [Fact]
    public void Free_power_of_a_steady_sample_is_that_sample()
    {
        var window = new FreePowerWindow();
        Assert.Equal(1000, window.Observe(T0, W(1000), Parameters)!.Value.Value, 6);
        Assert.Equal(1000, window.Observe(T0.AddSeconds(10), W(1000), Parameters)!.Value.Value, 6);
        Assert.Equal(1000, window.Observe(T0.AddSeconds(20), W(1000), Parameters)!.Value.Value, 6);
    }

    [Fact]
    public void Free_power_is_weighted_by_how_long_each_sample_held()
    {
        var window = new FreePowerWindow();
        window.Observe(T0, W(1000), Parameters);

        // 1000 W held for 60 s, the new 2000 W has held for none yet.
        Assert.Equal(1000, window.Observe(T0.AddSeconds(60), W(2000), Parameters)!.Value.Value, 6);
        // Another 60 s at 2000 W: (1000*60 + 2000*60) / 120.
        Assert.Equal(1500, window.Observe(T0.AddSeconds(120), W(2000), Parameters)!.Value.Value, 6);
    }

    [Fact]
    public void Free_power_forgets_what_is_older_than_the_smoothing_window()
    {
        var window = new FreePowerWindow();
        for (var s = 0; s <= 300; s += 30)
        {
            window.Observe(T0.AddSeconds(s), W(100), Parameters);
        }

        window.Observe(T0.AddSeconds(330), W(1000), Parameters);
        var mean = window.Observe(T0.AddSeconds(360), W(1000), Parameters)!.Value.Value;

        // The window is 60..360 s: 270 s at 100 W, then 30 s at 1000 W.
        Assert.Equal((100 * 270 + 1000 * 30) / 300.0, mean, 6);
    }

    [Fact]
    public void Free_power_is_unknown_when_a_sample_is_unknown_and_starts_afresh_after_it()
    {
        var window = new FreePowerWindow();
        window.Observe(T0, W(100), Parameters);
        window.Observe(T0.AddSeconds(30), W(100), Parameters);

        Assert.Null(window.Observe(T0.AddSeconds(40), null, Parameters));
        // Nothing from before the unknown sample is carried over.
        Assert.Equal(900, window.Observe(T0.AddSeconds(50), W(900), Parameters)!.Value.Value, 6);
    }

    [Fact]
    public void Free_power_is_not_bridged_over_a_gap_longer_than_max_age()
    {
        var window = new FreePowerWindow();
        window.Observe(T0, W(100), Parameters);
        window.Observe(T0.AddSeconds(30), W(100), Parameters);

        Assert.Equal(900, window.Observe(T0.AddSeconds(200), W(900), Parameters)!.Value.Value, 6);
    }

    [Fact]
    public void Free_power_starts_afresh_after_a_clock_step_back()
    {
        var window = new FreePowerWindow();
        window.Observe(T0, W(100), Parameters);
        window.Observe(T0.AddSeconds(30), W(100), Parameters);

        Assert.Equal(700, window.Observe(T0.AddSeconds(10), W(700), Parameters)!.Value.Value, 6);
    }

    [Fact]
    public void Two_samples_at_one_instant_count_once_with_the_later_value()
    {
        var window = new FreePowerWindow();
        window.Observe(T0, W(100), Parameters);
        window.Observe(T0, W(300), Parameters);

        Assert.Equal(300, window.Observe(T0.AddSeconds(10), W(300), Parameters)!.Value.Value, 6);
    }

    [Fact]
    public void A_non_finite_sample_is_unknown()
    {
        var window = new FreePowerWindow();
        window.Observe(T0, W(100), Parameters);

        Assert.Null(window.Observe(T0.AddSeconds(10), W(double.NaN), Parameters));
    }
}
