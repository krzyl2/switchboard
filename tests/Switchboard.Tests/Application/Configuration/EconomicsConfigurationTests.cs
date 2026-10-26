using Switchboard.Application.Configuration;
using Switchboard.Domain.Economics;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Configuration;

/// <summary>
/// ECO-02: <c>economics:</c> is optional and additive. A user's tariff is typed by hand, so every mistake
/// in it must be a named issue (never a silently wrong price), and a file without the section, the whole
/// installed base of 0.9, must load exactly as it did.
/// </summary>
public sealed class EconomicsConfigurationTests
{
    private static readonly string Base = TestConfigurations.ThreePhase();

    private static readonly string G13 = File.ReadAllText(RepoPaths.Combine("deploy", "examples", "economics-g13.yaml"));

    private static ConfigurationResult Parse(string economics) => TestConfigurations.Parse(Base + "\n" + economics);

    private static string Zoned(string zones, string defaultZone = "default_zone: a", string extra = "") =>
        $"economics:\n  currency: PLN\n{extra}  tariff:\n    {defaultZone}\n    zones:\n{zones}";

    private static string Zone(string id, string price = "1.0", string when = "when: []") =>
        $"      - id: {id}\n        price: {price}\n        {when}\n";

    private static ConfigurationIssue Single(IEnumerable<ConfigurationIssue> issues, string code) => Assert.Single(issues, i => i.Code == code);

    [Fact]
    public void A_file_without_economics_loads_unchanged_with_no_economics_and_no_new_warning()
    {
        // The installed base of 0.9 has no economics: section. It must stay valid, with the same signals,
        // devices and warnings, and nothing economic may appear that dispatch could act on.
        var without = TestConfigurations.Valid(TestConfigurations.Example);
        var with = TestConfigurations.Valid(TestConfigurations.Example.TrimEnd() + "\n\n" + G13);

        Assert.Null(without.Configuration!.Economics);
        Assert.Equal(without.Warnings.Select(w => w.ToString()), with.Warnings.Select(w => w.ToString()));
        Assert.Equal(without.Configuration.Signals.Select(s => s.ToString()), with.Configuration!.Signals.Select(s => s.ToString()));
        Assert.Equal(without.Configuration.Devices.Select(d => (d.Id, d.Name, d.Type, d.ValuePerKwh, d.Start)), with.Configuration.Devices.Select(d => (d.Id, d.Name, d.Type, d.ValuePerKwh, d.Start)));
        Assert.Equal(
            (without.Configuration.Connection.Topology, without.Configuration.Connection.BreakerRating, without.Configuration.Connection.Characteristic, without.Configuration.Connection.Voltage),
            (with.Configuration.Connection.Topology, with.Configuration.Connection.BreakerRating, with.Configuration.Connection.Characteristic, with.Configuration.Connection.Voltage));
        Assert.NotNull(with.Configuration.Economics);
    }

    [Fact]
    public void An_empty_economics_section_is_the_same_as_none()
    {
        var parsed = TestConfigurations.Valid(Base + "\neconomics:\n");

        Assert.Null(parsed.Configuration!.Economics);
    }

    [Fact]
    public void A_misspelt_root_key_is_still_refused()
    {
        var parsed = Parse("economic:\n  currency: PLN\n");

        Assert.Equal(ConfigurationStatus.Invalid, parsed.Status);
        Single(parsed.Errors, ConfigurationIssueCodes.UnknownKey);
    }

    [Fact]
    public void The_G13_template_parses_to_three_zones_a_default_and_no_issue()
    {
        var parsed = TestConfigurations.Valid(Base + "\n" + G13);

        var economics = parsed.Configuration!.Economics!;
        var zones = Assert.IsType<ZonedPrice>(economics.Tariff);
        Assert.Equal(["morning_peak", "afternoon_peak", "offpeak"], zones.Zones.Select(z => z.Id));
        Assert.Equal("offpeak", zones.DefaultZone);
        Assert.Equal("PLN", economics.Currency);
        Assert.Equal(5, economics.Holidays.Dates.Count);
        Assert.Equal(new SettlementDefinition(SettlementModelId.HourlyNetting, new FlatPrice(0.35)), economics.Settlement);
        Assert.Empty(parsed.Warnings);
    }

