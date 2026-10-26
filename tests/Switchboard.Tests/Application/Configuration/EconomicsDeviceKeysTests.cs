using Switchboard.Application.Configuration;
using Switchboard.Domain.Economics;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Configuration;

/// <summary>
/// The per-device economic keys (ECO-04, 14-ECONOMICS slice 2): value_follows, value_factor and sources. They are typed by hand, so
/// a mistake must be a named issue and a problem may only ever narrow a device (drop a zone, stay static), never widen it. A 0.9
/// file and a slice 1 file, the whole installed base, must load with no new key and no new warning.
/// </summary>
public sealed class EconomicsDeviceKeysTests
{
    private const string Tariff = """
        economics:
          currency: PLN
          holidays: [2026-11-01]
          tariff:
            default_zone: offpeak
            zones:
              - id: peak
                price: 1.60
                when:
                  - { days: [workday], hours: ["07:00-13:00"] }
              - id: offpeak
                price: 0.60
                when: []
          settlement:
            model: hourly_netting
            export_price: 0.35

        """;

    private static string Heater(string extra = "", string id = "heater", string phase = "L1", string actuator = "switch.heater_1") => $"""
          - id: {id}
            type: onoff
            actuator: {actuator}
            phase: {phase}
            rated_current_a: 6.6
            rated_power_w: 1500
            value_per_kwh: 0.35
        {extra}
        """;

    private static ConfigurationResult Parse(string devices, string economics = Tariff) => TestConfigurations.Parse(TestDevices.Document(devices, extra: economics));

    private static SwitchboardConfiguration Valid(string devices, string economics = Tariff) =>
        TestConfigurations.Valid(TestDevices.Document(devices, extra: economics)).Configuration!;

    private static DeviceDefinition One(string key, string economics = Tariff) => Valid(Heater($"    {key}"), economics).Devices.Single();

    private static ConfigurationIssue Warning(ConfigurationResult result, string code) => Assert.Single(result.Warnings, w => w.Code == code);

    [Fact]
    public void A_09_file_loads_with_every_device_unspecified_and_static()
    {
        // Why: the economic keys are additive. A file written for 0.9 keeps 0.9 behaviour: no sources (unspecified), static value, factor 1.
        var result = TestConfigurations.Valid(TestConfigurations.Example);

        Assert.All(result.Configuration!.Devices, d => Assert.Equal((null, null, ValueFollows.Static, 1.0), (d.Sources, d.SourcesDeclared, d.ValueFollows, d.ValueFactor)));
        Assert.DoesNotContain(result.Warnings, w => w.Code is "sources_unusable" or "value_follows_without_price");
    }

    [Fact]
    public void A_slice_1_file_with_a_tariff_and_no_device_keys_adds_no_warning_and_no_source()
    {
        var without = TestConfigurations.Valid(TestConfigurations.Example);
        var with = TestConfigurations.Valid(TestConfigurations.Example.TrimEnd() + "\n\n" + File.ReadAllText(RepoPaths.Combine("deploy", "examples", "economics-g13.yaml")));

        Assert.Equal(without.Warnings.Select(w => w.ToString()), with.Warnings.Select(w => w.ToString()));
        Assert.All(with.Configuration!.Devices, d => Assert.Equal((null, ValueFollows.Static, 1.0), (d.Sources, d.ValueFollows, d.ValueFactor)));
        Assert.Equal(without.Configuration!.Devices.Select(d => d.Value), with.Configuration.Devices.Select(d => d.Value));
    }

    [Fact]
    public void Sources_are_parsed_in_all_three_forms()
    {
        Assert.Equal("pv_only", One("sources: pv_only").Sources!.ToText());
        Assert.Equal("any", One("sources: any").Sources!.ToText());
        var zone = One("sources: pv_and_zone:offpeak");
        Assert.Equal(("pv_and_zone:offpeak", "pv_and_zone:offpeak"), (zone.Sources!.ToText(), zone.SourcesDeclared!.ToText()));
        Assert.Equal("pv_and_zone:offpeak,peak", One("sources: pv_and_zone:peak,offpeak").Sources!.ToText());
    }

