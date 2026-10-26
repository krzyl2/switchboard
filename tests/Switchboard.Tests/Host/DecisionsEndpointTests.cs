using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Application.Decisions;
using System.Collections.Immutable;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Modes;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Units;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// GET api/decisions is the decision log for the runtime UI (MODE-03): recent decisions, newest
/// first, filterable by device, phase and reason, paged with an opaque cursor. Filters are
/// validated, because a typo that silently matched nothing would read as "no decisions".
/// </summary>
public sealed class DecisionsEndpointTests
{
    private const string Supervisor = "172.30.32.2";
    private static readonly DateTimeOffset Noon = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Filters_and_pages_with_before()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        _ = factory.Server;
        var recorder = factory.Services.GetRequiredService<IDecisionRecorder>();
        var heaters = Enumerable.Range(0, 5).Select(i => TestDecisions.Shed(Noon.AddSeconds(i), "heater_l1", Phase.L1)).ToList();
        recorder.Record([.. heaters, TestDecisions.Shed(Noon, "heater_l2", Phase.L2, ReasonCode.GuardBudgetLow), TestDecisions.Event(Noon)]);

        var first = await GetAsync(factory, "/api/decisions?device=heater_l1&limit=2");
        var second = await GetAsync(factory, $"/api/decisions?device=heater_l1&limit=2&before={Uri.EscapeDataString(first.GetProperty("next").GetString()!)}");
        var byReason = await GetAsync(factory, "/api/decisions?reason=guard_budget_low");
        var byPhase = await GetAsync(factory, "/api/decisions?phase=L2");