    [Fact]
    public void A_flat_tariff_is_a_number()
    {
        var parsed = TestConfigurations.Valid(Base + "\neconomics:\n  currency: EUR\n  tariff: 0.31\n  settlement:\n    model: net_billing\n");

        var economics = parsed.Configuration!.Economics!;
        Assert.Equal(new FlatPrice(0.31), economics.Tariff);
        Assert.Equal(new SettlementDefinition(SettlementModelId.NetBilling, null), economics.Settlement);
    }

    [Fact]
    public void A_section_with_only_a_currency_is_valid_and_has_no_tariff()
    {
        var parsed = TestConfigurations.Valid(Base + "\neconomics:\n  currency: PLN\n");

        Assert.Null(parsed.Configuration!.Economics!.Tariff);
        Assert.Null(parsed.Configuration.Economics.Settlement);
    }

    [Fact]
    public void A_zoned_export_price_is_accepted_like_the_import_price()
    {
        var parsed = TestConfigurations.Valid(
            Base + "\neconomics:\n  currency: PLN\n  settlement:\n    model: net_billing\n    export_price:\n      default_zone: a\n      zones:\n" + Zone("a", "0.3"));

        Assert.IsType<ZonedPrice>(parsed.Configuration!.Economics!.Settlement!.ExportPrice);
    }

