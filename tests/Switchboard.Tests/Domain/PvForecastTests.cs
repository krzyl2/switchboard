using Switchboard.Domain.Planning;

namespace Switchboard.Tests.Domain;

/// <summary>
/// PV forecast processing (15-PLANNER). Why it matters: every surplus kWh the planner counts on comes out of these numbers; a unit slip (W against Wh),
/// an energy counted twice at a slot edge or a forecast extended past its end would plan a car charge on sun that is not there. Expected values are
/// worked by hand in the comments.
/// </summary>
public sealed class PvForecastTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly TimeSpan Cest = TimeSpan.FromHours(2);

    private static DateTimeOffset At(int hour, int minute = 0) => new(2026, 7, 15, hour, minute, 0, Cest);

    private static ForecastParameters Parameters(double derate = 1.0, int slotMinutes = 60) =>
        new(TimeSpan.FromMinutes(slotMinutes), derate, TimeSpan.FromDays(14), 0.8, 400);

    private static SlotGrid Grid(int fromHour, int toHour, int slotMinutes = 60) =>
        SlotGrid.Build(At(fromHour), At(toHour), TimeSpan.FromMinutes(slotMinutes), Warsaw);

    private static PvForecast Clear() => PvForecast.FromPowerPoints(
    [
        new PvPowerPoint(At(8), 0),
        new PvPowerPoint(At(9), 1000),
        new PvPowerPoint(At(10), 3000),
        new PvPowerPoint(At(11), 2000),
        new PvPowerPoint(At(12), 0),
    ]);

    [Fact]
    public void Power_points_integrate_by_the_trapezoid_per_slot()
    {
        // Why: the series gives power, the plan needs energy. Hourly trapezoids: 08-09 (0+1000)/2 = 500 Wh, 09-10 (1000+3000)/2 = 2000 Wh,
        // 10-11 (3000+2000)/2 = 2500 Wh, 11-12 (2000+0)/2 = 1000 Wh.
        var pv = Clear().Slots(Grid(8, 12), Parameters());

        Assert.Equal([0.5, 2.0, 2.5, 1.0], pv.Kwh.ToArray());
        Assert.Equal(PvBasis.Series, pv.Basis);
        Assert.Equal(PvCoverage.Full, pv.Coverage);
        Assert.Equal(At(12), pv.CoversUntil);
    }

    [Fact]
    public void A_half_hour_slot_reads_the_interpolated_power_inside_a_segment()
    {
        // Why: slot 15m and 30m share the code. 09:00-09:30 power goes 1000 -> 2000 W: (1000+2000)/2 x 0.5 h = 750 Wh;
        // 09:30-10:00 goes 2000 -> 3000 W: (2000+3000)/2 x 0.5 h = 1250 Wh. Together 2000 Wh, the same as the hourly slot (no energy lost at the cut).
        var pv = Clear().Slots(Grid(9, 10, 30), Parameters(slotMinutes: 30));

        Assert.Equal([0.75, 1.25], pv.Kwh.ToArray());
    }

    [Fact]
    public void The_derate_scales_every_slot_and_comes_from_the_parameters()
    {
        // Why: pv_derate 0.85 is the price difference the plan keeps against worse weather: 0.5 x 0.85 = 0.425, 2.0 x 0.85 = 1.7, 2.5 x 0.85 = 2.125, 1.0 x 0.85 = 0.85.
        var pv = Clear().Slots(Grid(8, 12), Parameters(derate: 0.85));

        Assert.Equal([0.425, 1.7, 2.125, 0.85], pv.Kwh.Select(k => Math.Round(k, 9)).ToArray());
    }

    [Fact]
    public void Nothing_is_invented_beyond_the_coverage_and_the_plan_says_the_forecast_is_partial()
    {
        // Why: a forecast that ends at 12:00 says nothing about the afternoon; the planner must see zero PV there and the note pv_forecast_partial, not the last value repeated.
        var pv = Clear().Slots(Grid(10, 15), Parameters());

        Assert.Equal([2.5, 1.0, 0, 0, 0], pv.Kwh.ToArray());
        Assert.Equal(PvCoverage.Partial, pv.Coverage);
        Assert.Equal(At(12), pv.CoversUntil);
    }

    [Fact]
    public void Slots_before_the_first_point_are_zero()
    {
        // Why: the forecast starts at sunrise; the hours before it are night, not an extrapolation of the first point.
        var pv = Clear().Slots(Grid(6, 9), Parameters());

        Assert.Equal([0, 0, 0.5], pv.Kwh.ToArray());
    }

    [Fact]
    public void A_missing_forecast_is_zero_for_the_whole_horizon_and_is_reported_missing()
    {
        // Why: a stale or absent forecast is never replaced by the last known curve (15-PLANNER); the plan then fills goals from the grid and says so.
        var pv = PvForecast.None.Slots(Grid(8, 12), Parameters());

        Assert.All(pv.Kwh, k => Assert.Equal(0, k));
        Assert.Equal(PvBasis.None, pv.Basis);
        Assert.Equal(PvCoverage.Missing, pv.Coverage);
        Assert.Null(pv.CoversUntil);
    }

    [Fact]
    public void A_forecast_that_ended_before_the_horizon_began_is_missing_not_partial()
    {
        // Why: yesterday's curve covers no slot of today; calling that "partial" would hide that there is no forecast at all.
        var pv = Clear().Slots(Grid(13, 16), Parameters());

        Assert.All(pv.Kwh, k => Assert.Equal(0, k));
        Assert.Equal(PvCoverage.Missing, pv.Coverage);
    }

    [Fact]
    public void Period_energies_are_wh_and_are_shared_by_overlap()
    {
        // Why: a series of energies per period (Wh) must not be read as power. 09-10 holds 1200 Wh (1200 W average), 10-11 holds 2400 Wh;
        // a grid from 09:30 to 10:30 gets 09:30-10:00 = 600 Wh and 10:00-10:30 = 1200 Wh, 1800 Wh in all, and the hourly slots get their own periods back.
        var forecast = PvForecast.FromPeriodEnergies([new PvPeriod(At(9), At(10), 1200), new PvPeriod(At(10), At(11), 2400)]);

        var shifted = forecast.Slots(SlotGrid.Build(At(9, 30), At(10, 30), TimeSpan.FromMinutes(60), Warsaw), Parameters());
        var hourly = forecast.Slots(Grid(9, 11), Parameters());

        Assert.Equal(1.8, shifted.Kwh.Sum(), 9);
        Assert.Equal([0.6, 1.2], shifted.Kwh.Select(k => Math.Round(k, 9)).ToArray());
        Assert.Equal([1.2, 2.4], hourly.Kwh.ToArray());
    }

    [Fact]
    public void Period_energies_given_by_their_start_last_until_the_next_start()
    {
        // Why: the {start, value} list shape names only starts; the end of a period is the next start, and the last one has the length the caller states.
        var forecast = PvForecast.FromPeriodStarts([(At(9), 1200), (At(10), 2400)], TimeSpan.FromHours(1));

        Assert.Equal(At(11), forecast.CoversUntil);
        Assert.Equal([1.2, 2.4], forecast.Slots(Grid(9, 11), Parameters()).Kwh.ToArray());
    }

    [Fact]
    public void Daily_totals_are_spread_as_a_half_sine_between_the_sun_times_and_add_up_to_the_total()
    {
        // Why: the degraded shape. 12 kWh between 06:00 and 18:00: the share of [t0, t1] is (cos(pi f0) - cos(pi f1)) / 2.
        // 06-09 (f 0 -> 1/4): 12 x (1 - 0.70711) / 2 = 1.75736; 09-15 (1/4 -> 3/4): 12 x (0.70711 + 0.70711) / 2 = 8.48528; 15-18 mirrors 06-09. Sum 12.
        var forecast = PvForecast.FromDailyTotals([new PvDaylight(At(6), At(18), 12)]);
        var grid = SlotGrid.Build(At(6), At(18), TimeSpan.FromMinutes(60), Warsaw, [At(9), At(15)]);

        var pv = forecast.Slots(Grid(6, 18), Parameters());
        var thirds = forecast.Slots(grid, Parameters());

        Assert.Equal(12, pv.Kwh.Sum(), 9);
        Assert.Equal(PvBasis.Totals, pv.Basis);
        Assert.Equal(1.757359313, thirds.Kwh[0] + thirds.Kwh[1] + thirds.Kwh[2], 6);
        Assert.Equal(8.485281374, thirds.Kwh.Skip(3).Take(6).Sum(), 6);
        Assert.Equal(1.757359313, thirds.Kwh.Skip(9).Sum(), 6);
        Assert.Equal(pv.Kwh[0], pv.Kwh[11], 9);
        Assert.True(pv.Kwh[5] > pv.Kwh[0] && pv.Kwh[6] > pv.Kwh[0], "the middle of the day carries more than the edges");
    }

    [Fact]
    public void Daily_totals_are_derated_and_zero_outside_daylight()
    {
        // Why: 12 kWh x 0.85 = 10.2 kWh in all; before sunrise and after sunset the half-sine says nothing, so those slots are exactly zero.
        var forecast = PvForecast.FromDailyTotals([new PvDaylight(At(6), At(18), 12)]);

        var pv = forecast.Slots(Grid(4, 20), Parameters(derate: 0.85));

        Assert.Equal(10.2, pv.Kwh.Sum(), 9);
        Assert.Equal([0, 0], pv.Kwh.Take(2).ToArray());
        Assert.Equal([0, 0], pv.Kwh.TakeLast(2).ToArray());
    }

    [Fact]
    public void Two_days_of_totals_each_keep_their_own_total_across_midnight()
    {
        // Why: today and tomorrow are two scalars with two pairs of sun times; the night between them must get nothing and neither day may leak into the other.
        var forecast = PvForecast.FromDailyTotals(
        [
            new PvDaylight(At(5), At(21), 20),
            new PvDaylight(At(5).AddDays(1), At(21).AddDays(1), 8),
        ]);
        var grid = SlotGrid.Build(At(0), At(0).AddDays(2), TimeSpan.FromMinutes(60), Warsaw);

        var pv = forecast.Slots(grid, Parameters());

        Assert.Equal(20, pv.Kwh.Take(24).Sum(), 9);
        Assert.Equal(8, pv.Kwh.Skip(24).Sum(), 9);
        Assert.Equal(PvCoverage.Partial, pv.Coverage);
    }

    [Fact]
    public void A_slot_energy_is_independent_of_how_the_grid_is_cut()
    {
        // Why: splitting a slot (a deadline cut, a 15 m grid) must conserve energy: 12 hourly slots and the same hours as 48 quarter-hour slots give one total.
        var forecast = Clear();

        var hourly = forecast.Slots(Grid(8, 12), Parameters()).Kwh.Sum();
        var quarters = forecast.Slots(Grid(8, 12, 15), Parameters(slotMinutes: 15)).Kwh.Sum();

        Assert.Equal(hourly, quarters, 9);
        Assert.Equal(6.0, hourly, 9);
    }

    [Fact]
    public void Malformed_input_is_refused_not_repaired()
    {
        // Why: the signal layer refuses a malformed series whole; the domain behind it must not quietly clamp a negative power or merge overlapping periods.
        Assert.Throws<ArgumentException>(() => PvForecast.FromPowerPoints([new PvPowerPoint(At(8), -1)]));
        Assert.Throws<ArgumentException>(() => PvForecast.FromPowerPoints([new PvPowerPoint(At(8), double.NaN)]));
        Assert.Throws<ArgumentException>(() => PvForecast.FromPowerPoints([new PvPowerPoint(At(8), 1), new PvPowerPoint(At(8), 2)]));
        Assert.Throws<ArgumentException>(() => PvForecast.FromPeriodEnergies([new PvPeriod(At(9), At(11), 1), new PvPeriod(At(10), At(12), 1)]));
        Assert.Throws<ArgumentException>(() => PvForecast.FromPeriodEnergies([new PvPeriod(At(9), At(9), 1)]));
        Assert.Throws<ArgumentException>(() => PvForecast.FromDailyTotals([new PvDaylight(At(18), At(6), 1)]));
        Assert.Throws<ArgumentException>(() => PvForecast.FromDailyTotals([new PvDaylight(At(6), At(18), 1), new PvDaylight(At(12), At(20), 1)]));
        Assert.Throws<ArgumentException>(() => PvForecast.FromDailyTotals([new PvDaylight(At(6), At(18), -1)]));
    }

    [Fact]
    public void Points_in_any_order_give_the_same_forecast()
    {
        // Why: determinism (the planner's output is byte-equal under input permutation); the processing sorts by time, never trusts the order.
        var sorted = Clear().Slots(Grid(8, 12), Parameters()).Kwh;
        var shuffled = PvForecast.FromPowerPoints(
        [
            new PvPowerPoint(At(11), 2000),
            new PvPowerPoint(At(8), 0),
            new PvPowerPoint(At(12), 0),
            new PvPowerPoint(At(10), 3000),
            new PvPowerPoint(At(9), 1000),
        ]).Slots(Grid(8, 12), Parameters()).Kwh;

        Assert.Equal(sorted, shuffled);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void A_derate_outside_zero_to_one_is_refused(double derate)
    {
        // Why: a derate above 1 would plan more sun than forecast; that is a configuration error, not a number to use.
        Assert.Throws<ArgumentOutOfRangeException>(() => Parameters(derate));
    }
}
