using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Switchboard.Application.Planning;
using Switchboard.Application.Signals;
using Switchboard.Domain.Forecast;
using Switchboard.Domain.Planning;
using Switchboard.Domain.Signals;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// The real Forecast.Solar <c>watts</c> response as the attribute of a template sensor (<c>sensor.pv_forecast_watts</c>, the three planes summed by key): a dict of
/// the site's LOCAL time without an offset ("2026-10-29 07:00:00") to instantaneous W, unsorted after summing, with irregular sunrise and sunset points at 0 W.
/// Why it matters: a time read as UTC would shift the whole forecast by one or two hours (PV planned into the night), a time read in the server's zone would do
/// it silently; an hour that does not exist or happens twice must not throw or leave a gap in the integral. The zone is the add-on's, the one of the slot grid.
/// </summary>
public sealed class ForecastSolarSeriesTests
{
    private const string Entity = "sensor.pv_forecast_watts";

    private const string Signals = """
          pv_forecast_series:
            entity: sensor.pv_forecast_watts
            kind: series
            attribute: watts
            series: { shape: dict, value: power_w }
            max_age: 3h
            role: pv_forecast

        """;

    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly SeriesSpec Dict = new("watts", SeriesShape.Dict, "start", "end", "value");
    private static readonly ForecastParameters NoDerate = new(TimeSpan.FromHours(1), 1.0, TimeSpan.FromDays(14), 0.8, 400);

    // The three planes of one site on 2026-10-29 and 2026-10-30, watts by local time. The sunrise and sunset edges differ per plane and are 0 W; plane C has
    // no 07:00 and no 16:00 key, plane B no key at 08:00 and 09:00 of the second day: the sum takes the union of keys, a missing key counts as 0.
    private static readonly Dictionary<string, double> PlaneA = new()
    {
        ["2026-10-29 06:28:42"] = 0, ["2026-10-29 07:00:00"] = 162, ["2026-10-29 08:00:00"] = 465, ["2026-10-29 09:00:00"] = 700, ["2026-10-29 10:00:00"] = 900,
        ["2026-10-29 11:00:00"] = 1000, ["2026-10-29 12:00:00"] = 1000, ["2026-10-29 13:00:00"] = 900, ["2026-10-29 14:00:00"] = 700, ["2026-10-29 15:00:00"] = 300,
        ["2026-10-29 16:00:00"] = 54, ["2026-10-29 16:26:43"] = 0,
        ["2026-10-30 06:30:21"] = 0, ["2026-10-30 07:00:00"] = 195, ["2026-10-30 08:00:00"] = 500, ["2026-10-30 09:00:00"] = 700,
    };

    private static readonly Dictionary<string, double> PlaneB = new()
    {
        ["2026-10-29 06:29:10"] = 0, ["2026-10-29 07:00:00"] = 100, ["2026-10-29 08:00:00"] = 300, ["2026-10-29 09:00:00"] = 450, ["2026-10-29 10:00:00"] = 560,
        ["2026-10-29 11:00:00"] = 620, ["2026-10-29 12:00:00"] = 620, ["2026-10-29 13:00:00"] = 560, ["2026-10-29 14:00:00"] = 450, ["2026-10-29 15:00:00"] = 200,
        ["2026-10-29 16:00:00"] = 30, ["2026-10-29 16:25:50"] = 0,
        ["2026-10-30 06:31:00"] = 0, ["2026-10-30 07:00:00"] = 100,
    };

    private static readonly Dictionary<string, double> PlaneC = new()
    {
        ["2026-10-29 06:28:15"] = 0, ["2026-10-29 08:00:00"] = 200, ["2026-10-29 09:00:00"] = 300, ["2026-10-29 10:00:00"] = 380, ["2026-10-29 11:00:00"] = 420,
        ["2026-10-29 12:00:00"] = 420, ["2026-10-29 13:00:00"] = 380, ["2026-10-29 14:00:00"] = 300, ["2026-10-29 15:00:00"] = 120, ["2026-10-29 16:27:30"] = 0,
    };

    private static ParsedSeries Parse(string json, TimeZoneInfo? zone)
    {
        using var document = JsonDocument.Parse(json);
        return SeriesAttributeParser.Parse(document.RootElement, Dict, zone);
    }

    private static DateTimeOffset LocalMidnight(int month, int day)
    {
        var local = new DateTime(2026, month, day, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Warsaw.GetUtcOffset(local));
    }