    [Fact]
    public void Sources_are_no_longer_a_reserved_key_to_ignore()
    {
        // Why: the 0.9 parser accepted sources with a 'reserved' warning and dropped it. Now it is read, so that warning must be gone.
        var result = TestConfigurations.Valid(TestDevices.Document(Heater("    sources: pv_only"), extra: Tariff));

        Assert.DoesNotContain(result.Warnings, w => w.Code == "reserved_key_ignored" && w.Path.EndsWith(".sources", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("sources: grid")]
    [InlineData("sources: \"pv_and_zone:\"")]
    [InlineData("sources: pv_and_zone:peak,,offpeak")]
    [InlineData("sources: pv_and_zone:peak,peak")]
    [InlineData("sources: [pv_only]")]
    [InlineData("sources: 3")]
    public void Malformed_sources_make_the_document_invalid_at_the_key(string key)
    {
        var result = Parse(Heater($"    {key}"));

        Assert.Equal(ConfigurationStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Path == "devices[0].sources");
    }

    [Fact]
    public void An_unknown_zone_is_a_warning_and_the_device_is_narrowed_not_widened()
    {
        // Why: a typo'd zone must not make the device run in a zone the user did not name, and must not silently vanish either.
        var result = Parse(Heater("    sources: pv_and_zone:offpeak,ghost"));

        var warning = Warning(result, "sources_unusable");
        Assert.Equal("devices[0].sources", warning.Path);
        Assert.Contains("ghost", warning.Message, StringComparison.Ordinal);
        var device = result.Configuration!.Devices.Single();
        Assert.Equal(("pv_and_zone:offpeak", "pv_and_zone:ghost,offpeak"), (device.Sources!.ToText(), device.SourcesDeclared!.ToText()));
    }

    [Fact]
    public void Only_unknown_zones_narrow_a_device_to_surplus_only()
    {
        var device = Valid(Heater("    sources: pv_and_zone:ghost")).Devices.Single();

        Assert.Equal("pv_only", device.Sources!.ToText());
    }

    [Fact]
    public void A_zone_source_without_any_tariff_is_dropped_with_a_warning()
    {
        // Failure table: no tariff, a device declares a zone source -> zone sources dropped (sources_unusable).
        var result = Parse(Heater("    sources: pv_and_zone:offpeak"), "");

        Warning(result, "sources_unusable");
        Assert.Equal("pv_only", result.Configuration!.Devices.Single().Sources!.ToText());
    }

    [Fact]
    public void A_flat_tariff_has_no_zones_so_a_named_zone_is_unusable()
    {
        var result = Parse(Heater("    sources: pv_and_zone:offpeak"), "economics:\n  currency: PLN\n  tariff: 0.5\n");

        Warning(result, "sources_unusable");
    }

    [Fact]
    public void Any_and_pv_only_need_no_zone_and_warn_about_nothing()
    {
        Assert.Empty(Parse(Heater("    sources: any"), "").Warnings.Where(w => w.Code == "sources_unusable"));
        Assert.Empty(Parse(Heater("    sources: pv_only")).Warnings.Where(w => w.Code == "sources_unusable"));
    }

    [Fact]
    public void Value_follows_and_factor_are_parsed_when_the_price_exists()
    {
        var device = One("value_follows: import_price\n    value_factor: 3");

        Assert.Equal((ValueFollows.ImportPrice, 3.0), (device.ValueFollows, device.ValueFactor));
        Assert.Equal(new ValueSpec(0.35, ValueFollows.ImportPrice, 3), device.Value);
        Assert.Equal(ValueFollows.ExportPrice, One("value_follows: export_price").ValueFollows);
        Assert.Equal(ValueFollows.Static, One("value_follows: static").ValueFollows);
    }

    [Fact]
    public void Following_a_price_the_file_has_no_spec_for_is_a_warning_and_the_device_stays_static()
    {
        // Why: the design's failure table; a missing price must leave the configured number, not an invented one.
        var noExport = "economics:\n  currency: PLN\n  tariff: 0.5\n";
        var export = Parse(Heater("    value_follows: export_price"), noExport);
        var import = Parse(Heater("    value_follows: import_price"), "");

        Assert.Equal("devices[0].value_follows", Warning(export, "value_follows_without_price").Path);
        Warning(import, "value_follows_without_price");
        Assert.Equal(ValueFollows.Static, export.Configuration!.Devices.Single().ValueFollows);
        Assert.Equal(ValueFollows.Static, import.Configuration!.Devices.Single().ValueFollows);
    }

    [Fact]
    public void An_unknown_value_follows_is_an_error()
    {
        var result = Parse(Heater("    value_follows: spot_price"));

        Assert.Contains(result.Errors, e => (e.Code, e.Path) == ("invalid_value", "devices[0].value_follows"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.0")]
    public void A_value_factor_must_be_above_zero(string factor)
    {
        var result = Parse(Heater($"    value_factor: {factor}"));

        Assert.Contains(result.Errors, e => (e.Code, e.Path) == ("out_of_range", "devices[0].value_factor"));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("\"2\"")]
    [InlineData(".nan")]
    [InlineData(".inf")]
    public void A_value_factor_must_be_a_finite_number(string factor)
    {
        var result = Parse(Heater($"    value_factor: {factor}"));

        Assert.Equal(ConfigurationStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Path == "devices[0].value_factor");
    }

    [Fact]
    public void A_zero_static_value_is_still_allowed_as_before()
    {
        // Existing rule (value_per_kwh at least 0) is untouched by the new keys.
        Assert.Equal(0, Valid(Heater().Replace("value_per_kwh: 0.35", "value_per_kwh: 0")).Devices.Single().ValuePerKwh);
    }

    private static string Element(string id, string phase, string extra) =>
        Heater($"    rotation_group: cwu\n{extra}", id, phase, $"switch.{id}");

    [Fact]
    public void Rotation_group_members_with_equal_economic_keys_are_accepted()
    {
        var configuration = Valid(
            Element("a", "L1", "    sources: pv_and_zone:peak,offpeak\n    value_follows: import_price\n    value_factor: 2")
            + "\n"
            + Element("b", "L2", "    sources: pv_and_zone:offpeak,peak\n    value_follows: import_price\n    value_factor: 2"));

        Assert.Equal(2, configuration.Devices.Length);
    }

    [Theory]
    [InlineData("    value_factor: 2")]
    [InlineData("    value_follows: import_price")]
    [InlineData("    sources: pv_only")]
    [InlineData("    sources: pv_and_zone:offpeak")]
    public void Rotation_group_members_that_differ_in_an_economic_key_are_refused(string differing)
    {
        // Why: the phase ranking rotates members as interchangeable; members with different value or sources are not.
        var result = Parse(Element("a", "L1", "    sources: any") + "\n" + Element("b", "L2", differing));

        Assert.Equal(ConfigurationStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Code == "rotation_group_mismatch" && e.Message.Contains("value_follows, value_factor and sources", StringComparison.Ordinal));
    }

    [Fact]
    public void A_member_with_no_sources_differs_from_one_with_sources()
    {
        // Unspecified (0.9 behaviour) and declared are different behaviours, so they are not interchangeable.
        var result = Parse(Element("a", "L1", "") + "\n" + Element("b", "L2", "    sources: pv_only"));

        Assert.Contains(result.Errors, e => e.Code == "rotation_group_mismatch");
    }

    [Fact]
    public void Net_metering_is_refused_as_deferred_not_as_a_typo()
    {
        // ECO-03: net_metering stays behind the interface, unbuilt. The user must be told it is deferred, not that it is misspelt.
        var result = Parse(Heater(), "economics:\n  currency: PLN\n  settlement:\n    model: net_metering\n");

        var error = Assert.Single(result.Errors, e => e.Path == "economics.settlement.model");
        Assert.Equal("invalid_value", error.Code);
        Assert.Contains("deferred", error.Message, StringComparison.Ordinal);
        Assert.Contains("hourly_netting", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_misspelt_settlement_model_is_still_a_plain_unknown_value()
    {
        var result = Parse(Heater(), "economics:\n  currency: PLN\n  settlement:\n    model: net_meter\n");

        var error = Assert.Single(result.Errors, e => e.Path == "economics.settlement.model");
        Assert.DoesNotContain("deferred", error.Message, StringComparison.Ordinal);
    }
}
