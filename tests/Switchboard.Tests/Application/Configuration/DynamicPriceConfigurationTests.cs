using Switchboard.Application.Configuration;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Configuration;

/// <summary>
/// ECO-10 in the configuration: a price spec with <c>signal:</c> and a required static <c>fallback</c> (and an optional static <c>add</c>)
/// is valid; every malformed one is a named issue and a missing fallback makes the document invalid (safe state, like any invalid file),
/// because a price with nothing to fall back to would have to be guessed. The price signal's unit label is checked against the currency.
/// </summary>
public sealed class DynamicPriceConfigurationTests
{
    private static string Signal(string unit = "PLN/MWh", string role = "price_import", string kind = "price", string extra = "") =>
        $"  market_price:\n    entity: sensor.rce_price\n    kind: {kind}\n    unit: {unit}\n    max_age: 5m\n{(role.Length == 0 ? string.Empty : $"    role: {role}\n")}{extra}";

    private static ConfigurationResult Parse(string economics, string? signal = null) =>
        TestConfigurations.Parse(TestConfigurations.ThreePhase(signal ?? Signal()) + "\n" + economics);

    private static ConfigurationIssue Single(IEnumerable<ConfigurationIssue> issues, string code) => Assert.Single(issues, i => i.Code == code);

    private const string Holidays = "  holidays: [2026-11-01, 2026-12-25]\n";

    private const string Zones = """
              default_zone: offpeak
              zones:
                - id: peak
                  price: 0.40
                  when:
                    - { days: [workday], hours: ["07:00-13:00"] }
                - id: offpeak
                  price: 0.10
                  when: []
        """;

    [Fact]
    public void A_dynamic_tariff_with_zones_to_add_and_a_flat_fallback_parses_to_a_dynamic_spec()
    {
        var parsed = TestConfigurations.Valid(TestConfigurations.ThreePhase(Signal()) + "\neconomics:\n  currency: PLN\n" + Holidays + "  tariff:\n    signal: market_price\n    fallback: 0.9\n    add:\n" + Zones);

        var dynamic = Assert.IsType<DynamicPrice>(parsed.Configuration!.Economics!.Tariff);
        Assert.Equal(("market_price", new FlatPrice(0.9)), (dynamic.Signal.Value, dynamic.Fallback));
        Assert.Equal(["peak", "offpeak"], Assert.IsType<ZonedPrice>(dynamic.Add).Zones.Select(z => z.Id));
        Assert.Empty(parsed.Warnings);
    }

    [Fact]
    public void A_dynamic_export_price_parses_and_a_fallback_may_itself_be_zones()
    {
        var parsed = TestConfigurations.Valid(
            TestConfigurations.ThreePhase(Signal(role: "price_export"))
            + "\neconomics:\n  currency: PLN\n" + Holidays + "  settlement:\n    model: hourly_netting\n    export_price:\n      signal: market_price\n      fallback:\n" + Zones.Replace("\n", "\n  ", StringComparison.Ordinal).Insert(0, "  ") + "\n");

        var export = Assert.IsType<DynamicPrice>(parsed.Configuration!.Economics!.Settlement!.ExportPrice);
        Assert.IsType<ZonedPrice>(export.Fallback);
        Assert.Null(export.Add);
    }

    [Fact]
    public void A_dynamic_price_without_a_fallback_is_an_invalid_document()
    {
        // Why: the fallback is what keeps the system off a guess when the entity is gone. Without it the file is refused (safe state), not run
        // with a price nobody chose.
        var parsed = Parse("economics:\n  currency: PLN\n  tariff:\n    signal: market_price\n    add: 0.4\n");

        Assert.Equal(ConfigurationStatus.Invalid, parsed.Status);
        Assert.Null(parsed.Configuration);
        Assert.Equal("economics.tariff.fallback", Single(parsed.Errors, ConfigurationIssueCodes.PriceFallbackMissing).Path);
    }

