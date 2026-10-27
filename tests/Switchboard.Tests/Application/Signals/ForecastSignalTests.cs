using Switchboard.Application.Configuration;
using Switchboard.Application.Signals;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Forecast;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Signals;

/// <summary>
/// PLAN-06 and PLAN-07 in the signal layer (15-PLANNER, slice 2): a forecast series, a day total, a sun time and a price curve become typed
/// values here and nowhere else, only while the entity is fresh (<c>last_reported</c> and <c>max_age</c>, SIG-05, SIG-06), and coverage is
/// judged apart from freshness. What is not usable carries no content, so the planner can never plan on a last known curve. All of it travels
/// apart from protection and dispatch (SIG-04 by type).
/// </summary>
public sealed class ForecastSignalTests
{
    public const string PvEntity = "sensor.pv_forecast_watts";
    private const string TotalEntity = "sensor.pv_today";
    private const string SunEntity = "sensor.sun_next_setting";
    private const string CurveEntity = "sensor.price_curve";

    public static string PvSeries(string maxAge = "6h") => $"""
          pv_forecast_series:
            entity: {PvEntity}
            kind: series
            attribute: watts
            series:
              shape: dict
              value: power_w
            max_age: {maxAge}
            role: pv_forecast

        """;

    private static string Others(string maxAge = "6h") => $"""
          pv_today:
            entity: {TotalEntity}
            kind: energy_forecast
            unit: Wh
            max_age: {maxAge}
            role: pv_forecast_today
          sun_setting:
            entity: {SunEntity}
            kind: timestamp
            max_age: 24h
            role: sun_setting
          price_curve:
            entity: {CurveEntity}
            kind: series
            attribute: raw_today
            unit: PLN/MWh
            max_age: {maxAge}
            role: price_curve_import

        """;

    private const string Economics = "\neconomics:\n  currency: PLN\n  tariff:\n    signal: market_price\n    curve: price_curve\n    fallback: 0.9\n";

    private const string MarketPrice = """
          market_price:
            entity: sensor.market_price
            kind: price
            unit: PLN/MWh
            max_age: 5m
            role: price_import

        """;

    private static readonly SignalName Pv = SignalName.Parse("pv_forecast_series");

    /// <summary>A bell-shaped hourly power series of <paramref name="hours"/> points starting at <paramref name="start"/>.</summary>
    public static ParsedSeries Hourly(DateTimeOffset start, int hours) =>
        new([.. Enumerable.Range(0, hours).Select(h => new SeriesPoint(start.AddHours(h), null, Math.Round(4000 * Math.Sin(Math.PI * (h + 0.5) / hours))))], SeriesProblem.None);

    private static StoreHarness Store() => new(TestConfigurations.ThreePhase(PvSeries() + Others() + MarketPrice) + Economics);

    private static SignalDefinition Definition(SignalKind kind, string? unit, SeriesValueKind? meaning = null, string? currency = "PLN", SignalRole role = SignalRole.None) =>
        new(SignalName.Parse("forecast"), EntityId.Parse(PvEntity), kind, unit, null, null, false, TimeSpan.FromHours(6), null, role, null, false) { PriceCurrency = currency, SeriesMeaning = meaning };

    private static readonly DateTimeOffset Noon = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_power_series_becomes_a_typed_series_value_with_its_meaning_from_the_declaration()
    {
        // Why: the numbers of a PV forecast mean W or Wh only because the configuration says so; the data never decides.
        var result = SignalNormalizer.Normalize(Definition(SignalKind.Series, null, SeriesValueKind.PowerW), "3412", null, Hourly(Noon, 6));

        Assert.Equal(NormalizationOutcome.Accepted, result.Outcome);
        var series = ((SeriesValue)result.Value!).Series;
        Assert.Equal((SeriesValueKind.PowerW, 6, Noon.AddHours(5)), (series.Kind, series.Points.Length, series.CoversUntil));
    }

