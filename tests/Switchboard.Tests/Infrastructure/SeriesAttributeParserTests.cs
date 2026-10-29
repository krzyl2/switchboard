using System.Text.Json;
using Switchboard.Application.Signals;
using Switchboard.Domain.Signals;
using Switchboard.Infrastructure.HomeAssistant;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// PLAN-06 and PLAN-07, the transport half (15-PLANNER, "What the signal layer needs that it does not have"): one whitelisted attribute becomes a
/// bounded, time-sorted series, or a named problem; never a truncated series, never a series with a hole in it, never a time guessed to be local.
/// The fixtures are the shapes the design names: a clear day, an overcast day, a late shift, a dict and a list, an oversized frame, a malformed point.
/// </summary>
public sealed class SeriesAttributeParserTests
{
    private static readonly SeriesSpec Dict = new("watts", SeriesShape.Dict, "start", "end", "value");
    private static readonly SeriesSpec List = new("forecast", SeriesShape.Auto, "start", "end", "value");
    private static readonly SeriesSpec Solcast = new("detailedForecast", SeriesShape.List, "period_start", null, "pv_estimate");

    private static ParsedSeries Parse(string json, SeriesSpec spec)
    {
        using var document = JsonDocument.Parse(json);
        return SeriesAttributeParser.Parse(document.RootElement, spec);
    }

    private static string DictOf(params (string Time, double Value)[] points) =>
        "{" + string.Join(",", points.Select(p => FormattableString.Invariant($"\"{p.Time}\": {p.Value}"))) + "}";

    private static string Hour(int h) => $"2026-10-23T{h:00}:00:00+00:00";

    [Fact]
    public void A_clear_day_as_a_dict_is_read_sorted_with_raw_watts()
    {
        // Why: a dict has no order of its own (JSON objects are unordered); the planner integrates in time order, so the parser sorts.
        var series = Parse(DictOf((Hour(12), 3600), (Hour(10), 2400), (Hour(11), 3300)), Dict);

        Assert.Equal(SeriesProblem.None, series.Problem);
        Assert.Equal([2400, 3300, 3600], series.Points.Select(p => p.Value));
        Assert.Equal(Hour(10), series.Points[0].Start.ToString("yyyy-MM-dd'T'HH:mm:ssK"));
    }

