using System.Collections.Immutable;
using System.Text.Json;
using Switchboard.Application.Configuration;
using Switchboard.Application.Goals;
using Switchboard.Application.Planning;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Forecast;
using Switchboard.Domain.Planning;
using Switchboard.Domain.Signals;
using Switchboard.Host.Web;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// GET api/plan, slice 3 integration (15-PLANNER, API): the slots and the inputs they stand on. Why it matters: the day plan is read off these slots, so a
/// price that is not the tariff's at a slot boundary (above all on a 23- or 25-hour day), a stale forecast that still produces PV, or a missing input that is
/// not named would make the plan look more certain than the data is. Without a <c>planner:</c> section nothing of this appears.
/// </summary>
public sealed class PlanInputsEndpointTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly SignalName PvSignal = SignalName.Parse("pv_forecast_series");
    private static readonly SignalName CurveSignal = SignalName.Parse("price_curve");

    private const string Planner = "\nplanner:\n  dispatch: off\n";

    // Peak on the hour (07-13 and 16-21, every day) so a slot's price is one zone's price and the tariff can be asked at the slot start.
    private const string HourlyTariff =
        "\neconomics:\n  currency: PLN\n  tariff:\n    default_zone: offpeak\n    zones:\n      - id: peak\n        price: 0.40\n        when:\n          - { days: [workday, weekend], hours: [\"07:00-13:00\", \"16:00-21:00\"] }\n      - id: offpeak\n        price: 0.10\n        when: []\n";

    private const string CurveSignals = """
          market_price:
            entity: sensor.market_price
            kind: price
            unit: PLN/MWh
            max_age: 5m
            role: price_import
          price_curve:
            entity: sensor.price_curve
            kind: series
            attribute: raw_today
            unit: PLN/MWh
            max_age: 6h
            role: price_curve_import

        """;

    private const string CurveEconomics = "\neconomics:\n  currency: PLN\n  tariff:\n    signal: market_price\n    curve: price_curve\n    fallback: 0.9\n";

    private static DateTimeOffset Local(int year, int month, int day, int hour, int offsetHours) => new(year, month, day, hour, 0, 0, TimeSpan.FromHours(offsetHours));

    private static ConfigurationResult Config(string economics, string signals = "", string planner = Planner) =>
        TestConfigurations.Valid(TestConfigurations.ThreePhase(signals) + economics + planner);

    private static PlanResponse Build(ConfigurationResult config, DateTimeOffset at, ForecastInputs? forecast = null)
    {
        var sources = PlanSourceMapper.Map(config.Configuration!, forecast, LoadProfile.Empty, Warsaw);
        return PlanEndpoint.Build(config, GoalBoard.Empty(at), sources, forecast);
    }

    private static ForecastInputs Forecast(SignalName name, SignalRole role, SignalState state, DateTimeOffset? reported, TimeSeries? series) =>
        new(ImmutableDictionary<SignalName, ForecastSample>.Empty.Add(
            name,
            new ForecastSample(role, SignalKind.Series, state, state == SignalState.Fresh ? SignalDetail.None : SignalDetail.MaxAgeExceeded, reported, series, null, null)));

    private static TimeSeries Power(DateTimeOffset start, int hours, double watts) =>
        new([.. Enumerable.Range(0, hours).Select(h => new SeriesPoint(start.AddHours(h), null, watts))], SeriesValueKind.PowerW);

    [Theory]
    [InlineData(2026, 3, 28, 12, 1)]
    [InlineData(2026, 10, 24, 12, 2)]
    public void Slots_equal_the_tariff_at_every_boundary_across_a_23_and_a_25_hour_day(int year, int month, int day, int hour, int offsetHours)
    {
        // Why: the plan's price per slot must be what dispatch pays. A 24-hour assumption on 2026-03-29 (23 h) or 2026-10-25 (25 h) would shift every price
        // after the change by an hour; the slot grid is cut on the zone's offset change and the slot's start and end carry the offset in force.
        var config = Config(HourlyTariff);
        var tariff = new Tariff(config.Configuration!.Economics!.Tariff!, HolidayCalendar.Empty, Warsaw);
        var at = Local(year, month, day, hour, offsetHours);

        var slots = Build(config, at).Slots;

        Assert.Equal(48, slots.Count);
        Assert.All(slots, s =>
        {
            Assert.Equal(tariff.PriceAt(s.Start)!.Value.PerKwh, s.ImportPrice!.Value, 9);
            Assert.Equal(tariff.ZoneAt(s.Start)!.Id, s.Zone);
            Assert.Equal(TimeSpan.FromHours(1), s.End - s.Start);
        });
        Assert.Equal(slots.SkipLast(1).Select(s => s.End), slots.Skip(1).Select(s => s.Start));
        var change = slots.Select(s => s.Start.Offset).Distinct().Count();
        Assert.Equal(2, change);
    }

    [Fact]
    public void The_25_hour_day_shows_its_repeated_hour_as_two_slots_with_different_offsets()
    {
        // Why: the UI draws the day as it is; the repeated 02:00 must be two slots (+02:00 then +01:00), not one slot of two hours or a missing hour.
        var slots = Build(Config(HourlyTariff), Local(2026, 10, 24, 12, 2)).Slots;

        var repeated = slots.Where(s => s.Start.Day == 25 && s.Start.Hour == 2).ToList();

        Assert.Equal([TimeSpan.FromHours(2), TimeSpan.FromHours(1)], repeated.Select(s => s.Start.Offset));
    }

    [Fact]
    public void Without_a_planner_section_there_are_no_slots_and_no_forecast_inputs()
    {
        // Why: a 0.10 file must answer exactly as before: not enabled, nothing computed. Even when sources are at hand the endpoint must not
        // present them as a plan.
        var config = TestConfigurations.Valid(TestConfigurations.ThreePhase() + HourlyTariff);
        var sources = PlanSourceMapper.Map(config.Configuration!, null, LoadProfile.Empty, Warsaw);

        var response = PlanEndpoint.Build(config, GoalBoard.Empty(Local(2026, 10, 24, 12, 2)), sources);

        Assert.False(response.Enabled);
        Assert.Empty(response.Slots);
        Assert.Empty(response.Notes);
        Assert.Null(response.Inputs.PvForecast);
        Assert.Null(response.Inputs.LoadForecast);
        Assert.Null(response.Inputs.Prices);
    }

    [Fact]
    public void A_missing_forecast_is_zero_pv_named_in_the_notes_and_in_the_basis()
    {
        // Why: the planner never guesses past a missing input (15-PLANNER, failure behaviour): no PV in any slot, and the answer says why.
        var response = Build(Config(HourlyTariff), Local(2026, 7, 15, 6, 2));

        Assert.Equal(["pv_forecast_missing"], response.Notes);
        Assert.Equal(new PvForecastDto("absent", null, null, "none", 0.85), response.Inputs.PvForecast);
        Assert.All(response.Slots, s => Assert.Equal(0, s.PvKwh));
    }

    [Fact]
    public void A_partial_forecast_is_derated_zero_beyond_its_end_and_noted_with_its_age()
    {
        // Why: coverage is not freshness. A forecast reported a minute ago that ends in 11 hours is fresh and partial: the 12 hours it covers are planned
        // (1 kW x 1 h x derate 0.85), the other 36 hours get zero PV and the note.
        var at = Local(2026, 7, 15, 6, 2);
        var forecast = Forecast(PvSignal, SignalRole.PvForecast, SignalState.Fresh, at.AddMinutes(-1), Power(at, 12, 1000));

        var response = Build(Config(HourlyTariff), at, forecast);

        Assert.Equal(["pv_forecast_partial"], response.Notes);
        Assert.Equal(new PvForecastDto("fresh", 60, at.AddHours(11), "series", 0.85), response.Inputs.PvForecast);
        Assert.Equal(0.85, response.Slots[0].PvKwh, 9);
        Assert.Equal(0.85, response.Slots[10].PvKwh, 9);
        Assert.Equal(0, response.Slots[12].PvKwh);
        Assert.Equal(0, response.Slots[47].PvKwh);
    }

    [Fact]
    public void A_stale_forecast_gives_no_pv_even_if_it_still_carries_its_last_series()
    {
        // Why: the last known forecast is never planned on (SIG-05, 15-PLANNER). The sample here is stale but, against the signal layer's own rule, still holds a
        // series: the mapper must read only a usable sample, so the plan has zero PV and says the forecast is missing, with the state and age shown.
        var at = Local(2026, 7, 15, 6, 2);
        var forecast = Forecast(PvSignal, SignalRole.PvForecast, SignalState.Stale, at.AddHours(-7), Power(at, 48, 1000));

        var response = Build(Config(HourlyTariff), at, forecast);

        Assert.Equal(["pv_forecast_missing"], response.Notes);
        Assert.Equal(new PvForecastDto("stale", 7 * 3600, null, "none", 0.85), response.Inputs.PvForecast);
        Assert.All(response.Slots, s => Assert.Equal(0, s.PvKwh));
    }

    [Fact]
    public void A_period_energy_series_without_an_end_covers_until_its_last_start_as_the_signal_layer_says()
    {
        // Why: TimeSeries.CoversUntil is conservative for an open-ended last point (its start). The plan must not assume an hour the signal layer does not
        // claim, so the last open-ended period is left out and the coverage is the same instant in both places.
        var at = Local(2026, 7, 15, 6, 2);
        var series = new TimeSeries([.. Enumerable.Range(0, 3).Select(h => new SeriesPoint(at.AddHours(h), null, 1000))], SeriesValueKind.EnergyWhPeriod);
        var forecast = Forecast(PvSignal, SignalRole.PvForecast, SignalState.Fresh, at, series);

        var response = Build(Config(HourlyTariff), at, forecast);

        Assert.Equal(series.CoversUntil, response.Inputs.PvForecast!.CoversUntil);
        Assert.Equal(0.85, response.Slots[0].PvKwh, 9);
        Assert.Equal(0.85, response.Slots[1].PvKwh, 9);
        Assert.Equal(0, response.Slots[2].PvKwh);
    }

    [Fact]
    public void A_curve_prices_the_slots_it_covers_and_the_fallback_tariff_the_rest_with_a_note()
    {
        // Why: a price curve that ends in six hours must not leave the later slots unpriced or priced as if it continued: the spec's fallback (0.90) prices
        // them, the basis says partial and the note is raised.
        var at = Local(2026, 7, 15, 6, 2);
        var curve = new TimeSeries([.. Enumerable.Range(0, 6).Select(h => new SeriesPoint(at.AddHours(h), at.AddHours(h + 1), 0.5))], SeriesValueKind.PricePerKwh);
        var forecast = Forecast(CurveSignal, SignalRole.PriceCurveImport, SignalState.Fresh, at, curve);

        var response = Build(Config(CurveEconomics, CurveSignals), at, forecast);

        Assert.Contains("price_curve_partial", response.Notes);
        Assert.Equal(new PlanPricesDto("partial", at.AddHours(6)), response.Inputs.Prices);
        Assert.Equal(0.5, response.Slots[5].ImportPrice!.Value, 9);
        Assert.Equal(0.9, response.Slots[6].ImportPrice!.Value, 9);
    }

    [Fact]
    public void A_stale_curve_is_never_used_the_tariff_prices_every_slot()
    {
        // Why: the last known curve must not price tomorrow. Stale (with content, against the signal layer's own rule) means every slot is priced from the
        // spec's fallback and the plan says the curve is partial.
        var at = Local(2026, 7, 15, 6, 2);
        var curve = new TimeSeries([new SeriesPoint(at, at.AddHours(48), 0.5)], SeriesValueKind.PricePerKwh);
        var forecast = Forecast(CurveSignal, SignalRole.PriceCurveImport, SignalState.Stale, at.AddHours(-7), curve);

        var response = Build(Config(CurveEconomics, CurveSignals), at, forecast);

        Assert.Equal(new PlanPricesDto("tariff", null), response.Inputs.Prices);
        Assert.Contains("price_curve_partial", response.Notes);
        Assert.All(response.Slots, s => Assert.Equal(0.9, s.ImportPrice!.Value, 9));
    }

    [Fact]
    public void The_enabled_answer_has_the_documented_keys_in_camel_case()
    {
        // Why: the SPA reads these names (slice 7); a renamed key would blank the day plan without a build error.
        var json = JsonSerializer.SerializeToElement(Build(Config(HourlyTariff), Local(2026, 7, 15, 6, 2)), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(["goalsActive", "goalsInactive", "loadForecast", "prices", "pvForecast"], json.GetProperty("inputs").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["ageS", "basis", "coversUntil", "derate", "state"], json.GetProperty("inputs").GetProperty("pvForecast").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["basis", "daysFilled"], json.GetProperty("inputs").GetProperty("loadForecast").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["baseKwh", "end", "exportPrice", "importPrice", "pvKwh", "start", "surplusKwh", "zone"], json.GetProperty("slots")[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("fallback", json.GetProperty("inputs").GetProperty("loadForecast").GetProperty("basis").GetString());
    }
}