    [Theory]
    [InlineData("  tariff: -0.1\n", ConfigurationIssueCodes.OutOfRange)]
    [InlineData("  tariff: abc\n", ConfigurationIssueCodes.WrongType)]
    [InlineData("  tariff: .inf\n", ConfigurationIssueCodes.WrongType)]
    [InlineData("  tariff: .nan\n", ConfigurationIssueCodes.WrongType)]
    [InlineData("  tariff: [1, 2]\n", ConfigurationIssueCodes.WrongType)]
    [InlineData("  settlement:\n    model: net_metering\n", ConfigurationIssueCodes.InvalidValue)]
    [InlineData("  settlement:\n    export_price: 0.3\n", ConfigurationIssueCodes.MissingKey)]
    [InlineData("  settlement:\n    model: net_billing\n    export_price: -1\n", ConfigurationIssueCodes.OutOfRange)]
    [InlineData("  holidays: 2026-11-01\n", ConfigurationIssueCodes.WrongType)]
    [InlineData("  holidays: [2026-13-01]\n", ConfigurationIssueCodes.InvalidValue)]
    [InlineData("  holidays: [1 November]\n", ConfigurationIssueCodes.InvalidValue)]
    [InlineData("  tarif: 1\n", ConfigurationIssueCodes.UnknownKey)]
    public void An_invalid_section_gives_the_named_issue_and_an_invalid_document(string body, string code)
    {
        var parsed = Parse("economics:\n  currency: PLN\n" + body);

        Assert.Equal(ConfigurationStatus.Invalid, parsed.Status);
        Assert.Null(parsed.Configuration);
        Assert.Contains(parsed.Errors, i => i.Code == code && i.Path.StartsWith("economics", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("economics:\n  tariff: 1\n", ConfigurationIssueCodes.MissingKey)]
    [InlineData("economics:\n  currency: ABCDEFGHI\n", ConfigurationIssueCodes.InvalidValue)]
    [InlineData("economics:\n  currency: \"\"\n", ConfigurationIssueCodes.InvalidValue)]
    [InlineData("economics:\n  currency: [PLN]\n", ConfigurationIssueCodes.WrongType)]
    [InlineData("economics: 5\n", ConfigurationIssueCodes.WrongType)]
    public void The_currency_is_required_and_is_a_label_of_at_most_eight_characters(string document, string code)
    {
        var parsed = Parse(document);

        Assert.Contains(parsed.Errors, i => i.Code == code);
        Assert.Equal(ConfigurationStatus.Invalid, parsed.Status);
    }

    [Fact]
    public void A_currency_label_of_eight_characters_is_kept_as_written()
    {
        var parsed = TestConfigurations.Valid(Base + "\neconomics:\n  currency: ABCDEFGH\n");

        Assert.Equal("ABCDEFGH", parsed.Configuration!.Economics!.Currency);
    }

    [Fact]
    public void Two_zones_with_the_same_id_are_refused_at_the_second()
    {
        var parsed = Parse(Zoned(Zone("a") + Zone("a", "2.0")));

        var issue = Single(parsed.Errors, ConfigurationIssueCodes.DuplicateZoneId);
        Assert.Equal("economics.tariff.zones[1].id", issue.Path);
    }

    [Fact]
    public void A_default_zone_that_is_not_listed_is_refused()
    {
        var parsed = Parse(Zoned(Zone("a"), "default_zone: nope"));

        var issue = Single(parsed.Errors, ConfigurationIssueCodes.InvalidValue);
        Assert.Equal("economics.tariff.default_zone", issue.Path);
    }

    [Theory]
    [InlineData("\"7-13\"")]
    [InlineData("\"25:00-26:00\"")]
    [InlineData("\"07:00-07:00\"")]
    [InlineData("\"24:00-06:00\"")]
    [InlineData("\"07:00\"")]
    [InlineData("7")]
    public void Hours_that_do_not_parse_are_refused_with_their_path(string hours)
    {
        var parsed = Parse(Zoned(Zone("a", when: $"when: [{{ hours: [{hours}] }}]")));

        Assert.Equal(ConfigurationStatus.Invalid, parsed.Status);
        Assert.Contains(parsed.Errors, i => i.Path.StartsWith("economics.tariff.zones[0].when[0].hours", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("days: [someday]")]
    [InlineData("days: []")]
    [InlineData("months: [smarch]")]
    [InlineData("months: [4]")]
    [InlineData("hours: []")]
    [InlineData("colour: red")]
    public void A_rule_with_an_unknown_day_month_or_key_is_refused(string rule)
    {
        var parsed = Parse(Zoned(Zone("a", when: $"when: [{{ {rule} }}]")));

        Assert.Equal(ConfigurationStatus.Invalid, parsed.Status);
        Assert.Contains(parsed.Errors, i => i.Path.StartsWith("economics.tariff.zones[0].when[0]", StringComparison.Ordinal));
    }

    [Fact]
    public void A_zone_price_must_be_finite_and_not_negative()
    {
        Assert.Contains(Parse(Zoned(Zone("a", "-1"))).Errors, i => i.Code == ConfigurationIssueCodes.OutOfRange && i.Path == "economics.tariff.zones[0].price");
        Assert.Contains(Parse(Zoned(Zone("a", ".inf"))).Errors, i => i.Code == ConfigurationIssueCodes.WrongType && i.Path == "economics.tariff.zones[0].price");
        Assert.Contains(Parse(Zoned(Zone("a", "free"))).Errors, i => i.Code == ConfigurationIssueCodes.WrongType);
        Assert.Contains(Parse(Zoned("      - id: a\n        when: []\n")).Errors, i => i.Code == ConfigurationIssueCodes.MissingKey);
        Assert.Equal(ConfigurationStatus.Valid, Parse(Zoned(Zone("a", "0"))).Status);
    }

    [Theory]
    [InlineData("      - id: Peak\n        price: 1\n")]
    [InlineData("      - id: \"a,b\"\n        price: 1\n")]
    [InlineData("      - id: \"zone:a\"\n        price: 1\n")]
    public void A_zone_id_is_a_plain_name_so_that_sources_can_name_it_later(string zone)
    {
        var parsed = Parse(Zoned(zone, "default_zone: x"));

        Assert.Contains(parsed.Errors, i => i.Code == ConfigurationIssueCodes.InvalidValue && i.Path == "economics.tariff.zones[0].id");
    }

    [Fact]
    public void Without_a_default_zone_a_gap_makes_the_document_invalid()
    {
        var parsed = Parse(Zoned(Zone("n", when: "when: [{ hours: [\"22:00-06:00\"] }]"), defaultZone: string.Empty).Replace("    \n", string.Empty, StringComparison.Ordinal));

        Assert.Equal(ConfigurationStatus.Invalid, parsed.Status);
        var gap = Single(parsed.Errors, ConfigurationIssueCodes.ZoneGap);
        Assert.Contains("06:00", gap.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Zones_that_cover_all_time_need_no_default_zone()
    {
        var zones = Zone("n", when: "when: [{ hours: [\"22:00-06:00\"] }]") + Zone("d", "2.0", "when: [{ hours: [\"06:00-22:00\"] }]");

        var parsed = Parse(Zoned(zones, defaultZone: string.Empty).Replace("    \n", string.Empty, StringComparison.Ordinal));

        Assert.Equal(ConfigurationStatus.Valid, parsed.Status);
        Assert.Empty(parsed.Warnings);
    }

    [Fact]
    public void A_default_zone_with_rules_of_its_own_that_still_leave_a_gap_is_a_warning_not_an_error()
    {
        // The gap falls to the default zone, so the schedule is usable; but a default that also lists hours was probably meant to be exhaustive.
        var zones = Zone("n", when: "when: [{ hours: [\"22:00-06:00\"] }]") + Zone("d", "2.0", "when: [{ hours: [\"08:00-18:00\"] }]");

        var parsed = Parse(Zoned(zones, "default_zone: d"));

        Assert.Equal(ConfigurationStatus.Valid, parsed.Status);
        var warning = Single(parsed.Warnings, ConfigurationIssueCodes.ZoneGap);
        Assert.Equal("economics.tariff.default_zone", warning.Path);
    }

    [Fact]
    public void A_default_zone_with_no_rules_is_the_normal_way_to_say_the_rest_and_raises_nothing()
    {
        var parsed = Parse(Zoned(Zone("n", when: "when: [{ hours: [\"22:00-06:00\"] }]") + Zone("d", "2.0"), "default_zone: d"));

        Assert.Equal(ConfigurationStatus.Valid, parsed.Status);
        Assert.Empty(parsed.Warnings);
    }

    [Fact]
    public void A_holiday_list_with_no_date_of_the_current_year_is_a_warning_and_the_document_stays_valid()
    {
        // LoadedAt of the test fixtures is in 2026.
        var parsed = Parse(Zoned(Zone("a"), extra: "  holidays: [2025-12-25]\n"));

        Assert.Equal(ConfigurationStatus.Valid, parsed.Status);
        var warning = Single(parsed.Warnings, ConfigurationIssueCodes.HolidaysListStale);
        Assert.Equal("economics.holidays", warning.Path);
    }

    [Fact]
    public void A_holiday_list_with_a_date_of_the_current_year_is_not_stale()
    {
        var parsed = Parse(Zoned(Zone("a"), extra: "  holidays: [2025-12-25, 2026-01-01]\n"));

        Assert.Empty(parsed.Warnings);
    }

    [Fact]
    public void An_empty_list_is_warned_about_only_when_the_zones_depend_on_holidays()
    {
        var workday = Parse(Zoned(Zone("a", when: "when: [{ days: [workday] }]") + Zone("b"), "default_zone: b"));
        var holiday = Parse(Zoned(Zone("a", when: "when: [{ days: [holiday] }]") + Zone("b"), "default_zone: b"));
        var weekend = Parse(Zoned(Zone("a", when: "when: [{ days: [weekend, monday] }]") + Zone("b"), "default_zone: b"));

        Assert.Single(workday.Warnings, w => w.Code == ConfigurationIssueCodes.HolidaysListStale);
        Assert.Single(holiday.Warnings, w => w.Code == ConfigurationIssueCodes.HolidaysListStale);
        Assert.Empty(weekend.Warnings);
        Assert.Equal(ConfigurationStatus.Valid, workday.Status);
    }

    [Fact]
    public void An_error_inside_economics_does_not_hide_other_errors_of_the_file()
    {
        var parsed = TestConfigurations.Parse(Base + "\nbogus: 1\neconomics:\n  currency: PLN\n  tariff: -1\n");

        Assert.Contains(parsed.Errors, i => i.Code == ConfigurationIssueCodes.UnknownKey);
        Assert.Contains(parsed.Errors, i => i.Code == ConfigurationIssueCodes.OutOfRange);
    }

    [Fact]
    public void Economics_may_not_carry_a_currency_symbol_default_in_the_shipped_defaults()
    {
        // No price, label or symbol ships with the add-on: the defaults file holds parameters only.
        var defaults = File.ReadAllText(RepoPaths.Combine("src", "Switchboard.Infrastructure", "Configuration", "control-defaults.yaml"));

        Assert.DoesNotContain("currency", defaults, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PLN", defaults, StringComparison.Ordinal);
    }
}