    private static ForecastInputs Forecast(ParsedSeries read)
    {
        var series = new TimeSeries(read.Points, SeriesValueKind.PowerW);
        var sample = new ForecastSample(SignalRole.PvForecast, SignalKind.Series, SignalState.Fresh, SignalDetail.None, null, series, null, null);
        return new(ImmutableDictionary<SignalName, ForecastSample>.Empty.Add(SignalName.Parse("pv"), sample));
    }

    /// <summary>What the template sensor produces: the union of the keys, planes summed, in descending key order (not sorted), written as floats on every other key.</summary>
    private static string SummedWatts()
    {
        var sum = new[] { PlaneA, PlaneB, PlaneC }.SelectMany(p => p).GroupBy(p => p.Key).OrderByDescending(g => g.Key, StringComparer.Ordinal);
        return "{" + string.Join(",", sum.Select((g, i) => string.Create(CultureInfo.InvariantCulture, $"\"{g.Key}\": {g.Sum(p => p.Value)}{(i % 2 == 0 ? ".0" : string.Empty)}"))) + "}";
    }

    private static string EventBody(StoreHarness store) =>
        "{\"a\": {\"" + Entity + "\": {\"s\": \"1234\", \"a\": {\"watts\": " + SummedWatts() + ", \"unit_of_measurement\": \"W\"}, \"lc\": "
        + store.Now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + "}}}";

    private static StoreHarness Pushed()
    {
        var store = new StoreHarness(TestConfigurations.ThreePhase(Signals));
        store.AdvanceTo(new DateTimeOffset(2026, 10, 29, 5, 0, 0, TimeSpan.Zero)); // 06:00 in Warsaw (CET)
        var id = EntityId.Parse(Entity);
        var parser = new SubscribeEntitiesParser([id], new Dictionary<EntityId, SeriesSpec> { [id] = Dict }, null, Warsaw);
        var batch = parser.Parse(JsonDocument.Parse(EventBody(store)).RootElement, out var problem)!;
        Assert.Null(problem);
        store.Store.Apply(batch, store.Now);
        return store;
    }

