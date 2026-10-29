using System.Collections.Immutable;
using Switchboard.Application.Planning;
using Switchboard.Domain.Forecast;
using Switchboard.Domain.Planning;
using Switchboard.Domain.Signals;

namespace Switchboard.Tests.Application.Planning;

/// <summary>
/// PLAN-06, the degraded PV shape in the planner's sources (15-PLANNER, "PV forecast processing"): when Home Assistant gives no hourly series, only a
/// day total for today and for tomorrow (kWh) and the sun's next rising and next setting, the totals are spread over daylight. Why it matters: a core
/// <c>sun.sun</c> reports the NEXT events, so after sunset both point to tomorrow; taking them as today's daylight would put today's PV on the wrong
/// day or throw. A total that is stale or a sun that is missing gives zero PV for that day, never the last known curve, and a usable series always
/// wins. The numbers are the maintainer's HA (energy_production_today 9.655 kWh, energy_production_tomorrow 12.903 kWh).
/// </summary>
public sealed class PlanSourceMapperTotalsTests
{
    private const double Today = 9.655;
    private const double Tomorrow = 12.903;

    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly ForecastParameters NoDerate = new(TimeSpan.FromMinutes(15), 1.0, TimeSpan.FromDays(14), 0.8, 400);

    private static DateTimeOffset Utc(int month, int day, int hour, int minute) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    private static ForecastSample Total(SignalRole role, double kwh, SignalState state = SignalState.Fresh) =>
        new(role, SignalKind.EnergyForecast, state, state == SignalState.Fresh ? SignalDetail.None : SignalDetail.MaxAgeExceeded, Utc(10, 29, 0, 0), null, state == SignalState.Fresh ? kwh : null, null);

    private static ForecastSample Sun(SignalRole role, DateTimeOffset at, SignalState state = SignalState.Fresh) =>
        new(role, SignalKind.Timestamp, state, state == SignalState.Fresh ? SignalDetail.None : SignalDetail.MaxAgeExceeded, Utc(10, 29, 0, 0), null, null, state == SignalState.Fresh ? at : null);

    private static ForecastInputs Inputs(params (string Name, ForecastSample Sample)[] samples) =>
        new(samples.ToImmutableDictionary(s => SignalName.Parse(s.Name), s => s.Sample));

    private static ForecastInputs Totals(DateTimeOffset rising, DateTimeOffset setting, SignalState sun = SignalState.Fresh, SignalState today = SignalState.Fresh, SignalState tomorrow = SignalState.Fresh) =>
        Inputs(
            ("pv_today", Total(SignalRole.PvForecastToday, Today, today)),
            ("pv_tomorrow", Total(SignalRole.PvForecastTomorrow, Tomorrow, tomorrow)),
            ("sun_rising", Sun(SignalRole.SunRising, rising, sun)),
            ("sun_setting", Sun(SignalRole.SunSetting, setting, sun)));

    private static DateTimeOffset LocalMidnight(int month, int day)
    {
        var local = new DateTime(2026, month, day, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Warsaw.GetUtcOffset(local));
    }

    /// <summary>The kWh of each local day of the two days starting at the local midnight of <paramref name="month"/>/<paramref name="day"/>, over a grid cut on the zone's offset changes.</summary>
    private static (double First, double Second, PvSlots Slots) DaySums(PvForecast pv, int month, int day)
    {
        var from = LocalMidnight(month, day);
        var next = DateOnly.FromDateTime(from.DateTime).AddDays(2);
        var to = LocalMidnight(next.Month, next.Day);
        var grid = SlotGrid.Build(from, to, TimeSpan.FromMinutes(15), Warsaw);
        var slots = pv.Slots(grid, NoDerate);
        var firstDay = DateOnly.FromDateTime(from.DateTime);
        var first = grid.Slots.Zip(slots.Kwh).Where(p => DateOnly.FromDateTime(p.First.Start.DateTime) == firstDay).Sum(p => p.Second);
        var second = grid.Slots.Zip(slots.Kwh).Where(p => DateOnly.FromDateTime(p.First.Start.DateTime) != firstDay).Sum(p => p.Second);
        return (first, second, slots);
    }

