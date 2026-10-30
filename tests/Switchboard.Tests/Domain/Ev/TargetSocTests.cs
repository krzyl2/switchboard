using Switchboard.Domain.Ev;
using static Switchboard.Tests.Domain.Ev.EvTestParameters;

namespace Switchboard.Tests.Domain.Ev;

/// <summary>
/// EV-05: the target SoC of a trip. Formula: target = min(100, max(floor, trip_soc + floor_after + reserve)), trip_soc = km x kWh/km / capacity x 100.
/// Why it matters: a target too low strands the maintainer on fuel; one that drifts with every learned sample makes the plan flap (B3).
/// </summary>
public sealed class TargetSocTests
{
    // 0.20 kWh/km on 18 kWh: one km is 1.111 points, so 45 km is 50 points and 54 km is 60.
    private static TargetSoc Trip(double km, double kwhPerKm = 0.20, EvParameters? p = null) => TargetSocRule.ForTrip(km, kwhPerKm, CapacityKwh, p ?? Defaults);

    [Theory]
    [InlineData(45, 90)] // 50 points + 25 + 15 (the maintainer's table: 50 % needs 90 %)
    [InlineData(54, 100)] // 60 points + 40 (60 % needs 100 %)
    [InlineData(27, 70)] // 30 points + 40
    [InlineData(49.5, 95)] // 55 points + 40
    [InlineData(0, 40)] // no distance still leaves 25 + 15 on the floor
    [InlineData(9, 50)] // 10 points + 40
    public void The_target_is_the_trip_plus_the_floor_after_it_plus_the_reserve(double km, double expected)
    {
        Assert.Equal(expected, Trip(km).Percent, 9);
    }

    [Fact]
    public void The_floor_wins_when_it_is_above_what_the_trip_asks()
    {
        // Why: max(floor, ...): a high configured floor must hold even for a short trip.
        Assert.Equal(60, Trip(9, p: Defaults with { FloorPercent = 60 }).Percent, 9);
        Assert.Equal(50, Trip(9, p: Defaults with { FloorPercent = 30 }).Percent, 9);
    }

    [Fact]
    public void Each_term_comes_from_configuration()
    {
        // Why: floor_after and reserve are not literals; changing either moves the target by exactly that much.
        Assert.Equal(80, Trip(45, p: Defaults with { ReservePoints = 5 }).Percent, 9);
        Assert.Equal(80, Trip(45, p: Defaults with { FloorAfterPercent = 15 }).Percent, 9);
    }

    [Fact]
    public void The_target_follows_the_learned_consumption()
    {
        // Why: 45 km at 0.30 is 75 points and asks for everything; the same trip at 0.20 asks for 90. The learner's value must reach the target.
        Assert.Equal(90, Trip(45, 0.20).Percent, 9);
        Assert.Equal(100, Trip(45, 0.30).Percent, 9);
    }

    [Fact]
    public void A_trip_overrides_the_surplus_cap()
    {
        // Why: the 90 % cap is for charging without a trip; 100 % for a long one is not clipped by it.
        Assert.Equal(100, Trip(54, p: Defaults with { CapPercent = 80 }).Percent, 9);
    }

    [Fact]
    public void A_trip_that_asks_for_more_than_full_is_clamped_and_flagged()
    {
        // Why: 85 km uses 94 points; with the floor and reserve that is 134 %, so 100 % and Clamped, but the trip still fits the battery.
        var fits = Trip(85);
        Assert.Equal(100, fits.Percent);
        Assert.True(fits.Clamped);
        Assert.False(fits.ExceedsElectricRange);
        Assert.False(Trip(45).Clamped);
    }

    [Fact]
    public void A_trip_longer_than_the_electric_range_is_not_an_error_but_is_flagged()
    {
        // Why: a PHEV finishes on fuel: the target is 100 % and the UI says "exceeds electric range". A full 18 kWh at 0.20 is 90 km.
        var far = Trip(95);

        Assert.Equal(100, far.Percent);
        Assert.True(far.ExceedsElectricRange);
        Assert.False(Trip(89).ExceedsElectricRange);
        Assert.False(Trip(90 - 1e-6).ExceedsElectricRange);
    }