    [Fact]
    public void A_dynamic_export_price_without_a_fallback_is_invalid_too()
    {
        var parsed = Parse("economics:\n  currency: PLN\n  settlement:\n    model: net_billing\n    export_price:\n      signal: market_price\n", Signal(role: "price_export"));

        Assert.Equal("economics.settlement.export_price.fallback", Single(parsed.Errors, ConfigurationIssueCodes.PriceFallbackMissing).Path);
    }

    [Theory]
    [InlineData("tariff:\n    signal: market_price\n    fallback:\n      signal: market_price\n      fallback: 1\n", "economics.tariff.fallback.signal")]
    [InlineData("tariff:\n    signal: market_price\n    fallback: 1\n    add:\n      signal: market_price\n      fallback: 1\n", "economics.tariff.add.signal")]
    public void A_dynamic_price_inside_a_fallback_or_an_add_is_refused(string tariff, string path)
    {
        // Why: the fallback and the add are static by definition; a fallback that depends on another entity is not a fallback.
        var parsed = Parse("economics:\n  currency: PLN\n  " + tariff);

        Assert.Equal(path, Single(parsed.Errors, ConfigurationIssueCodes.DynamicPriceNotSupported).Path);
    }

    [Theory]
    [InlineData("tariff:\n    signal: nothing_by_that_name\n    fallback: 1\n", "economics.tariff.signal")]
    [InlineData("tariff:\n    fallback: 1\n", "economics.tariff")]
    public void A_dynamic_price_must_name_a_signal_that_exists_under_signals(string tariff, string path)
    {
        var parsed = Parse("economics:\n  currency: PLN\n  " + tariff);

        Assert.Equal(ConfigurationStatus.Invalid, parsed.Status);
        Assert.StartsWith(path, parsed.Errors.First(e => e.Code is ConfigurationIssueCodes.InvalidValue or ConfigurationIssueCodes.MissingKey).Path, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dynamic_price_must_name_a_signal_of_kind_price()
    {
        // Why: a power signal named as a price would read watts as currency.
        var parsed = Parse("economics:\n  currency: PLN\n  tariff:\n    signal: grid_power_l1\n    fallback: 1\n");

        Assert.Equal("economics.tariff.signal", Single(parsed.Errors, ConfigurationIssueCodes.InvalidValue).Path);
    }

    [Theory]
    [InlineData("tariff:\n    signal: market_price\n    fallback: -1\n")]
    [InlineData("tariff:\n    signal: market_price\n    fallback: 1\n    add: -0.2\n")]
    [InlineData("tariff:\n    signal: market_price\n    fallback: cheap\n")]
    [InlineData("tariff:\n    signal: market_price\n    fallback: 1\n    bogus: 1\n")]
    public void A_malformed_dynamic_spec_is_an_error_not_a_silent_default(string tariff)
    {
        var parsed = Parse("economics:\n  currency: PLN\n  " + tariff);

        Assert.Equal(ConfigurationStatus.Invalid, parsed.Status);
        Assert.NotEmpty(parsed.Errors);
    }

    [Fact]
    public void A_price_signal_unit_must_be_a_label_per_kwh_or_per_mwh()
    {
        var parsed = Parse("economics:\n  currency: PLN\n", Signal("kWh"));

        Assert.Equal("signals.market_price.unit", Single(parsed.Errors, ConfigurationIssueCodes.UnitNotAllowedForKind).Path);
    }

    [Theory]
    [InlineData("PLN/Wh")]
    [InlineData("/kWh")]
    [InlineData("PLN / kWh")]
    [InlineData("PLN")]
    public void Other_price_units_are_refused(string unit)
    {
        Assert.Contains(Parse("economics:\n  currency: PLN\n", Signal(unit)).Errors, e => e.Code == ConfigurationIssueCodes.UnitNotAllowedForKind);
    }

    [Fact]
    public void A_price_unit_with_another_label_than_the_currency_is_a_warning_and_the_signal_is_marked_unusable()
    {
        // Why: the document is valid (the fallback keeps the price defined) but the user must be told the entity will not be used.
        var parsed = Parse("economics:\n  currency: PLN\n  tariff:\n    signal: market_price\n    fallback: 1\n", Signal("EUR/MWh"));

        Assert.Equal(ConfigurationStatus.Valid, parsed.Status);
        Assert.Equal("signals.market_price.unit", Single(parsed.Warnings, ConfigurationIssueCodes.UnitMismatch).Path);
        Assert.Equal("PLN", parsed.Configuration!.Signals.Single(s => s.Name.Value == "market_price").PriceCurrency);
    }

    [Fact]
    public void A_price_role_that_no_price_names_is_a_warning()
    {
        // Why: ECO-10, "a signal with a role that no spec names is a warning": a mapped price that feeds nothing is a misconfiguration.
        var parsed = Parse("economics:\n  currency: PLN\n  tariff: 0.9\n");

        Assert.Equal(ConfigurationStatus.Valid, parsed.Status);
        Assert.Equal("signals.market_price.role", Single(parsed.Warnings, ConfigurationIssueCodes.PriceSignalUnused).Path);
    }

    [Fact]
    public void A_price_role_without_any_economics_section_is_a_warning_and_the_signal_is_unusable()
    {
        var parsed = TestConfigurations.Valid(TestConfigurations.ThreePhase(Signal()));

        Assert.Single(parsed.Warnings, w => w.Code == ConfigurationIssueCodes.PriceSignalUnused);
        Assert.Null(parsed.Configuration!.Signals.Single(s => s.Name.Value == "market_price").PriceCurrency);
    }

    [Fact]
    public void A_named_price_role_signal_gives_no_unused_warning()
    {
        var parsed = TestConfigurations.Valid(TestConfigurations.ThreePhase(Signal()) + "\neconomics:\n  currency: PLN\n  tariff:\n    signal: market_price\n    fallback: 1\n");

        Assert.Empty(parsed.Warnings);
    }

    [Theory]
    [InlineData("role: price_import", "kind: current")]
    [InlineData("role: phase_power", "kind: price")]
    public void A_price_role_needs_a_price_signal_and_a_price_signal_needs_no_other_role(string role, string kind)
    {
        var signal = $"  market_price:\n    entity: sensor.rce_price\n    {kind}\n    unit: {(kind.EndsWith("price", StringComparison.Ordinal) ? "PLN/MWh" : "A")}\n    {(kind.EndsWith("current", StringComparison.Ordinal) ? "sign: unsigned\n    direction_from: none\n    " : string.Empty)}max_age: 5m\n    {role}\n    phase: L1\n";

        var parsed = TestConfigurations.Parse(TestConfigurations.ThreePhase(signal));

        Assert.Contains(parsed.Errors, e => e.Code == ConfigurationIssueCodes.RoleKindMismatch);
    }

    [Fact]
    public void Each_price_role_may_be_mapped_once_and_both_may_be_mapped()
    {
        var both = Signal(role: "price_import") + Signal(role: "price_export").Replace("market_price", "export_price", StringComparison.Ordinal).Replace("sensor.rce_price", "sensor.rce_sell", StringComparison.Ordinal);
        var twice = Signal(role: "price_import") + Signal(role: "price_import").Replace("market_price", "other_price", StringComparison.Ordinal).Replace("sensor.rce_price", "sensor.rce_other", StringComparison.Ordinal);

        Assert.Equal(ConfigurationStatus.Valid, TestConfigurations.Parse(TestConfigurations.ThreePhase(both)).Status);
        Assert.Contains(TestConfigurations.Parse(TestConfigurations.ThreePhase(twice)).Errors, e => e.Code == ConfigurationIssueCodes.DuplicateRole);
    }

    [Fact]
    public void A_price_signal_has_no_sign_no_direction_no_phase_and_no_sigma()
    {
        // Why: a price has no direction of flow and no measurement noise; keys that only make sense for a current or a power are refused.
        foreach (var extra in new[] { "    sign: unsigned\n", "    direction_from: none\n", "    phase: L1\n", "    sigma: 0.1\n" })
        {
            var parsed = Parse("economics:\n  currency: PLN\n", Signal(extra: extra));

            Assert.NotEmpty(parsed.Errors);
        }
    }

    [Fact]
    public void A_price_signal_without_a_role_is_valid_and_a_price_may_still_name_it()
    {
        var parsed = TestConfigurations.Valid(TestConfigurations.ThreePhase(Signal(role: string.Empty)) + "\neconomics:\n  currency: PLN\n  tariff:\n    signal: market_price\n    fallback: 1\n");

        Assert.IsType<DynamicPrice>(parsed.Configuration!.Economics!.Tariff);
        Assert.Empty(parsed.Warnings);
    }

    [Fact]
    public void The_zones_of_the_add_and_of_the_fallback_are_the_zones_a_device_may_name_as_a_source()
    {
        // Why: with a dynamic tariff the zones are those of the add (live) and of the fallback; a source naming one of them is valid, one that
        // neither has is the usual sources_unusable warning.
        var devices = "\ndevices:\n  - id: car\n    type: onoff\n    actuator: switch.car\n    phase: L1\n    rated_current_a: 6\n    rated_power_w: 1300\n    value_per_kwh: 2\n    sources: pv_and_zone:offpeak,nowhere\n";
        var parsed = TestConfigurations.Valid(TestConfigurations.ThreePhase(Signal()) + "\neconomics:\n  currency: PLN\n" + Holidays + "  tariff:\n    signal: market_price\n    fallback: 1\n    add:\n" + Zones + "\n" + devices);

        var warning = Single(parsed.Warnings, ConfigurationIssueCodes.SourcesUnusable);
        Assert.Contains("nowhere", warning.Message, StringComparison.Ordinal);
        Assert.Equal("pv_and_zone:offpeak", parsed.Configuration!.Devices.Single().Sources!.ToText());
    }

    [Fact]
    public void A_file_without_a_dynamic_spec_parses_exactly_as_before()
    {
        // Why: ECO-15 for the parser. A file with a price signal declared but no economics at all differs from the same file without it only
        // by that signal and its one warning; and the G13 file is untouched by slice 4.
        var g13 = File.ReadAllText(RepoPaths.Combine("deploy", "examples", "economics-g13.yaml"));
        var before = TestConfigurations.Valid(TestConfigurations.ThreePhase() + "\n" + g13);

        Assert.Empty(before.Warnings);
        Assert.IsType<ZonedPrice>(before.Configuration!.Economics!.Tariff);
    }

    [Fact]
    public void The_dynamic_example_in_deploy_examples_parses_without_issue()
    {
        var example = File.ReadAllText(RepoPaths.Combine("deploy", "examples", "economics-dynamic.yaml"));
        var signal = ExtractBlock(example, "signals:");
        var economics = ExtractBlock(example, "economics:");

        var parsed = TestConfigurations.Valid(TestConfigurations.ThreePhase(signal) + "\n" + economics);

        Assert.IsType<DynamicPrice>(parsed.Configuration!.Economics!.Tariff);
        Assert.Empty(parsed.Warnings);
    }

    /// <summary>The block of a root key in an example snippet (uncommented lines from the key to the next root key); for a signals block, its entries re-indented to the signals list of the test document.</summary>
    private static string ExtractBlock(string text, string key)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var start = Array.FindIndex(lines, l => l == key);
        Assert.True(start >= 0, $"the example has no uncommented '{key}'");
        var block = lines.Skip(start + 1).TakeWhile(l => l.Length == 0 || l.StartsWith(' ')).ToList();
        return key == "signals:" ? string.Join('\n', block) + "\n" : key + "\n" + string.Join('\n', block) + "\n";
    }
}