    [Theory]
    [InlineData(SeriesProblem.Missing, SignalDetail.SeriesMissing)]
    [InlineData(SeriesProblem.Malformed, SignalDetail.SeriesMalformed)]
    [InlineData(SeriesProblem.TooLarge, SignalDetail.SeriesTooLarge)]
    public void A_series_the_transport_refused_is_rejected_with_its_reason_and_never_partial(SeriesProblem problem, SignalDetail detail)
    {
        // Why: a malformed item refuses the whole series and an oversized frame is dropped whole; a series with a hole in it would be planned on.
        var result = SignalNormalizer.Normalize(Definition(SignalKind.Series, null, SeriesValueKind.PowerW), "3412", null, ParsedSeries.Of(problem));

        Assert.Equal((NormalizationOutcome.Rejected, null, detail), (result.Outcome, result.Value, result.Detail));
    }

    [Fact]
    public void A_negative_power_point_refuses_the_whole_series()
    {
        // Why: a forecast of less than nothing is a broken source, not a number to clamp.
        var series = new ParsedSeries([new SeriesPoint(Noon, null, 100), new SeriesPoint(Noon.AddHours(1), null, -5)], SeriesProblem.None);

        var result = SignalNormalizer.Normalize(Definition(SignalKind.Series, null, SeriesValueKind.PowerW), "1", null, series);

        Assert.Equal((NormalizationOutcome.Rejected, SignalDetail.SeriesMalformed), (result.Outcome, result.Detail));
    }

    [Fact]
    public void A_price_curve_is_per_kwh_after_the_signal_layer_and_may_be_negative()
    {
        // Why: ECO-10's rule for the forward prices: 450 per MWh is exactly 0.45 per kWh, converted here and nowhere else; a market publishes negative hours.
        var curve = new ParsedSeries([new SeriesPoint(Noon, Noon.AddHours(1), 450), new SeriesPoint(Noon.AddHours(1), Noon.AddHours(2), -12)], SeriesProblem.None);

        var result = SignalNormalizer.Normalize(Definition(SignalKind.Series, "PLN/MWh", SeriesValueKind.PricePerKwh, role: SignalRole.PriceCurveImport), "0.45", null, curve);

        var series = ((SeriesValue)result.Value!).Series;
        Assert.Equal([0.45, -0.012], series.Points.Select(p => p.Value));
        Assert.Equal(SeriesValueKind.PricePerKwh, series.Kind);
    }

    [Theory]
    [InlineData("EUR/MWh", "PLN")]
    [InlineData("PLN/MWh", null)]
    public void A_price_curve_in_another_currency_than_the_configured_one_is_refused(string unit, string? currency)
    {
        // Why: a curve in a currency the user did not declare, compared with their own prices, would be a silent error.
        var curve = new ParsedSeries([new SeriesPoint(Noon, null, 450)], SeriesProblem.None);

        var result = SignalNormalizer.Normalize(Definition(SignalKind.Series, unit, SeriesValueKind.PricePerKwh, currency), "x", null, curve);

        Assert.Equal((NormalizationOutcome.Rejected, SignalDetail.UnitMismatch), (result.Outcome, result.Detail));
    }

    [Theory]
    [InlineData("Wh", "4200", 4.2)]
    [InlineData("kWh", "4.2", 4.2)]
    [InlineData("MWh", "0.0042", 4.2)]
    public void A_day_total_is_kwh_after_the_signal_layer(string unit, string state, double expected)
    {
        var result = SignalNormalizer.Normalize(Definition(SignalKind.EnergyForecast, unit), state, unit);

        Assert.Equal(NormalizationOutcome.Accepted, result.Outcome);
        Assert.Equal(expected, ((EnergyForecastValue)result.Value!).Energy.Value, 9);
    }

    [Theory]
    [InlineData("4200", "kWh", SignalDetail.UnitMismatch)]
    [InlineData("4200", null, SignalDetail.UnitMismatch)]
    [InlineData("-3", "Wh", SignalDetail.SignViolation)]
    [InlineData("sunny", "Wh", SignalDetail.NotNumeric)]
    public void A_day_total_in_the_wrong_unit_or_not_a_number_is_refused_never_converted_or_read_as_zero(string state, string? reported, SignalDetail detail)
    {
        // Why: Wh read as kWh is a 1000x error on the energy the planner plans a car's charge on.
        var result = SignalNormalizer.Normalize(Definition(SignalKind.EnergyForecast, "Wh"), state, reported);

        Assert.Equal((NormalizationOutcome.Rejected, detail), (result.Outcome, result.Detail));
    }