        Assert.Equal(["items", "next", "logWritable"], first.EnumerateObject().Select(p => p.Name));
        Assert.Equal(heaters.Select(d => d.Id).Reverse().Take(2), Ids(first));
        Assert.Equal(heaters.Select(d => d.Id).Reverse().Skip(2).Take(2), Ids(second));
        Assert.Equal("heater_l2", Assert.Single(byReason.GetProperty("items").EnumerateArray()).GetProperty("device").GetString());
        Assert.Equal("L2", Assert.Single(byPhase.GetProperty("items").EnumerateArray()).GetProperty("phase").GetString());
        var item = first.GetProperty("items")[0];
        Assert.Equal(("switch_off", "guard_tier_exceeded", "lockout_min_on", "mode_shadow", false), (item.GetProperty("action").GetString(), item.GetProperty("reason").GetString(), item.GetProperty("overridden").GetString(), item.GetProperty("refusedBy").GetString(), item.GetProperty("commanded").GetBoolean()));
        Assert.Equal(33, item.GetProperty("inputs").GetProperty("phase").GetProperty("currentA").GetDouble());
    }

    // BAL-07: the Log page can show the balance a dispatch decision was made beside; an unknown stays null.
    [Fact]
    public async Task A_dispatch_decision_carries_the_balance_it_was_made_beside()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        _ = factory.Server;
        DecisionInputs Inputs(BalanceDecisionInputs? balance) => DecisionInputs.None with
        {
            Dispatch = new DispatchDecisionInputs([], Amperes.Zero, Amperes.Zero, null, ImmutableDictionary<Phase, Amperes>.Empty, ImmutableDictionary<ConstraintKey, Amperes>.Empty, null, null, TimeSpan.Zero, TimeSpan.Zero, null, OverrideMode.Auto) { Balance = balance },
        };
        Decision Start(string id, DateTimeOffset at, string device, Phase phase, BalanceDecisionInputs? balance) =>
            new(id, at, DecisionScope.Device, DeviceName.Parse(device), null, phase, DecisionAction.SwitchOn, ReasonCode.BalanceSurplus, null, OperatingMode.Shadow, false, ReasonCode.ModeShadow, Inputs(balance), DecisionOrigin.Dispatch);
        var withBalance = Start("with-balance", Noon, "heater_l1", Phase.L1, new BalanceDecisionInputs(240.5, 3100, 1650, null, 150, 525, 1320, false));
        var without = Start("without-balance", Noon.AddSeconds(1), "heater_l2", Phase.L2, null);
        factory.Services.GetRequiredService<IDecisionRecorder>().Record([withBalance, without]);

        var items = (await GetAsync(factory, "/api/decisions")).GetProperty("items");
        var balance = items.EnumerateArray().Single(i => i.GetProperty("device").GetString() == "heater_l1").GetProperty("inputs").GetProperty("dispatch").GetProperty("balance");

        Assert.Equal(["balanceWh", "freeW", "loadW", "projectionWh", "loWh", "hiWh", "tRemS", "burning"], balance.EnumerateObject().Select(p => p.Name));
        Assert.Equal((240.5, 3100, 1650, JsonValueKind.Null, 1320), (balance.GetProperty("balanceWh").GetDouble(), balance.GetProperty("freeW").GetDouble(), balance.GetProperty("loadW").GetDouble(), balance.GetProperty("projectionWh").ValueKind, balance.GetProperty("tRemS").GetDouble()));
        Assert.Equal(JsonValueKind.Null, items.EnumerateArray().Single(i => i.GetProperty("device").GetString() == "heater_l2").GetProperty("inputs").GetProperty("dispatch").GetProperty("balance").ValueKind);
    }

    // ECO-09: the Log page can show which energy a dispatch decision was judged on; no economics section means null, and no class is written as none.
    [Fact]
    public async Task A_dispatch_decision_carries_the_economics_it_was_made_beside()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        _ = factory.Server;
        DecisionInputs Inputs(EconomicsDecisionInputs? economics) => DecisionInputs.None with
        {
            Dispatch = new DispatchDecisionInputs([], Amperes.Zero, Amperes.Zero, null, ImmutableDictionary<Phase, Amperes>.Empty, ImmutableDictionary<ConstraintKey, Amperes>.Empty, null, null, TimeSpan.Zero, TimeSpan.Zero, null, OverrideMode.Auto) { Economics = economics },
        };
        Decision Start(string id, DateTimeOffset at, string device, Phase phase, ReasonCode reason, EconomicsDecisionInputs? economics) =>
            new(id, at, DecisionScope.Device, DeviceName.Parse(device), null, phase, DecisionAction.SwitchOn, reason, null, OperatingMode.Shadow, false, ReasonCode.ModeShadow, Inputs(economics), DecisionOrigin.Dispatch);
        var zone = Start("zone", Noon, "ev_charger", Phase.L2, ReasonCode.ZoneAllowed, new EconomicsDecisionInputs("offpeak", 0.60, null, PriceBasis.Zone, 2.03, ValueBasis.Static, 2.03, EnergyClass.Zone, "pv_and_zone:offpeak", SettlementModelId.HourlyNetting));
        var refused = Start("none", Noon.AddSeconds(1), "heater_l1", Phase.L1, ReasonCode.SourceNotAllowed, new EconomicsDecisionInputs(null, null, null, null, 0.35, ValueBasis.Fallback, 0.35, null, null, SettlementModelId.NetBilling));
        var without = Start("without", Noon.AddSeconds(2), "heater_l2", Phase.L2, ReasonCode.HeadroomAvailable, null);
        factory.Services.GetRequiredService<IDecisionRecorder>().Record([zone, refused, without]);

        var items = (await GetAsync(factory, "/api/decisions")).GetProperty("items").EnumerateArray().ToList();
        JsonElement Economics(string device) => items.Single(i => i.GetProperty("device").GetString() == device).GetProperty("inputs").GetProperty("dispatch").GetProperty("economics");
        var car = Economics("ev_charger");

        Assert.Equal(["zone", "importPrice", "exportPrice", "priceBasis", "vEff", "valueBasis", "orderKey", "class", "sources", "settlement"], car.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("offpeak", 0.60, JsonValueKind.Null, "zone", 2.03, "static", "zone", "pv_and_zone:offpeak", "hourly_netting"),
            (car.GetProperty("zone").GetString(), car.GetProperty("importPrice").GetDouble(), car.GetProperty("exportPrice").ValueKind, car.GetProperty("priceBasis").GetString(), car.GetProperty("vEff").GetDouble(), car.GetProperty("valueBasis").GetString(), car.GetProperty("class").GetString(), car.GetProperty("sources").GetString(), car.GetProperty("settlement").GetString()));
        var heater = Economics("heater_l1");
        Assert.Equal(("none", "fallback", "net_billing", JsonValueKind.Null, JsonValueKind.Null), (heater.GetProperty("class").GetString(), heater.GetProperty("valueBasis").GetString(), heater.GetProperty("settlement").GetString(), heater.GetProperty("priceBasis").ValueKind, heater.GetProperty("sources").ValueKind));
        Assert.Equal(JsonValueKind.Null, Economics("heater_l2").ValueKind);
    }

    [Theory]
    [InlineData("reason=made_up_code")]
    [InlineData("reason=GuardTierExceeded")]
    [InlineData("phase=L4")]
    [InlineData("phase=l1")]
    [InlineData("device=switch.heater_1")]
    [InlineData("limit=0")]
    [InlineData("limit=501")]
    [InlineData("limit=ten")]
    [InlineData("before=")]
    [InlineData("before=abc-1")]
    [InlineData("phase=L1&phase=L2")]
    public async Task Unknown_reason_filter_rejected(string query)
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        _ = factory.Server;

        var context = await SendAsync(factory, "/api/decisions?" + query);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    private static List<string?> Ids(JsonElement page) => [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString())];

    private static async Task<JsonElement> GetAsync(SwitchboardHostFactory factory, string pathAndQuery)
    {
        var context = await SendAsync(factory, pathAndQuery);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var json = JsonDocument.Parse(context.Response.Body);
        return json.RootElement.Clone();
    }

    private static Task<HttpContext> SendAsync(SwitchboardHostFactory factory, string pathAndQuery) =>
        factory.Server.SendAsync(context =>
        {
            var split = pathAndQuery.Split('?', 2);
            context.Request.Method = "GET";
            context.Request.Path = split[0];
            context.Request.QueryString = split.Length > 1 ? new QueryString("?" + split[1]) : QueryString.Empty;
            context.Connection.RemoteIpAddress = IPAddress.Parse(Supervisor);
        });
}
