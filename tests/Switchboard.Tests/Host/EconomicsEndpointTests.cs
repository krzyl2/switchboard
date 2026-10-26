using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Switchboard.Application.Configuration;
using Switchboard.Host.Web;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// GET api/economics (ECO-01, ECO-02): what the tariff says now on the local clock, and nothing else. It is
/// the first place a user can check the zone against the invoice, so the answer must follow the local time
/// zone, say plainly when nothing is configured, and carry the configuration's own issues.
/// </summary>
public sealed class EconomicsEndpointTests
{
    private const string Supervisor = "172.30.32.2";
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly string G13 = File.ReadAllText(RepoPaths.Combine("deploy", "examples", "economics-g13.yaml"));

    private static ConfigurationResult Config(string economics) => TestConfigurations.Parse(TestConfigurations.ThreePhase() + "\n" + economics);

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) => new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void The_tariff_now_is_the_zone_of_the_local_clock_with_its_price_and_the_next_change()
    {
        // 06:30 UTC on 15 July is 08:30 in Warsaw: the morning peak, not the night.
        var response = EconomicsEndpoint.Build(Config(G13), Utc(2026, 7, 15, 6, 30), Warsaw);

        Assert.True(response.Configured);
        Assert.Equal("PLN", response.Currency);
        var tariff = response.Tariff!;
        Assert.Equal(("zoned", "morning_peak", 1.10, "zone"), (tariff.Kind, tariff.Zone, tariff.ImportPrice, tariff.PriceBasis));
        Assert.Equal(Utc(2026, 7, 15, 11, 0), tariff.NextChangeAt);
        Assert.Equal("offpeak", tariff.NextZone);
        Assert.Empty(response.Issues);
        Assert.False(response.HolidaysListStale);
    }

    [Fact]
    public void The_settlement_and_export_price_are_echoed_as_configured()
    {
        var response = EconomicsEndpoint.Build(Config(G13), Utc(2026, 7, 15, 6, 30), Warsaw);

        Assert.Equal("hourly_netting", response.Settlement!.Model);
        Assert.Equal(new ExportPriceDto("flat", null, 0.35, "flat"), response.Settlement.ExportPrice);
    }

    [Fact]
    public void A_flat_tariff_has_a_price_and_no_zone_and_no_next_change()
    {
        var response = EconomicsEndpoint.Build(Config("economics:\n  currency: EUR\n  tariff: 0.31\n"), Utc(2026, 7, 15, 6, 30), Warsaw);

        var tariff = response.Tariff!;
        Assert.Equal(("flat", null, 0.31, "flat", null, null), (tariff.Kind, tariff.Zone, tariff.ImportPrice, tariff.PriceBasis, tariff.NextChangeAt, tariff.NextZone));
        Assert.Null(response.Settlement);
    }

    [Fact]
    public void Without_a_tariff_the_section_is_configured_and_says_nothing_about_prices()
    {
        var response = EconomicsEndpoint.Build(Config("economics:\n  currency: PLN\n"), Utc(2026, 7, 15, 6, 30), Warsaw);

        Assert.True(response.Configured);
        Assert.Null(response.Tariff);
    }

    [Fact]
    public void The_same_instant_is_a_different_zone_in_a_different_time_zone()
    {
        // 21:30 UTC on 15 July: 23:30 in Warsaw (off-peak), but 21:30 in UTC (the afternoon peak ended at 22:00 only for Warsaw's clock).
        var warsaw = EconomicsEndpoint.Build(Config(G13), Utc(2026, 7, 15, 21, 30), Warsaw).Tariff!;
        var utc = EconomicsEndpoint.Build(Config(G13), Utc(2026, 7, 15, 21, 30), TimeZoneInfo.Utc).Tariff!;

        Assert.Equal("offpeak", warsaw.Zone);
        Assert.Equal("afternoon_peak", utc.Zone);
    }

    [Fact]
    public void Without_an_economics_section_only_configured_false_is_said()
    {
        var response = EconomicsEndpoint.Build(TestConfigurations.Valid(TestConfigurations.Example), Utc(2026, 7, 15, 6, 30), Warsaw);

        Assert.False(response.Configured);
        Assert.Null(response.Currency);
        Assert.Null(response.Tariff);
        Assert.Null(response.Settlement);
        Assert.Empty(response.Issues);
        Assert.False(response.HolidaysListStale);
    }

    [Fact]
    public void With_no_configuration_at_all_it_is_not_configured_either()
    {
        var response = EconomicsEndpoint.Build(null, Utc(2026, 7, 15, 6, 30), Warsaw);

        Assert.False(response.Configured);
        Assert.Empty(response.Issues);
    }

    [Fact]
    public void An_invalid_economics_section_reports_its_issues_and_is_not_configured()
    {
        var response = EconomicsEndpoint.Build(Config("economics:\n  currency: PLN\n  tariff: -1\n"), Utc(2026, 7, 15, 6, 30), Warsaw);

        Assert.False(response.Configured);
        var issue = Assert.Single(response.Issues);
        Assert.Equal((ConfigurationIssueCodes.OutOfRange, "economics.tariff"), (issue.Code, issue.Path));
    }

    [Fact]
    public void Issues_of_other_sections_do_not_leak_into_the_answer()
    {
        var response = EconomicsEndpoint.Build(TestConfigurations.Parse(TestConfigurations.ThreePhase() + "\nbogus: 1\n"), Utc(2026, 7, 15, 6, 30), Warsaw);

        Assert.Empty(response.Issues);
    }

    [Fact]
    public void Warnings_of_the_section_are_listed_and_the_list_goes_stale_with_the_local_year()
    {
        // The list is renewed for 2026 only: on New Year's Day in Warsaw (still 31 December in UTC) it is already stale.
        var zones = "economics:\n  currency: PLN\n  holidays: [2026-12-25]\n  tariff:\n    default_zone: a\n    zones:\n      - { id: a, price: 1, when: [] }\n";
        var config = Config(zones);

        var december = EconomicsEndpoint.Build(config, Utc(2026, 12, 31, 22, 30), Warsaw);
        var january = EconomicsEndpoint.Build(config, Utc(2026, 12, 31, 23, 30), Warsaw);

        Assert.False(december.HolidaysListStale);
        Assert.True(january.HolidaysListStale);
    }

    [Fact]
    public void A_parser_warning_of_the_section_is_carried()
    {
        var response = EconomicsEndpoint.Build(Config("economics:\n  currency: PLN\n  holidays: [2025-12-25]\n  tariff: 1\n"), Utc(2026, 7, 15, 6, 30), Warsaw);

        Assert.True(response.Configured);
        Assert.Contains(response.Issues, i => i.Code == ConfigurationIssueCodes.HolidaysListStale);
        Assert.True(response.HolidaysListStale);
    }

    [Fact]
    public async Task Through_the_host_a_fresh_install_answers_configured_false_with_the_agreed_keys()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        _ = factory.Server;

        var body = await GetAsync(factory);
        Assert.False(body.GetProperty("configured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("tariff").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("settlement").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("currency").ValueKind);
        Assert.Equal(0, body.GetProperty("issues").GetArrayLength());
        Assert.True(body.TryGetProperty("at", out _));
    }

    [Fact]
    public void The_shape_of_a_configured_answer_has_exactly_the_documented_keys()
    {
        var json = JsonSerializer.SerializeToElement(
            EconomicsEndpoint.Build(Config(G13), Utc(2026, 7, 15, 6, 30), Warsaw),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(["at", "configured", "currency", "devices", "holidaysListStale", "issues", "savings", "settlement", "tariff"], json.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["fallbackInForce", "importPrice", "kind", "nextChangeAt", "nextZone", "priceBasis", "signal", "zone"],
            json.GetProperty("tariff").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["exportPrice", "model"], json.GetProperty("settlement").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    private static ConfigurationResult WithDevices(string devices, string economics) =>
        TestConfigurations.Parse(TestDevices.Document(devices, extra: economics));

    private const string Heater = """
          - id: heater
            type: onoff
            actuator: switch.heater_1
            phase: L1
            rated_current_a: 6.6
            rated_power_w: 1500
            value_per_kwh: 0.35
        """;

    [Fact]
    public void Each_device_echoes_its_effective_value_now_and_its_sources()
    {
        // 06:30 UTC on 15 July is the morning peak (1.10): a device following the import price with factor 2 is worth 2.20 now, not its static 0.35.
        // The sources are echoed as declared and as narrowed to the tariff's zones, so a typo'd zone is visible.
        var result = WithDevices(
            Heater + "\n    value_follows: import_price\n    value_factor: 2\n    sources: pv_and_zone:offpeak,ghost",
            G13);

        var device = Assert.Single(EconomicsEndpoint.Build(result, Utc(2026, 7, 15, 6, 30), Warsaw).Devices!);

        Assert.Equal(("heater", 0.35, "import_price", 2.0, 2.2, "followed"), (device.Id, device.ValuePerKwh, device.ValueFollows, device.ValueFactor, device.VEff, device.ValueBasis));
        Assert.Equal(("pv_and_zone:offpeak", "pv_and_zone:ghost,offpeak"), (device.Sources, device.SourcesDeclared));
    }

    [Fact]
    public void The_effective_value_follows_the_zone_of_the_clock()
    {
        // Why: a value that follows the tariff must change at the zone boundary, on the local clock.
        var result = WithDevices(Heater + "\n    value_follows: import_price", G13);

        Assert.Equal(1.10, EconomicsEndpoint.Build(result, Utc(2026, 7, 15, 6, 30), Warsaw).Devices!.Single().VEff);
        Assert.Equal(0.60, EconomicsEndpoint.Build(result, Utc(2026, 7, 15, 21, 30), Warsaw).Devices!.Single().VEff);
    }

    [Fact]
    public void A_device_with_no_economic_keys_is_static_and_has_no_sources()
    {
        var response = EconomicsEndpoint.Build(WithDevices(Heater, G13), Utc(2026, 7, 15, 6, 30), Warsaw);

        var device = Assert.Single(response.Devices!);
        Assert.Equal((0.35, "static", 1.0, "static", null, null), (device.VEff, device.ValueFollows, device.ValueFactor, device.ValueBasis, device.Sources, device.SourcesDeclared));
    }

    [Fact]
    public void Devices_are_echoed_with_their_static_value_also_without_an_economics_section()
    {
        // The 0.9 case: nothing configured, nothing economic, but the device list and its value stay readable.
        var response = EconomicsEndpoint.Build(WithDevices(Heater, ""), Utc(2026, 7, 15, 6, 30), Warsaw);

        Assert.False(response.Configured);
        Assert.Equal(0.35, Assert.Single(response.Devices!).VEff);
    }

    [Fact]
    public void An_invalid_configuration_echoes_no_devices()
    {
        var response = EconomicsEndpoint.Build(WithDevices(Heater + "\n    value_factor: 0", G13), Utc(2026, 7, 15, 6, 30), Warsaw);

        Assert.Empty(response.Devices!);
    }

    private static async Task<JsonElement> GetAsync(SwitchboardHostFactory factory)
    {
        var context = await factory.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/api/economics";
            c.Connection.RemoteIpAddress = IPAddress.Parse(Supervisor);
        });
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var json = JsonDocument.Parse(context.Response.Body);
        return json.RootElement.Clone();
    }
}