    [Theory]
    [InlineData(4, 29, 5, 30, 29, 15, 45)] // before sunrise: both next events are today's
    [InlineData(10, 30, 5, 32, 29, 15, 45)] // midday: the sun has risen, the next rising is tomorrow's
    [InlineData(17, 30, 5, 32, 30, 15, 43)] // after sunset: both next events are tomorrow's
    public void Each_total_lands_on_its_own_local_day_whichever_next_events_the_sun_reports(int nowHour, int riseDay, int riseHour, int riseMinute, int setDay, int setHour, int setMinute)
    {
        // Why: sun.sun gives the next rising and the next setting; taken as they are they would be today's or tomorrow's depending on the hour. Whatever they
        // are, today's total must be spent on today and tomorrow's on tomorrow, in full (the integral of the half-sine is the total).
        var forecast = Totals(Utc(10, riseDay, riseHour, riseMinute), Utc(10, setDay, setHour, setMinute));

        var pv = PlanSourceMapper.Pv(forecast, Warsaw, Utc(10, 29, nowHour, 0));

        Assert.Equal(PvBasis.Totals, pv.Basis);
        var (today, tomorrow, _) = DaySums(pv, 10, 29);
        Assert.Equal(Today, today, 6);
        Assert.Equal(Tomorrow, tomorrow, 6);
    }

    [Fact]
    public void After_sunset_today_is_the_next_events_moved_back_a_day_and_the_forecast_ends_at_tomorrows_sunset()
    {
        // Why: pins what the derivation does with next_rising and next_setting that both point to tomorrow: today's window is theirs minus 24 hours, so
        // the half-sine of today sits between today's real rise and set, and nothing is promised beyond tomorrow's sunset.
        var rising = Utc(10, 30, 5, 32);
        var setting = Utc(10, 30, 15, 43);

        var pv = PlanSourceMapper.Pv(Totals(rising, setting), Warsaw, Utc(10, 29, 17, 0));

        Assert.Equal(setting, pv.CoversUntil);
        var beforeTodaysRise = SlotGrid.Build(Utc(10, 28, 22, 0), rising.AddDays(-1), TimeSpan.FromMinutes(15), Warsaw);
        Assert.All(pv.Slots(beforeTodaysRise, NoDerate).Kwh, kwh => Assert.Equal(0, kwh));
    }

    [Theory]
    [InlineData(10, 25, 25)]
    [InlineData(3, 29, 23)]
    public void A_day_of_23_or_25_hours_keeps_its_total_whole_and_on_its_own_day(int month, int day, int hours)
    {
        // Why: the sun does not follow the clock change. Moving the next events by local clock time would shift the whole day by an hour at the DST change;
        // moving them by absolute 24-hour spans keeps the daylight where the sun is. 2026-03-29 has 23 hours, 2026-10-25 has 25.
        var length = LocalMidnight(month, day + 1) - LocalMidnight(month, day);
        Assert.Equal(TimeSpan.FromHours(hours), length);
        var rising = month == 10 ? Utc(10, day + 1, 5, 36) : Utc(3, day + 1, 4, 30);
        var setting = month == 10 ? Utc(10, day, 15, 55) : Utc(3, day, 17, 20);
        var now = new DateTimeOffset(2026, month, day, 12, 0, 0, TimeSpan.Zero);

        var pv = PlanSourceMapper.Pv(Totals(rising, setting), Warsaw, now);

        var (first, second, _) = DaySums(pv, month, day);
        Assert.Equal(Today, first, 6);
        Assert.Equal(Tomorrow, second, 6);
    }

    [Fact]
    public void A_stale_total_is_zero_pv_for_its_day_never_the_last_known_total()
    {
        // Why: PLAN-06: a stale forecast carries no content and is not used. Only today's total is stale here; tomorrow's still counts.
        var forecast = Totals(Utc(10, 30, 5, 32), Utc(10, 29, 15, 45), today: SignalState.Stale);

        var pv = PlanSourceMapper.Pv(forecast, Warsaw, Utc(10, 29, 10, 0));

        var (first, second, _) = DaySums(pv, 10, 29);
        Assert.Equal((PvBasis.Totals, 0.0), (pv.Basis, first));
        Assert.Equal(Tomorrow, second, 6);
    }

    [Fact]
    public void Both_totals_stale_is_no_forecast_at_all()
    {
        var forecast = Totals(Utc(10, 30, 5, 32), Utc(10, 29, 15, 45), today: SignalState.Stale, tomorrow: SignalState.Unavailable);

        var pv = PlanSourceMapper.Pv(forecast, Warsaw, Utc(10, 29, 10, 0));

        Assert.Same(PvForecast.None, pv);
        Assert.Equal(PvBasis.None, pv.Basis);
    }

