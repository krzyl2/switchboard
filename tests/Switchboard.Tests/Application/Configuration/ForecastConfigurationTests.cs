using Switchboard.Application.Configuration;
using Switchboard.Application.Signals;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Configuration;

/// <summary>
/// PLAN-06 and PLAN-07 in the configuration (15-PLANNER, "Configuration" checks): the new kinds, roles and the <c>attribute:</c> and <c>series:</c>
/// keys are validated like every other signal, and a file that has none of them loads exactly as before.
/// </summary>
public sealed class ForecastConfigurationTests
{
    private static string Signal(string body) => "  forecast:\n" + string.Join('\n', body.Split('\n').Select(l => "    " + l.TrimEnd())) + "\n";

    private static ConfigurationResult Parse(string body, string extra = "") => TestConfigurations.Parse(TestConfigurations.ThreePhase(Signal(body)) + extra);

    private const string Pv = "entity: sensor.pv_forecast\nkind: series\nattribute: watts\nseries:\n  shape: dict\n  value: power_w\nmax_age: 6h\nrole: pv_forecast";

    private static void AssertError(ConfigurationResult result, string code, string path) =>
        Assert.Contains(result.Errors, e => e.Code == code && e.Path == path);

    [Fact]
    public void A_pv_forecast_series_loads_with_its_whitelist_and_meaning()
    {
        var result = Parse(Pv);

        Assert.Equal(ConfigurationStatus.Valid, result.Status);
        var signal = result.Configuration!.Signals.Single(s => s.Name == SignalName.Parse("forecast"));
        Assert.Equal((SignalKind.Series, SignalRole.PvForecast, SeriesValueKind.PowerW), (signal.Kind, signal.Role, signal.SeriesMeaning));
        Assert.Equal(new SeriesSpec("watts", SeriesShape.Dict, "start", "end", "value"), signal.Series);
        Assert.Null(signal.Unit);
        Assert.Null(signal.Sigma);
    }

    [Fact]
    public void The_item_keys_and_the_end_key_can_be_named_and_the_end_key_can_be_switched_off()
    {
        var result = Parse(Pv.Replace("shape: dict\n  value: power_w", "shape: list\n  value: energy_wh_period\n  start_key: period_start\n  value_key: pv_estimate\n  end_key: period_end", StringComparison.Ordinal));

        Assert.Equal(new SeriesSpec("watts", SeriesShape.List, "period_start", "period_end", "pv_estimate"), result.Configuration!.Signals.Single(s => s.Name == SignalName.Parse("forecast")).Series);
    }

    [Fact]
    public void A_series_needs_an_attribute()
    {
        var result = Parse(Pv.Replace("attribute: watts\n", string.Empty, StringComparison.Ordinal));

        Assert.Contains(result.Errors, e => e.Path == "signals.forecast" || e.Path == "signals.forecast.attribute");
        Assert.NotEqual(ConfigurationStatus.Valid, result.Status);
    }

    [Fact]
    public void A_series_needs_its_meaning_from_the_declaration_never_from_the_data()
    {
        var result = Parse(Pv.Replace("series:\n  shape: dict\n  value: power_w\n", string.Empty, StringComparison.Ordinal));

        AssertError(result, ConfigurationIssueCodes.SeriesValueRequired, "signals.forecast.series.value");
    }

    [Theory]
    [InlineData("attribute: watts", "attribute")]
    [InlineData("series:\n  value: power_w", "series")]
    public void The_attribute_whitelist_belongs_to_series_signals_only(string extra, string key)
    {
        var result = Parse("entity: sensor.x\nkind: power\nunit: W\nsign: positive_import\nmax_age: 60s\n" + extra);

        AssertError(result, ConfigurationIssueCodes.KeyNotAllowed, $"signals.forecast.{key}");
    }

    private const string Sun = "entity: sun.sun\nkind: timestamp\nattribute: next_rising\nmax_age: 1h\nrole: sun_rising";

    [Fact]
    public void A_sun_time_names_the_attribute_that_holds_its_instant()
    {
        var result = Parse(Sun);

        Assert.Equal(ConfigurationStatus.Valid, result.Status);
        var signal = result.Configuration!.Signals.Single(s => s.Name == SignalName.Parse("forecast"));
        Assert.Equal((SignalKind.Timestamp, SignalRole.SunRising, "next_rising"), (signal.Kind, signal.Role, signal.Attribute));
        Assert.Null(signal.Series);
    }