    [Fact]
    public void A_time_without_an_offset_is_the_wall_clock_of_the_given_zone_and_is_refused_without_one()
    {
        // Why: the keys carry no offset; the zone is the add-on's (as the slot grid), never UTC and never guessed when there is none.
        var json = """{"2026-10-29 16:00:00": 54, "2026-10-29 07:00:00": 162}""";

        var read = Parse(json, Warsaw);

        Assert.Equal(SeriesProblem.None, read.Problem);
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 6, 0, 0, TimeSpan.Zero), read.Points[0].Start); // 07:00 CET
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 15, 0, 0, TimeSpan.Zero), read.Points[1].Start);
        Assert.Equal([162, 54], read.Points.Select(p => p.Value));
        Assert.Equal(SeriesProblem.Malformed, Parse(json, null).Problem);
    }

    [Fact]
    public void The_same_wall_clock_is_a_different_instant_in_summer_and_in_winter()
    {
        // Why: the offset comes from the zone's rules for that date (CEST +02:00 before 2026-10-25, CET +01:00 after), not from the day the add-on started.
        var read = Parse("""{"2026-10-24 12:00:00": 1, "2026-10-26 12:00:00": 2}""", Warsaw);

        Assert.Equal([new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 26, 11, 0, 0, TimeSpan.Zero)], read.Points.Select(p => p.Start.ToUniversalTime()));
    }

    [Theory]
    [InlineData("2026-10-25 02:30:00", 2026, 10, 25, 0, 30)] // happens twice: the first (CEST, +02:00) is 00:30 UTC
    [InlineData("2026-03-29 02:30:00", 2026, 3, 29, 1, 30)] // does not exist: moved to the first valid moment, 03:30 CEST = 01:30 UTC
    public void An_ambiguous_or_nonexistent_local_time_is_read_deterministically_and_does_not_throw(string key, int year, int month, int day, int hour, int minute)
    {
        var read = Parse($$"""{"{{key}}": 10}""", Warsaw);

        Assert.Equal(SeriesProblem.None, read.Problem);
        Assert.Equal(new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero), read.Points[0].Start.ToUniversalTime());
    }

    [Theory]
    [InlineData(10, 25, 26, 25)]
    [InlineData(3, 29, 30, 23)]
    public void A_constant_forecast_over_a_dst_day_integrates_to_one_kwh_per_real_hour(int month, int day, int nextDay, int realHours)
    {
        // Why: the 25-hour day repeats 02:00 (the key exists once and means the first one) and the 23-hour day has no 02:00 (a provider emits no key for it).
        // Either way every real hour must be covered once: no gap, no double count (1 kW all day is 25 and 23 kWh).
        var hours = Enumerable.Range(0, 24).Where(h => !(month == 3 && h == 2)).Select(h => $"\"2026-{month:00}-{day:00} {h:00}:00:00\": 1000");
        var json = "{" + string.Join(",", hours.Append($"\"2026-{month:00}-{nextDay:00} 00:00:00\": 1000")) + "}";

        var read = Parse(json, Warsaw);
        var pv = PlanSourceMapper.Pv(Forecast(read), Warsaw, LocalMidnight(month, day));
        var grid = SlotGrid.Build(LocalMidnight(month, day), LocalMidnight(month, nextDay), TimeSpan.FromHours(1), Warsaw);

        Assert.Equal(SeriesProblem.None, read.Problem);
        Assert.Equal(realHours, grid.Slots.Length);
        Assert.Equal(realHours, pv.Slots(grid, NoDerate).Kwh.Sum(), 9);
    }

    [Fact]
    public void A_nonexistent_time_that_collides_with_a_real_one_refuses_the_series_rather_than_inventing_a_choice()
    {
        // Why: 02:30 on 2026-03-29 does not exist and is read as 03:30; a key for 03:30 as well is two values for one instant, which is never repaired.
        Assert.Equal(SeriesProblem.Malformed, Parse("""{"2026-03-29 02:30:00": 0, "2026-03-29 03:30:00": 0}""", Warsaw).Problem);
    }

    [Fact]
    public void The_summed_forecast_solar_dict_reaches_the_planner_as_the_trapezoid_of_its_points_in_the_site_zone()
    {
        // Why: end to end on a virtual clock, from the transport's JSON to the slot energies: local keys in Warsaw (not UTC), unsorted keys, int and float values,
        // edge points at 0 W that neither leave a gap (the ramp from the last 0 W point to the first hour) nor are counted twice.
        var store = Pushed();

        var pv = PlanSourceMapper.Pv(store.Snapshot().Forecast!, Warsaw, store.Now);
        var grid = SlotGrid.Build(LocalMidnight(10, 29), LocalMidnight(10, 31), TimeSpan.FromHours(1), Warsaw);
        var slots = pv.Slots(grid, NoDerate);
        var wh = slots.Kwh.Select(k => k * 1000).ToArray();

        Assert.Equal((PvBasis.Series, PvCoverage.Partial), (pv.Basis, slots.Coverage));
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero), pv.CoversUntil); // 09:00 CET, the last key
        Assert.Equal(48, wh.Length);
        Assert.All(wh.Take(6), w => Assert.Equal(0, w)); // 00:00-06:00
        double[] expected =
        [
            262 * 1850 / 7200.0, // 06-07: ramp from the latest 0 W point (06:29:10) to 262 W at 07:00
            613.5, 1207.5, 1645, 1940, 2040, 1940, 1645, 1035, 352, // 07-16: mean of the two hour values
            84 * 1550 / 7200.0, // 16-17: ramp from 84 W at 16:00 to the earliest 0 W point (16:25:50)
        ];
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], wh[6 + i], 6);
        }

        Assert.All(wh.Skip(17).Take(24 + 6 - 17), w => Assert.Equal(0, w)); // the night to 06:00 on the 30th
        Assert.Equal(295 * 1740 / 7200.0, wh[30], 6); // 06-07 on the 30th: ramp from 06:31:00
        Assert.Equal(397.5, wh[31], 6);
        Assert.Equal(600, wh[32], 6);
        Assert.All(wh.Skip(33), w => Assert.Equal(0, w)); // nothing beyond the last key
    }

    [Fact]
    public void A_forecast_that_is_not_refreshed_within_the_three_hour_max_age_is_no_forecast()
    {
        // Why: the source refreshes hourly; max_age 3h tolerates two missed refreshes, and past that the plan must not run on yesterday's sun.
        var store = Pushed();
        store.Advance(TimeSpan.FromHours(2));
        Assert.Equal(PvBasis.Series, PlanSourceMapper.Pv(store.Snapshot().Forecast, Warsaw, store.Now).Basis);

        store.Advance(TimeSpan.FromHours(1.5));

        Assert.Equal(PvBasis.None, PlanSourceMapper.Pv(store.Snapshot().Forecast, Warsaw, store.Now).Basis);
    }
}