    [Theory]
    [InlineData(-1, 0.2, 18)]
    [InlineData(double.NaN, 0.2, 18)]
    [InlineData(10, 0, 18)]
    [InlineData(10, -0.2, 18)]
    [InlineData(10, 0.2, 0)]
    [InlineData(10, double.PositiveInfinity, 18)]
    public void Nonsense_inputs_are_refused_rather_than_turned_into_a_target(double km, double kwhPerKm, double capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TargetSocRule.ForTrip(km, kwhPerKm, capacity, Defaults));
    }

    [Fact]
    public void The_energy_to_draw_is_the_missing_points_with_the_losses_added()
    {
        // Why: from 40 % to 90 % stores 9 kWh; at 10 % loss the charger draws 9.9. Losses belong to the energy, not to the target.
        Assert.Equal(9.9, TargetSocRule.EnergyToDrawKwh(40, 90, CapacityKwh, 0.10), 9);
        Assert.Equal(9.0, TargetSocRule.EnergyToDrawKwh(40, 90, CapacityKwh, 0), 9);
        Assert.Equal(0, TargetSocRule.EnergyToDrawKwh(95, 90, CapacityKwh, 0.10));
    }

    // ----- the frozen target (B3) -----

    [Fact]
    public void The_target_stays_frozen_while_the_learned_consumption_wanders()
    {
        // Why: a target that follows every EWMA step changes the goal on every sample and the plan flaps. 45 km at 0.20 asks 90; the smoothed value then wanders by
        // a few thousandths per sample (the fresh target moves by 1 to 2 points, inside the 3-point threshold) and the frozen target must not move.
        var frozen = TargetSocRule.Freeze(null, "trip-1", Trip(45), T0, Defaults);
        var learned = KwhPerKmState.Empty;
        var moved = false;

        for (var i = 0; i < 6; i++)
        {
            var at = T0.AddHours(i + 1);
            var range = i % 2 == 0 ? 68 : 74; // 0.2118 and 0.1946 kWh/km
            learned = KwhPerKmLearner.Learn(learned, new EvConsumptionObservation(80, at, range, at, false), CapacityKwh, Defaults.Learn).State;
            var consumption = KwhPerKmLearner.Current(learned, Defaults.FallbackKwhPerKm, Defaults.Learn);
            var fresh = Trip(45, consumption);
            moved |= Math.Abs(fresh.Percent - frozen.Target.Percent) > 0.1;

            Assert.Same(frozen, TargetSocRule.Freeze(frozen, "trip-1", fresh, at, Defaults));
        }

        Assert.True(moved, "the fresh target must actually move for this test to mean anything");
    }

    [Fact]
    public void A_kept_target_keeps_its_own_flags()
    {
        // Why: the flags describe the frozen percentage. A fresh computation that crossed 100 (clamped, exceeds range) but stayed within the threshold of the
        // frozen one must not leak its flags onto the kept target.
        var frozen = TargetSocRule.Freeze(null, "trip-1", Trip(85), T0, Defaults); // 94 points, 100 % clamped
        var fresh = Trip(95); // exceeds the range, 100 %

        var kept = TargetSocRule.Freeze(frozen, "trip-1", fresh, T0.AddHours(1), Defaults);

        Assert.Same(frozen, kept);
        Assert.False(kept.Target.ExceedsElectricRange);
        Assert.True(fresh.ExceedsElectricRange);
    }

    [Fact]
    public void The_target_is_recomputed_only_beyond_the_threshold()
    {
        // Why: exactly the threshold (3 points) keeps the frozen target, anything more takes the fresh one and says when.
        var frozen = TargetSocRule.Freeze(null, "trip-1", Trip(45), T0, Defaults); // 90 %
        var exactly = Trip(45) with { Percent = 87 };
        var more = Trip(45) with { Percent = 86.9 };

        Assert.Same(frozen, TargetSocRule.Freeze(frozen, "trip-1", exactly, T0.AddHours(1), Defaults));
        var moved = TargetSocRule.Freeze(frozen, "trip-1", more, T0.AddHours(1), Defaults);
        Assert.Equal(86.9, moved.Target.Percent);
        Assert.Equal(T0.AddHours(1), moved.FrozenAt);

        var wide = Defaults with { TargetChangeThresholdPoints = 10 };
        Assert.Same(frozen, TargetSocRule.Freeze(frozen, "trip-1", more, T0.AddHours(1), wide));
    }

    [Fact]
    public void A_different_trip_never_inherits_the_frozen_target()
    {
        // Why: the freeze belongs to one trip; the next trip's target must be its own even when it is within the threshold of the old one.
        var frozen = TargetSocRule.Freeze(null, "trip-1", Trip(45), T0, Defaults);

        var next = TargetSocRule.Freeze(frozen, "trip-2", Trip(45.5), T0.AddHours(1), Defaults);

        Assert.Equal("trip-2", next.TripKey);
        Assert.NotSame(frozen, next);
    }

    // ----- weekly full charge: amendment 2a, data only -----

    [Theory]
    [InlineData("2026-03-28T23:30:00Z", true)] // 00:30 on Sunday 2026-03-29, the 23-hour day
    [InlineData("2026-03-29T21:59:00Z", true)] // 23:59 the same Sunday
    [InlineData("2026-03-29T22:00:00Z", false)] // 00:00 Monday
    [InlineData("2026-03-28T22:59:00Z", true)] // 23:59 Saturday
    [InlineData("2026-03-27T22:59:00Z", false)] // 23:59 Friday
    [InlineData("2026-10-24T22:00:00Z", true)] // 00:00 Sunday 2026-10-25, the 25-hour day
    [InlineData("2026-10-25T22:59:00Z", true)] // 23:59 the same Sunday (UTC+1 by then)
    [InlineData("2026-10-25T23:00:00Z", false)] // 00:00 Monday
    [InlineData("2026-10-23T21:59:00Z", false)] // 23:59 Friday
    public void The_weekly_day_is_the_local_calendar_day_also_on_the_23_and_25_hour_days(string utc, bool allowed)
    {
        // Why: Sunday begins and ends at local midnight; a UTC date or a fixed offset would be off by one hour twice a year.
        Assert.Equal(allowed, TargetSocRule.WeeklyFullAllowed(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture), Warsaw, false, Defaults));
    }

    [Fact]
    public void A_holiday_counts_as_a_weekend_day_only_when_the_weekend_is_allowed()
    {
        var friday = new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);

        Assert.False(TargetSocRule.WeeklyFullAllowed(friday, Warsaw, false, Defaults));
        Assert.True(TargetSocRule.WeeklyFullAllowed(friday, Warsaw, true, Defaults));
        Assert.False(TargetSocRule.WeeklyFullAllowed(friday, Warsaw, true, Defaults with { WeeklyFull = Defaults.WeeklyFull with { Days = [] } }));
    }

    [Fact]
    public void The_spec_rows_have_no_floating_point_noise()
    {
        // Why: 54 km x 0.20 / 18 x 100 is 60.00000000000001 in doubles. The row "60 % needs 100 %" reaches full, it does not ask for more, so it is not clamped
        // and exactly the rows 50 -> 90 and 60 -> 100 hold without a tolerance in the assertion.
        var fifty = Trip(45);
        Assert.Equal(50, fifty.TripSocPercent);
        Assert.Equal(90, fifty.Percent);

        var sixty = Trip(54);
        Assert.Equal(60, sixty.TripSocPercent);
        Assert.Equal(100, sixty.Percent);
        Assert.False(sixty.Clamped);
        Assert.False(sixty.ExceedsElectricRange);

        Assert.True(Trip(54.1).Clamped);
    }

    [Fact]
    public void A_trip_of_exactly_a_full_battery_does_not_exceed_the_range_and_a_hair_more_does()
    {
        // Why: 90 km at 0.20 kWh/km is exactly 18 kWh. Exceeds means longer than a full battery carries.
        var exact = Trip(90);
        Assert.Equal(100, exact.TripSocPercent);
        Assert.False(exact.ExceedsElectricRange);
        Assert.Equal(100, exact.Percent);

        Assert.True(Trip(90.01).ExceedsElectricRange);
    }

    [Fact]
    public void The_last_trip_rebuilds_the_frozen_target_with_every_flag()
    {
        // Why: after a restart the target is the frozen one, flags included; a record that kept only the percentage would rebuild a different target.
        var frozen = TargetSocRule.Freeze(null, "trip-9", Trip(95), T0, Defaults);

        var rebuilt = EvLastTrip.From(frozen).ToFrozen();

        Assert.Equal(frozen, rebuilt);
        Assert.True(rebuilt.Target.ExceedsElectricRange);
        Assert.True(rebuilt.Target.Clamped);
        Assert.Equal(frozen.Target.TripSocPercent, rebuilt.Target.TripSocPercent);
    }
}