    [Fact]
    public void A_timestamp_without_an_attribute_reads_the_state_as_before()
    {
        var result = Parse(Sun.Replace("attribute: next_rising\n", string.Empty, StringComparison.Ordinal));

        Assert.Equal(ConfigurationStatus.Valid, result.Status);
        Assert.Null(result.Configuration!.Signals.Single(s => s.Name == SignalName.Parse("forecast")).Attribute);
    }

    private static string SunSetting(string attributeLine) =>
        Signal(Sun).Replace("forecast:", "sunset:", StringComparison.Ordinal).Replace("sun_rising", "sun_setting", StringComparison.Ordinal).Replace("    attribute: next_rising\n", attributeLine, StringComparison.Ordinal);

    [Fact]
    public void The_sun_entity_may_feed_both_sun_times_through_two_different_attributes()
    {
        // Why: the sun entity is one entity with next_rising and next_setting; "one entity feeds one signal" would make it unusable.
        var result = TestConfigurations.Parse(TestConfigurations.ThreePhase(Signal(Sun) + SunSetting("    attribute: next_setting\n")));

        Assert.Equal(ConfigurationStatus.Valid, result.Status);
        Assert.Equal(["next_rising", "next_setting"], result.Configuration!.Signals.Where(s => s.Kind == SignalKind.Timestamp).Select(s => s.Attribute).Order());
    }

    [Theory]
    [InlineData("    attribute: next_rising\n")]
    [InlineData("")]
    public void Two_sun_signals_that_do_not_read_different_attributes_are_still_a_duplicate_entity(string secondAttribute)
    {
        var result = TestConfigurations.Parse(TestConfigurations.ThreePhase(Signal(Sun) + SunSetting(secondAttribute)));

        Assert.Contains(result.Errors, e => e.Code == ConfigurationIssueCodes.DuplicateEntity);
    }

    [Theory]
    [InlineData("kind: energy_forecast\nunit: kWh")]
    [InlineData("kind: state")]
    [InlineData("kind: price\nunit: PLN/kWh")]
    [InlineData("kind: voltage\nunit: V")]
    public void No_kind_but_series_and_timestamp_has_an_attribute(string kind)
    {
        // Why: the attribute whitelist is granted per kind; an ordinary measurement reads the state and nothing else of the entity.
        var result = Parse($"entity: sensor.x\n{kind}\nattribute: next_rising\nmax_age: 60s");

        AssertError(result, ConfigurationIssueCodes.KeyNotAllowed, "signals.forecast.attribute");
    }

    [Fact]
    public void A_timestamp_has_no_series_block()
    {
        var result = Parse(Sun + "\nseries:\n  value: power_w");

        AssertError(result, ConfigurationIssueCodes.KeyNotAllowed, "signals.forecast.series");
    }