    [Fact]
    public void An_overcast_day_with_zeros_and_a_late_shift_in_a_list_keeps_every_point_and_the_end_times()
    {
        // Why: zeros are a forecast (no sun), not a gap; a late shift is only different numbers. Nothing is dropped or interpolated.
        var json = """
            [
              {"start": "2026-10-23T06:00:00+02:00", "end": "2026-10-23T07:00:00+02:00", "value": 0},
              {"start": "2026-10-23T07:00:00+02:00", "end": "2026-10-23T08:00:00+02:00", "value": 120.5},
              {"start": "2026-10-23T08:00:00+02:00", "value": 0}
            ]
            """;

        var series = Parse(json, List);

        Assert.Equal(SeriesProblem.None, series.Problem);
        Assert.Equal([0, 120.5, 0], series.Points.Select(p => p.Value));
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 6, 0, 0, TimeSpan.Zero), series.Points[1].End);
        Assert.Null(series.Points[2].End);
    }

    [Fact]
    public void Item_keys_are_the_ones_the_signal_names()
    {
        var json = """[{"period_start": "2026-10-23T10:00:00Z", "pv_estimate": 1.5}, {"period_start": "2026-10-23T10:30:00Z", "pv_estimate": 2.5}]""";

        Assert.Equal([1.5, 2.5], Parse(json, Solcast).Points.Select(p => p.Value));
        Assert.Equal(SeriesProblem.Malformed, Parse(json, List).Problem);
    }

    [Fact]
    public void A_declared_shape_that_does_not_match_the_data_is_refused()
    {
        Assert.Equal(SeriesProblem.Malformed, Parse("""[{"start": "2026-10-23T10:00:00Z", "value": 1}]""", Dict).Problem);
        Assert.Equal(SeriesProblem.Malformed, Parse(DictOf((Hour(10), 1)), Solcast).Problem);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("\"3412\"")]
    [InlineData("42")]
    public void An_attribute_that_is_empty_or_not_a_list_or_mapping_is_missing(string json)
    {
        Assert.Equal(SeriesProblem.Missing, Parse(json, List).Problem);
    }

    [Fact]
    public void More_than_512_points_drops_the_whole_frame_never_a_truncated_series()
    {
        // Why: the bound exists so a hostile or broken attribute cannot cost memory; a series cut at 512 would silently end the forecast hours early.
        var start = new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
        string Of(int n) => "[" + string.Join(",", Enumerable.Range(0, n).Select(i => FormattableString.Invariant($"{{\"start\": \"{start.AddMinutes(i):yyyy-MM-ddTHH:mm:ssZ}\", \"value\": 1}}"))) + "]";

        Assert.Equal(512, Parse(Of(512), List).Points.Length);
        var oversized = Parse(Of(513), List);

        Assert.Equal((SeriesProblem.TooLarge, 0), (oversized.Problem, oversized.Points.Length));
    }

    [Fact]
    public void More_than_64_kib_drops_the_whole_frame_even_with_few_points()
    {
        var padding = new string('x', TimeSeries.MaxBytes);
        var json = $$"""[{"start": "2026-10-23T10:00:00Z", "value": 1, "note": "{{padding}}"}]""";

        Assert.Equal(SeriesProblem.TooLarge, Parse(json, List).Problem);
    }

    [Fact]
    public void The_size_is_counted_in_utf8_bytes_not_characters()
    {
        // Why: the bound is on what Home Assistant sent, which is bytes; multi-byte text must not slip a frame under it.
        string Of(char c) => $$"""[{"start": "2026-10-23T10:00:00Z", "value": 1, "note": "{{new string(c, 40000)}}"}]""";

        Assert.Equal(SeriesProblem.None, Parse(Of('x'), List).Problem);
        Assert.Equal(SeriesProblem.TooLarge, Parse(Of('ł'), List).Problem);
    }

    [Theory]
    [InlineData("""[{"start": "2026-10-23T10:00:00Z", "value": 1}, {"start": "yesterday", "value": 2}]""")]
    [InlineData("""[{"start": "2026-10-23T10:00:00Z", "value": 1}, {"start": "2026-10-23T11:00:00", "value": 2}]""")]
    [InlineData("""[{"start": "2026-10-23T10:00:00Z", "value": 1}, {"start": "2026-10-23T11:00:00Z", "value": "2"}]""")]
    [InlineData("""[{"start": "2026-10-23T10:00:00Z", "value": 1}, {"start": "2026-10-23T11:00:00Z"}]""")]
    [InlineData("""[{"start": "2026-10-23T10:00:00Z", "value": 1}, {"value": 2}]""")]
    [InlineData("""[{"start": "2026-10-23T10:00:00Z", "value": 1}, 7]""")]
    [InlineData("""[{"start": "2026-10-23T10:00:00Z", "value": 1}, {"start": "2026-10-23T10:00:00Z", "value": 2}]""")]
    [InlineData("""[{"start": "2026-10-23T10:00:00Z", "end": "2026-10-23T09:00:00Z", "value": 1}]""")]
    [InlineData("""[{"start": "2026-10-23T10:00:00Z", "end": 5, "value": 1}]""")]
    [InlineData("""[{"start": "2026-10-23T10:00:00Z", "value": 1e999}]""")]
    public void One_malformed_item_refuses_the_whole_series(string json)
    {
        // Why: a series with a hole in it would be planned on as if the hole were zero PV or a free hour. A time without an offset is refused because
        // the add-on's zone is not the installation's, and a duplicate time has no single answer.
        var series = Parse(json, List);

        Assert.Equal((SeriesProblem.Malformed, 0), (series.Problem, series.Points.Length));
    }

    [Fact]
    public void A_dict_with_a_malformed_key_or_value_is_refused_whole()
    {
        Assert.Equal(SeriesProblem.Malformed, Parse("""{"2026-10-23T10:00:00Z": 1, "noon": 2}""", Dict).Problem);
        Assert.Equal(SeriesProblem.Malformed, Parse("""{"2026-10-23T10:00:00Z": 1, "2026-10-23T11:00:00Z": null}""", Dict).Problem);
    }

    [Fact]
    public void A_whitelisted_attribute_is_the_only_one_the_subscription_parser_keeps()
    {
        // Why: the whitelist is the bound. Of a state with many attributes only the named one is read; a diff replaces it, a removal empties it,
        // and an entity with no series spec never produces a series.
        var pv = EntityId.Parse("sensor.pv_forecast_watts");
        var plain = EntityId.Parse("sensor.other");
        var parser = new SubscribeEntitiesParser([pv, plain], new Dictionary<EntityId, SeriesSpec> { [pv] = Dict });

        var json = "{\"a\": {"
            + "\"sensor.pv_forecast_watts\": {\"s\": \"3412\", \"a\": {\"watts\": " + DictOf((Hour(10), 1000), (Hour(11), 2000)) + ", \"unit_of_measurement\": \"W\", \"huge\": [1,2,3]}, \"lc\": 1700000000, \"lr\": 1700000000},"
            + "\"sensor.other\": {\"s\": \"1\", \"a\": {\"watts\": " + DictOf((Hour(10), 5)) + "}, \"lc\": 1700000000}"
            + "}}";
        var initial = parser.Parse(JsonDocument.Parse(json).RootElement, out var problem)!;

        Assert.Null(problem);
        Assert.Equal([1000, 2000], initial.Updates.Single(u => u.Entity == pv).Series!.Points.Select(p => p.Value));
        Assert.Null(initial.Updates.Single(u => u.Entity == plain).Series);

        var diff = "{\"c\": {\"sensor.pv_forecast_watts\": {\"+\": {\"s\": \"3500\", \"lc\": 1700000100, \"a\": {\"watts\": " + DictOf((Hour(10), 7)) + "}}}}}";
        var replaced = parser.Parse(JsonDocument.Parse(diff).RootElement, out _)!;
        Assert.Equal([7], replaced.Updates.Single().Series!.Points.Select(p => p.Value));

        var untouched = parser.Parse(JsonDocument.Parse("""{"c": {"sensor.pv_forecast_watts": {"+": {"lr": 1700000200}}}}""").RootElement, out _)!;
        Assert.Equal([7], untouched.Updates.Single().Series!.Points.Select(p => p.Value));

        var removed = parser.Parse(JsonDocument.Parse("""{"c": {"sensor.pv_forecast_watts": {"-": {"a": ["watts"]}, "+": {"lr": 1700000300}}}}""").RootElement, out _)!;
        Assert.Equal(SeriesProblem.Missing, removed.Updates.Single().Series!.Problem);
    }

    [Fact]
    public void The_text_of_a_declared_attribute_is_kept_for_a_sun_time_and_no_other_attribute_is()
    {
        // Why: the state of the sun entity is above_horizon; its instants live in next_rising and next_setting. Only the declared names are read (a bounded
        // whitelist), a diff replaces one, a removal empties it, a value that is not text or is oversized reads as empty, and an entity with none declared carries none.
        var sun = EntityId.Parse("sun.sun");
        var plain = EntityId.Parse("sensor.other");
        var names = System.Collections.Immutable.ImmutableArray.Create("next_rising", "next_setting");
        var parser = new SubscribeEntitiesParser([sun, plain], null, new Dictionary<EntityId, System.Collections.Immutable.ImmutableArray<string>> { [sun] = names });

        var json = """
            {"a": {
              "sun.sun": {"s": "above_horizon", "a": {"next_rising": "2026-10-30T05:32:11.1+00:00", "next_setting": 5, "elevation": 12.3, "next_dusk": "2026-10-29T16:20:00+00:00"}, "lc": 1700000000, "lr": 1700000000},
              "sensor.other": {"s": "1", "a": {"next_rising": "2026-10-30T05:32:11+00:00"}, "lc": 1700000000}
            }}
            """;
        var initial = parser.Parse(JsonDocument.Parse(json).RootElement, out var problem)!;

        Assert.Null(problem);
        var sunUpdate = initial.Updates.Single(u => u.Entity == sun);
        Assert.Equal(["next_rising", "next_setting"], sunUpdate.Attributes!.Keys.Order());
        Assert.Equal(("2026-10-30T05:32:11.1+00:00", string.Empty), (sunUpdate.Attributes["next_rising"], sunUpdate.Attributes["next_setting"]));
        Assert.Null(initial.Updates.Single(u => u.Entity == plain).Attributes);

        var diff = """{"c": {"sun.sun": {"+": {"lc": 1700000100, "a": {"next_setting": "2026-10-29T15:41:00+00:00"}}}}}""";
        var replaced = parser.Parse(JsonDocument.Parse(diff).RootElement, out _)!.Updates.Single().Attributes!;
        Assert.Equal(("2026-10-30T05:32:11.1+00:00", "2026-10-29T15:41:00+00:00"), (replaced["next_rising"], replaced["next_setting"]));

        var untouched = parser.Parse(JsonDocument.Parse("""{"c": {"sun.sun": {"+": {"lr": 1700000200}}}}""").RootElement, out _)!.Updates.Single().Attributes!;
        Assert.Equal("2026-10-29T15:41:00+00:00", untouched["next_setting"]);

        var removed = parser.Parse(JsonDocument.Parse("""{"c": {"sun.sun": {"-": {"a": ["next_rising"]}, "+": {"lr": 1700000300}}}}""").RootElement, out _)!.Updates.Single().Attributes!;
        Assert.Equal((string.Empty, "2026-10-29T15:41:00+00:00"), (removed["next_rising"], removed["next_setting"]));

        var oversized = "{\"c\": {\"sun.sun\": {\"+\": {\"lc\": 1700000400, \"a\": {\"next_rising\": \"" + new string('x', EntityUpdate.MaxAttributeText + 1) + "\"}}}}}";
        Assert.Equal(string.Empty, parser.Parse(JsonDocument.Parse(oversized).RootElement, out _)!.Updates.Single().Attributes!["next_rising"]);
    }

    [Fact]
    public void An_entity_without_the_attribute_at_the_initial_state_is_a_missing_series_not_a_dropped_state()
    {
        var pv = EntityId.Parse("sensor.pv_forecast_watts");
        var parser = new SubscribeEntitiesParser([pv], new Dictionary<EntityId, SeriesSpec> { [pv] = Dict });

        var batch = parser.Parse(JsonDocument.Parse("""{"a": {"sensor.pv_forecast_watts": {"s": "3412", "a": {}, "lc": 1700000000}}}""").RootElement, out var problem)!;

        Assert.Null(problem);
        Assert.Equal((SeriesProblem.Missing, "3412"), (batch.Updates.Single().Series!.Problem, batch.Updates.Single().State));
    }
}