    [Theory]
    [InlineData("stale", "fresh")]
    [InlineData("fresh", "stale")]
    [InlineData("absent", "fresh")]
    [InlineData("fresh", "absent")]
    [InlineData("absent", "absent")]
    public void Without_a_usable_sun_there_is_no_daylight_to_spread_the_totals_over_so_zero_pv(string rising, string setting)
    {
        // Why: a total with no sun times could only be spread over an invented day; the planner never guesses past a missing input.
        var samples = new List<(string, ForecastSample)>
        {
            ("pv_today", Total(SignalRole.PvForecastToday, Today)),
            ("pv_tomorrow", Total(SignalRole.PvForecastTomorrow, Tomorrow)),
        };
        if (rising != "absent")
        {
            samples.Add(("sun_rising", Sun(SignalRole.SunRising, Utc(10, 30, 5, 32), rising == "stale" ? SignalState.Stale : SignalState.Fresh)));
        }

        if (setting != "absent")
        {
            samples.Add(("sun_setting", Sun(SignalRole.SunSetting, Utc(10, 29, 15, 45), setting == "stale" ? SignalState.Stale : SignalState.Fresh)));
        }

        var pv = PlanSourceMapper.Pv(Inputs([.. samples]), Warsaw, Utc(10, 29, 10, 0));

        Assert.Same(PvForecast.None, pv);
    }

    [Fact]
    public void No_forecast_inputs_at_all_is_zero_pv()
    {
        Assert.Same(PvForecast.None, PlanSourceMapper.Pv(null, Warsaw, Utc(10, 29, 10, 0)));
        Assert.Same(PvForecast.None, PlanSourceMapper.Pv(ForecastInputs.Empty, Warsaw, Utc(10, 29, 10, 0)));
    }

    [Fact]
    public void A_sun_that_makes_no_day_of_24_hours_or_less_is_zero_pv_and_never_throws()
    {
        // Why: the domain refuses a rise after its set (ArgumentException); the mapper must turn what it cannot make sense of into "no forecast".
        var polar = Totals(Utc(10, 30, 5, 0), Utc(10, 30, 5, 0));

        var pv = PlanSourceMapper.Pv(polar, Warsaw, Utc(10, 29, 10, 0));

        Assert.Equal(PvBasis.None, pv.Basis);
    }

    [Fact]
    public void A_negative_total_is_refused_whole()
    {
        var forecast = Inputs(
            ("pv_today", Total(SignalRole.PvForecastToday, -1)),
            ("sun_rising", Sun(SignalRole.SunRising, Utc(10, 30, 5, 32))),
            ("sun_setting", Sun(SignalRole.SunSetting, Utc(10, 29, 15, 45))));

        Assert.Equal(PvBasis.None, PlanSourceMapper.Pv(forecast, Warsaw, Utc(10, 29, 10, 0)).Basis);
    }

    private static ForecastSample Series(SignalState state = SignalState.Fresh) =>
        new(
            SignalRole.PvForecast,
            SignalKind.Series,
            state,
            state == SignalState.Fresh ? SignalDetail.None : SignalDetail.MaxAgeExceeded,
            Utc(10, 29, 0, 0),
            state == SignalState.Fresh ? new TimeSeries([.. Enumerable.Range(0, 24).Select(h => new SeriesPoint(Utc(10, 29, h, 0), null, 1000))], SeriesValueKind.PowerW) : null,
            null,
            null);

    [Fact]
    public void A_usable_series_wins_over_the_totals()
    {
        // Why: the totals are the degraded shape, used only when there is no series; beside a series they would double the energy.
        var forecast = new ForecastInputs(Totals(Utc(10, 30, 5, 32), Utc(10, 29, 15, 45)).Samples.Add(SignalName.Parse("pv_series"), Series()));

        var pv = PlanSourceMapper.Pv(forecast, Warsaw, Utc(10, 29, 10, 0));

        Assert.Equal(PvBasis.Series, pv.Basis);
    }

    [Fact]
    public void A_stale_series_is_not_used_and_the_totals_take_over()
    {
        var forecast = new ForecastInputs(Totals(Utc(10, 30, 5, 32), Utc(10, 29, 15, 45)).Samples.Add(SignalName.Parse("pv_series"), Series(SignalState.Stale)));

        var pv = PlanSourceMapper.Pv(forecast, Warsaw, Utc(10, 29, 10, 0));

        Assert.Equal(PvBasis.Totals, pv.Basis);
    }

    [Fact]
    public void Only_a_series_that_is_not_there_is_replaced_by_totals_a_broken_usable_one_is_zero_pv()
    {
        // Why: a usable series the domain refuses (a negative watt) is "unusable, never repaired"; falling to the totals would hide a broken source.
        var broken = Series() with { Series = new TimeSeries([new SeriesPoint(Utc(10, 29, 10, 0), null, -5)], SeriesValueKind.PowerW) };
        var forecast = new ForecastInputs(Totals(Utc(10, 30, 5, 32), Utc(10, 29, 15, 45)).Samples.Add(SignalName.Parse("pv_series"), broken));

        Assert.Equal(PvBasis.None, PlanSourceMapper.Pv(forecast, Warsaw, Utc(10, 29, 10, 0)).Basis);
    }
}