    [Theory]
    [InlineData("2026-10-23T15:41:10+00:00")]
    [InlineData("2026-10-23T17:41:10+02:00")]
    [InlineData("2026-10-23T15:41:10Z")]
    public void A_timestamp_is_an_instant_with_its_offset(string state)
    {
        var result = SignalNormalizer.Normalize(Definition(SignalKind.Timestamp, null), state, null);

        Assert.Equal(new TimestampValue(new DateTimeOffset(2026, 10, 23, 15, 41, 10, TimeSpan.Zero)), result.Value);
    }

    [Theory]
    [InlineData("2026-10-23T15:41:10")]
    [InlineData("2026-10-23")]
    [InlineData("tomorrow")]
    public void A_time_without_a_utc_offset_is_not_read_as_local_time(string state)
    {
        // Why: the add-on's host zone is not the installation's; guessing an offset would move sunset by hours.
        var result = SignalNormalizer.Normalize(Definition(SignalKind.Timestamp, null), state, null);

        Assert.Equal((NormalizationOutcome.Rejected, SignalDetail.NotTimestamp), (result.Outcome, result.Detail));
    }

    [Fact]
    public void A_fresh_forecast_reaches_the_planner_inputs_with_its_content_coverage_and_report_time()
    {
        var store = Store();
        store.PushSeries(PvEntity, "3412", Hourly(store.Now, 30));
        store.Push(TotalEntity, "4200");
        store.Push(SunEntity, "2026-10-23T15:41:10+00:00");

        var forecast = store.Snapshot().Forecast!;

        var pv = forecast.Sample(Pv)!;
        Assert.True(pv.Usable);
        Assert.Equal((SignalRole.PvForecast, 30, store.Now.AddHours(29), store.Now), (pv.Role, pv.Series!.Points.Length, pv.CoversUntil, pv.ReportedAt));
        Assert.Equal(4.2, forecast.Usable(SignalRole.PvForecastToday)!.EnergyKwh!.Value, 9);
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 15, 41, 10, TimeSpan.Zero), forecast.Usable(SignalRole.SunSetting)!.At);
    }

    [Fact]
    public void A_series_that_ends_in_six_hours_is_a_fresh_partial_forecast_not_a_stale_one()
    {
        // Why: 15-PLANNER, "Inputs": freshness is the entity's last_reported, coverage is the last point. Treating a short series as stale would
        // throw away a good morning forecast; treating a stale one as fresh would plan on yesterday's weather.
        var store = Store();
        store.PushSeries(PvEntity, "3412", Hourly(store.Now, 6));
        store.Advance(TimeSpan.FromHours(1));

        var pv = store.Snapshot().Forecast!.Sample(Pv)!;

        Assert.Equal((SignalState.Fresh, true), (pv.State, pv.Usable));
        Assert.True(pv.CoversUntil < store.Now.AddHours(5));
    }

    [Fact]
    public void A_forecast_goes_stale_at_max_age_and_then_carries_no_content_at_all()
    {
        // Why: SIG-05 and the planner's rule "a forecast older than its max_age is not used at all, never the last known curve". At exactly
        // max_age the signal is stale, and a stale one has no series left to plan on.
        var store = Store();
        store.PushSeries(PvEntity, "3412", Hourly(store.Now, 30));
        store.Advance(TimeSpan.FromHours(6) - TimeSpan.FromSeconds(1));
        Assert.True(store.Snapshot().Forecast!.Sample(Pv)!.Usable);

        store.Advance(TimeSpan.FromSeconds(1));

        var pv = store.Snapshot().Forecast!.Sample(Pv)!;
        Assert.Equal((SignalState.Stale, SignalDetail.MaxAgeExceeded, false), (pv.State, pv.Detail, pv.Series is not null));
        Assert.Null(store.Snapshot().Forecast!.Usable(SignalRole.PvForecast));
    }

    [Fact]
    public void A_repeated_state_with_a_newer_last_reported_keeps_the_series_fresh_and_a_probe_never_replaces_it()
    {
        // Why: SIG-06. A forecast that does not change for hours is reported again only as a newer last_reported. A probe answer carries no
        // attributes, so it may confirm the series in use, but a probe answer for a different state must not blank or replace it.
        var store = Store();
        store.PushSeries(PvEntity, "3412", Hourly(store.Now, 30));
        var first = store.Now;
        store.Advance(TimeSpan.FromHours(5));

        store.ProbeAnswer(PvEntity, "3412", first, store.Now, unit: null);
        store.Advance(TimeSpan.FromHours(5));
        Assert.True(store.Snapshot().Forecast!.Sample(Pv)!.Usable);

        store.ProbeAnswer(PvEntity, "9999", store.Now, store.Now, unit: null);

        var pv = store.Snapshot().Forecast!.Sample(Pv)!;
        Assert.Equal(30, pv.Series!.Points.Length);
        Assert.Equal("3412", store.Reading("pv_forecast_series").RawState);
    }

    [Theory]
    [InlineData(SeriesProblem.Malformed, SignalDetail.SeriesMalformed)]
    [InlineData(SeriesProblem.TooLarge, SignalDetail.SeriesTooLarge)]
    [InlineData(SeriesProblem.Missing, SignalDetail.SeriesMissing)]
    public void A_refused_series_after_a_good_one_leaves_no_usable_forecast_and_says_why_in_the_diagnostics(SeriesProblem problem, SignalDetail detail)
    {
        // Why: the old curve must not stand in for a refused frame.
        var store = Store();
        store.PushSeries(PvEntity, "3412", Hourly(store.Now, 30));
        store.Advance(TimeSpan.FromMinutes(10));

        store.PushSeries(PvEntity, "3413", ParsedSeries.Of(problem));

        var reading = store.Reading("pv_forecast_series");
        Assert.Equal((SignalState.Rejected, detail), (reading.State, reading.Detail));
        Assert.Null(store.Snapshot().Forecast!.Usable(SignalRole.PvForecast));
    }

    [Fact]
    public void An_unavailable_entity_carries_no_forecast()
    {
        var store = Store();
        store.PushSeries(PvEntity, "3412", Hourly(store.Now, 30));
        store.Advance(TimeSpan.FromMinutes(1));

        store.PushSeries(PvEntity, "unavailable", ParsedSeries.Of(SeriesProblem.Missing));

        var pv = store.Snapshot().Forecast!.Sample(Pv)!;
        Assert.Equal((SignalState.Unavailable, false), (pv.State, pv.Usable));
    }

    [Fact]
    public void The_diagnostics_show_a_series_with_its_points_and_coverage_and_a_timestamp_as_text()
    {
        var store = Store();
        store.PushSeries(PvEntity, "3412", Hourly(store.Now, 30));
        store.Push(SunEntity, "2026-10-23T15:41:10+00:00");

        var series = store.Reading("pv_forecast_series");
        var sun = store.Reading("sun_setting");

        Assert.Equal((SignalKind.Series, SignalRole.PvForecast), (series.Kind, series.Role));
        Assert.Equal(30, ((SeriesValue)series.Value!).Series.Points.Length);
        Assert.Equal(new TimestampValue(new DateTimeOffset(2026, 10, 23, 15, 41, 10, TimeSpan.Zero)), sun.Value);
    }

    [Fact]
    public void The_curve_is_kept_apart_from_the_live_price_and_never_changes_what_dispatch_reads()
    {
        // Why: slice 2 only carries the forward curve; dispatch's price is the live signal or the fallback exactly as before. The same price spec
        // with and without a curve gives the same price at every moment, whatever the curve says.
        var store = Store();
        store.Push("sensor.market_price", "450");
        store.PushSeries(CurveEntity, "0.45", new ParsedSeries([new SeriesPoint(store.Now, store.Now.AddHours(1), 9999)], SeriesProblem.None), unit: "PLN/MWh");
        var market = SignalName.Parse("market_price");
        var curve = SignalName.Parse("price_curve");
        var spec = new DynamicPrice(market, new FlatPrice(0.9), null);
        var withCurve = spec with { Curve = curve };
        var snapshot = store.Snapshot();

        Assert.Equal(
            new Tariff(spec, HolidayCalendar.Empty, TimeZoneInfo.Utc).PriceAt(store.Now, snapshot.Prices),
            new Tariff(withCurve, HolidayCalendar.Empty, TimeZoneInfo.Utc).PriceAt(store.Now, snapshot.Prices));
        Assert.Equal(0.45, snapshot.Prices!.Usable(market));
        Assert.DoesNotContain(curve, snapshot.Prices.Samples.Keys);
        Assert.Equal(SignalState.Fresh, snapshot.Forecast!.Sample(curve)!.State);
    }

    [Fact]
    public void A_series_signal_asks_the_transport_for_its_one_attribute_and_nothing_else()
    {
        // Why: the whitelist is the bound. Only the entities with a series signal carry an attribute spec; every other tracked entity carries none.
        var store = Store();

        Assert.True(store.Store.TryTakeTrackedChange(out var tracked));

        var pv = tracked.Single(t => t.Entity == EntityId.Parse(PvEntity));
        Assert.Equal(new SeriesSpec("watts", SeriesShape.Dict, "start", "end", "value"), pv.Series);
        Assert.Equal(new SeriesSpec("raw_today", SeriesShape.Auto, "start", "end", "value"), tracked.Single(t => t.Entity == EntityId.Parse(CurveEntity)).Series);
        Assert.Equal(2, tracked.Count(t => t.Series is not null));
    }

    [Fact]
    public void A_reload_with_the_same_forecast_declaration_keeps_the_series_and_a_changed_one_drops_it()
    {
        // Why: editing a comment must not blank a forecast that HA will not resend for hours; a series parsed under another attribute says nothing under a new one.
        var store = Store();
        store.PushSeries(PvEntity, "3412", Hourly(store.Now, 30));

        store.Store.ApplyConfiguration(TestConfigurations.Parse(TestConfigurations.ThreePhase(PvSeries() + Others() + MarketPrice) + Economics), store.Now);
        Assert.True(store.Snapshot().Forecast!.Sample(Pv)!.Usable);

        store.Store.ApplyConfiguration(TestConfigurations.Parse(TestConfigurations.ThreePhase(PvSeries().Replace("watts", "wh_period", StringComparison.Ordinal) + Others() + MarketPrice) + Economics), store.Now);
        Assert.False(store.Snapshot().Forecast!.Sample(Pv)!.Usable);
    }

    [Fact]
    public void There_are_no_forecast_inputs_when_no_forecast_signal_is_mapped()
    {
        Assert.Null(new StoreHarness(TestConfigurations.ThreePhase()).Snapshot().Forecast);
    }

    [Fact]
    public void Forecasts_travel_beside_the_other_inputs_and_never_inside_protection_dispatch_or_the_control_inputs()
    {
        // Why: SIG-04 by type. Neither the protection inputs, the dispatch inputs nor the control inputs the guard runner reads may reach a
        // forecast type: the planner reads the snapshot of the store cycle, which is a field of its own.
        bool IsForecast(Type t) => t == typeof(ForecastInputs) || t == typeof(ForecastSample) || t == typeof(TimeSeries) || t == typeof(SeriesValue);

        Assert.DoesNotContain(SignalArchitectureTests.TypeGraph(typeof(ProtectionInputs)), IsForecast);
        Assert.DoesNotContain(SignalArchitectureTests.TypeGraph(typeof(DispatchInputs)), IsForecast);
        Assert.DoesNotContain(SignalArchitectureTests.TypeGraph(typeof(ControlInputs)), IsForecast);
        Assert.Contains(typeof(SignalSnapshot).GetProperties(), p => p.PropertyType == typeof(ForecastInputs));
    }

    [Fact]
    public void A_forecast_signal_is_never_a_protection_or_dispatch_measurement()
    {
        var store = Store();
        store.PushSeries(PvEntity, "3412", Hourly(store.Now, 30));
        store.Push(TotalEntity, "4200");

        var snapshot = store.Snapshot();

        Assert.All(snapshot.Protection.Phases, p => Assert.NotEqual(4.2, p.Current?.Signed.Value));
        Assert.Equal(SignalKind.EnergyForecast, store.Reading("pv_today").Kind);
    }
}
