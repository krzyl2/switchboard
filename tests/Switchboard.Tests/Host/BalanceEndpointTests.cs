using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Switchboard.Application.Balance;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Modes;
using Switchboard.Domain.Balance;
using Switchboard.Domain.Units;
using Switchboard.Host.Web;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// GET api/balance (BAL-07): what the dashboard shows of the hourly balance. The measured balance and
/// what the allowance rule made of it (free power, own load, projection, band, time to the :00) must
/// agree with the dispatch step they come from, and an unknown must say why instead of reading as a
/// zero. The SPA parses the answer strictly (web/src/api/balance.ts); the closed lists and the sample
/// payload it parses are held to what the endpoint writes.
/// </summary>
public sealed class BalanceEndpointTests
{
    private const string Supervisor = "172.30.32.2";
    private static readonly string Contract = RepoPaths.Combine("web", "src", "api", "balance.ts");
    private static readonly string SampleFile = RepoPaths.Combine("web", "src", "api", "balance.sample.json");
    private static readonly DateTimeOffset HourStart = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset At = HourStart + TimeSpan.FromMinutes(38);

    private static HourlyBalanceStatus Status(double? wh, BalanceUnknown reason = BalanceUnknown.None) =>
        new(new BalanceReading(wh, HourStart, reason), At, TimeSpan.FromMinutes(22));

    private static DispatchStatus Dispatch(BalanceView? view) =>
        DispatchStatus.Initial(At, ModeState.Resolve(OperatingMode.Shadow, controlAllowed: false, configurationValid: true, stateConfirmed: true)) with { Balance = view };

    private static BalanceView Known() =>
        new(240.5, new Watts(3100), new Watts(1650), TimeSpan.FromMinutes(22), new BalanceBand(150, 525, TimeSpan.FromMinutes(22), false), 410.25);

    [Fact]
    public void A_known_balance_carries_what_the_rule_used_in_Wh_and_W()
    {
        var response = BalanceEndpoint.Build(Status(240.5), Dispatch(Known()));

        Assert.True(response.Configured);
        Assert.True(response.Known);
        Assert.Equal("none", response.UnknownReason);
        Assert.Equal(240.5, response.Wh);
        Assert.Equal((3100, 1650, 410.25), (response.FreeW, response.LoadW, response.ProjectionWh));
        Assert.Equal(new BalanceBandDto(150, 525, false), response.Band);
        Assert.Equal(22 * 60, response.RuleSecondsToNextHour);
        Assert.Equal(At, response.RuleAt);
    }

    [Fact]
    public void Inside_the_burn_window_the_band_says_it_is_burning()
    {
        var view = Known() with { UntilNext = TimeSpan.FromMinutes(9), Band = new BalanceBand(150, 300, TimeSpan.FromMinutes(9), true) };

        var response = BalanceEndpoint.Build(Status(240.5), Dispatch(view));

        Assert.Equal(new BalanceBandDto(150, 300, true), response.Band);
        Assert.Equal(9 * 60, response.RuleSecondsToNextHour);
    }

    [Fact]
    public void Without_counters_nothing_is_configured_and_nothing_is_reported()
    {
        var response = BalanceEndpoint.Build(null, Dispatch(null));

        Assert.False(response.Configured);
        Assert.False(response.Known);
        Assert.Null(response.Wh);
        Assert.Null(response.Band);
        Assert.Null(response.ProjectionWh);
    }

    [Theory]
    [InlineData("balance", "stale")]
    [InlineData("no step", "no_dispatch_step")]
    [InlineData("not of the hour", "balance_not_used")]
    [InlineData("free power", "free_power_unknown")]
    [InlineData("own load", "own_load_unknown")]
    public void An_unknown_says_why_and_reports_no_figure_as_zero(string case_, string reason)
    {
        var (status, dispatch) = case_ switch
        {
            "balance" => (Status(null, BalanceUnknown.Stale), Dispatch(new BalanceView(null, null, null, TimeSpan.Zero, null, null))),
            "no step" => (Status(240.5), (DispatchStatus?)null),
            "not of the hour" => (Status(240.5), Dispatch(new BalanceView(null, new Watts(3100), new Watts(1650), TimeSpan.Zero, null, null))),
            "free power" => (Status(240.5), Dispatch(Known() with { Free = null, ProjectionWh = null })),
            _ => (Status(240.5), Dispatch(Known() with { Load = null, ProjectionWh = null })),
        };

        var response = BalanceEndpoint.Build(status, dispatch);

        Assert.True(response.Configured);
        Assert.False(response.Known);
        Assert.Equal(reason, response.UnknownReason);
        Assert.Null(response.ProjectionWh);
        if (case_ == "balance")
        {
            Assert.Null(response.Wh);
            Assert.Null(response.Band);
            Assert.Null(response.FreeW);
        }
    }

    [Fact]
    public void The_measured_balance_and_its_reason_stay_as_before()
    {
        var response = BalanceEndpoint.Build(Status(null, BalanceUnknown.CounterReset));

        Assert.Equal(("counter_reset", null), (response.Reason, response.Wh));
        Assert.Equal(22 * 60, response.SecondsToNextHour);
        Assert.Equal(HourStart, response.HourStart);
    }

    [Fact]
    public void Spa_closed_lists_match_the_backend_exactly()
    {
        var tracker = Enum.GetValues<BalanceUnknown>().Select(ApiText.Snake).Order(StringComparer.Ordinal);
        Assert.Equal(tracker, SpaContract.List(Contract, "BALANCE_REASONS").Order(StringComparer.Ordinal));
        Assert.Equal(
            tracker.Concat(BalanceEndpoint.RuleUnknownReasons).Order(StringComparer.Ordinal),
            SpaContract.List(Contract, "BALANCE_UNKNOWN_REASONS").Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Spa_sample_has_exactly_the_keys_the_endpoint_writes()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        _ = factory.Server;

        // Through the host: no counters mapped. Direct: the running hour with every nested object.
        var empty = await GetAsync(factory);
        var running = Serialize(BalanceEndpoint.Build(Status(240.5), Dispatch(Known())));

        Assert.False(empty.GetProperty("configured").GetBoolean());
        var sample = SpaContract.Sample(SampleFile, running);
        Assert.Equal(SpaContract.Describe([empty]).Where(l => !l.StartsWith("$.band", StringComparison.Ordinal)), SpaContract.Describe([sample]).Where(l => !l.StartsWith("$.band", StringComparison.Ordinal)));
        Assert.Equal(SpaContract.Describe([running]), SpaContract.Describe([sample]));
    }

    private static JsonElement Serialize(BalanceResponse response) => JsonDocument.Parse(JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement.Clone();

    private static async Task<JsonElement> GetAsync(SwitchboardHostFactory factory)
    {
        var context = await factory.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/api/balance";
            c.Connection.RemoteIpAddress = IPAddress.Parse(Supervisor);
        });
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var json = JsonDocument.Parse(context.Response.Body);
        return json.RootElement.Clone();
    }
}