    [Theory]
    [InlineData("a.b")]
    [InlineData("next rising")]
    [InlineData("1st")]
    [InlineData("a[0]")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void A_timestamp_attribute_is_a_plain_identifier_never_a_path_or_a_pattern(string name)
    {
        var result = Parse(Sun.Replace("next_rising", $"\"{name}\"", StringComparison.Ordinal));

        Assert.NotEqual(ConfigurationStatus.Valid, result.Status);
        Assert.Contains(result.Errors, e => e.Path == "signals.forecast.attribute");
    }

    [Theory]
    [InlineData("shape: tree")]
    [InlineData("value: kw")]
    [InlineData("colour: red")]
    [InlineData("start_key: ''")]
    public void An_unknown_or_invalid_series_key_is_an_error(string line)
    {
        var result = Parse(Pv.Replace("shape: dict", line, StringComparison.Ordinal).Replace("value: power_w", "value: power_w", StringComparison.Ordinal));

        Assert.NotEqual(ConfigurationStatus.Valid, result.Status);
    }

    [Theory]
    [InlineData("kind: power\nunit: W\nsign: positive_import", "role: pv_forecast")]
    [InlineData("kind: series\nattribute: a\nseries:\n  value: power_w", "role: sun_rising")]
    [InlineData("kind: series\nattribute: a\nseries:\n  value: power_w", "role: pv_forecast_today")]
    [InlineData("kind: energy_forecast\nunit: kWh", "role: price_curve_import")]
    [InlineData("kind: timestamp", "role: pv_forecast")]
    [InlineData("kind: series\nattribute: a\nunit: PLN/kWh", "role: pv_forecast")]
    [InlineData("kind: series\nattribute: a\nseries:\n  value: power_w", "role: price_curve_export")]
    public void A_forecast_or_curve_role_needs_its_kind(string kind, string role)
    {
        var result = Parse($"entity: sensor.x\n{kind}\nmax_age: 6h\n{role}");

        Assert.Contains(result.Errors, e => e.Code == ConfigurationIssueCodes.RoleKindMismatch);
    }

    [Theory]
    [InlineData("Wh")]
    [InlineData("kWh")]
    [InlineData("MWh")]
    public void A_day_total_takes_energy_units(string unit)
    {
        var result = Parse($"entity: sensor.x\nkind: energy_forecast\nunit: {unit}\nmax_age: 6h\nrole: pv_forecast_today");

        Assert.Equal(ConfigurationStatus.Valid, result.Status);
    }

    [Theory]
    [InlineData("kind: energy_forecast\nunit: W")]
    [InlineData("kind: energy_forecast")]
    [InlineData("kind: timestamp\nunit: s")]
    [InlineData("kind: timestamp\nsigma: 1")]
    [InlineData("kind: energy_forecast\nunit: kWh\nsigma: 1")]
    public void A_total_or_a_time_with_a_wrong_unit_or_a_noise_value_is_an_error(string body)
    {
        var result = Parse("entity: sensor.x\nmax_age: 6h\n" + body);

        Assert.NotEqual(ConfigurationStatus.Valid, result.Status);
    }

    [Fact]
    public void A_forecast_may_wait_hours_but_never_longer_than_the_signal_limit()
    {
        var result = Parse(Pv.Replace("max_age: 6h", "max_age: 48h", StringComparison.Ordinal));

        Assert.Contains(result.Errors, e => e.Code == ConfigurationIssueCodes.OutOfRange && e.Path == "signals.forecast.max_age");
    }

    private const string Curve = "entity: sensor.curve\nkind: series\nattribute: raw_today\nunit: PLN/MWh\nmax_age: 6h\nrole: price_curve_import";
    private const string Market = "  market:\n    entity: sensor.market\n    kind: price\n    unit: PLN/MWh\n    max_age: 5m\n    role: price_import\n";

    private static ConfigurationResult WithTariff(string tariff, string curve = Curve) =>
        TestConfigurations.Parse(TestConfigurations.ThreePhase(Signal(curve) + Market) + $"\neconomics:\n  currency: PLN\n  tariff:\n{tariff}");

    [Fact]
    public void A_price_curve_is_a_series_with_a_price_unit_and_is_named_by_the_dynamic_price()
    {
        var result = WithTariff("    signal: market\n    curve: forecast\n    fallback: 0.9\n");

        Assert.Equal(ConfigurationStatus.Valid, result.Status);
        var spec = (DynamicPrice)result.Configuration!.Economics!.Tariff!;
        Assert.Equal((SignalName.Parse("market"), SignalName.Parse("forecast")), (spec.Signal, spec.Curve!.Value));
        Assert.DoesNotContain(result.Warnings, w => w.Code == ConfigurationIssueCodes.ForecastSignalUnused);
        var signal = result.Configuration.Signals.Single(s => s.Name == SignalName.Parse("forecast"));
        Assert.Equal(("PLN", SeriesValueKind.PricePerKwh), (signal.PriceCurrency, signal.SeriesMeaning!.Value));
    }

    [Theory]
    [InlineData("market")]
    [InlineData("nowhere")]
    public void A_curve_must_be_a_price_curve_signal(string name)
    {
        var result = WithTariff($"    signal: market\n    curve: {name}\n    fallback: 0.9\n");

        AssertError(result, ConfigurationIssueCodes.InvalidValue, "economics.tariff.curve");
    }

    [Fact]
    public void A_curve_without_a_signal_is_a_warning_and_replaces_nothing()
    {
        // Why: 15-PLANNER, configuration checks. The curve extends a live price; with no live price there is nothing to extend, and the price is
        // the fallback, exactly what the file would give without the curve line.
        var result = WithTariff("    curve: forecast\n    fallback: 0.9\n");

        Assert.Equal(ConfigurationStatus.Valid, result.Status);
        Assert.Contains(result.Warnings, w => w.Code == ConfigurationIssueCodes.CurveWithoutSignal && w.Path == "economics.tariff.curve");
        Assert.Equal(new FlatPrice(0.9), result.Configuration!.Economics!.Tariff);
        Assert.DoesNotContain(result.Warnings, w => w.Code == ConfigurationIssueCodes.ForecastSignalUnused);
    }

    [Fact]
    public void A_dynamic_price_still_needs_a_signal_or_a_curve_to_be_dynamic()
    {
        var result = WithTariff("    fallback: 0.9\n    add: 0.1\n");

        Assert.NotEqual(ConfigurationStatus.Valid, result.Status);
    }

    [Fact]
    public void A_curve_in_a_foreign_currency_warns_and_is_not_usable_like_a_price_signal()
    {
        var result = WithTariff("    signal: market\n    curve: forecast\n    fallback: 0.9\n", Curve.Replace("PLN/MWh", "EUR/MWh", StringComparison.Ordinal));

        Assert.Contains(result.Warnings, w => w.Code == ConfigurationIssueCodes.UnitMismatch && w.Path == "signals.forecast.unit");
    }

    [Fact]
    public void A_curve_nothing_names_and_a_forecast_nothing_reads_are_warnings_never_errors()
    {
        // Why: 15-PLANNER, configuration checks. An unused role is probably a forgotten line, not a broken file; nothing is dropped.
        var unusedCurve = TestConfigurations.Parse(TestConfigurations.ThreePhase(Signal(Curve)) + "\neconomics:\n  currency: PLN\n  tariff: 0.9\n");
        var unusedForecast = Parse(Pv);

        Assert.Equal((ConfigurationStatus.Valid, ConfigurationStatus.Valid), (unusedCurve.Status, unusedForecast.Status));
        Assert.Contains(unusedCurve.Warnings, w => w.Code == ConfigurationIssueCodes.ForecastSignalUnused && w.Path == "signals.forecast.role");
        Assert.Contains(unusedForecast.Warnings, w => w.Code == ConfigurationIssueCodes.ForecastSignalUnused && w.Path == "signals.forecast.role");
    }

    [Fact]
    public void A_series_signal_with_no_role_is_shown_in_the_diagnostics_and_not_warned()
    {
        var result = Parse(Pv.Replace("\nrole: pv_forecast", string.Empty, StringComparison.Ordinal));

        Assert.Equal(ConfigurationStatus.Valid, result.Status);
        Assert.DoesNotContain(result.Warnings, w => w.Code == ConfigurationIssueCodes.ForecastSignalUnused);
    }

    [Fact]
    public void A_file_without_forecast_signals_loads_with_no_series_no_new_warning_and_no_forecast_inputs()
    {
        // Why: PLAN-14 in miniature. The shipped example and the plain three-phase document must load exactly as before this slice.
        foreach (var yaml in new[] { TestConfigurations.Example, TestConfigurations.ThreePhase(), TestConfigurations.ExampleWithCharger })
        {
            var result = TestConfigurations.Parse(yaml);

            Assert.Equal(ConfigurationStatus.Valid, result.Status);
            Assert.All(result.Configuration!.Signals, s => Assert.True(s.Series is null && s.SeriesMeaning is null));
            Assert.DoesNotContain(result.Warnings, w => w.Code is ConfigurationIssueCodes.ForecastSignalUnused or ConfigurationIssueCodes.CurveWithoutSignal);
            Assert.Null(new StoreHarness(yaml).Snapshot().Forecast);
        }
    }

    [Fact]
    public void The_new_roles_never_count_as_a_phase_or_total_signal()
    {
        var result = Parse(Pv + "\nphase: L1");

        Assert.Contains(result.Errors, e => e.Code == ConfigurationIssueCodes.PhaseNotAllowed);
    }
}
