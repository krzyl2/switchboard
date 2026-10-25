using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Application.Decisions;
using Switchboard.Application.Presentation;
using Switchboard.Application.Signals;
using Switchboard.Application.State;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// The runtime UI's endpoints: read-only views of the presentation hub and of the configuration.
/// Before the first step they answer with nothing known (never invented values); the statistics
/// range is validated, because a typo that silently matched nothing would read as "no energy";
/// the decision log filters by kind.
/// </summary>
public sealed class PresentationEndpointTests
{
    private const string Supervisor = "172.30.32.2";
    private static readonly DateTimeOffset Noon = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Before_the_first_step_power_knows_nothing_and_says_why()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);

        var power = await GetAsync(factory, "/api/power");

        Assert.False(power.GetProperty("dataValid").GetBoolean());
        Assert.Equal(JsonValueKind.Null, power.GetProperty("evaluatedAt").ValueKind);
        Assert.Equal(0, power.GetProperty("phases").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, power.GetProperty("total").GetProperty("load").GetProperty("w").ValueKind);
        Assert.Equal("configuration_invalid", power.GetProperty("total").GetProperty("load").GetProperty("unknown").GetString());
    }

    [Fact]
    public async Task Power_reports_the_balance_of_the_last_step()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        var loop = new GuardRunnerHarness(TestDevices.Document());
        var hub = factory.Services.GetRequiredService<PresentationHub>();
        loop.Runner.Stepped += (_, step) => hub.Observe(step);
        loop.PushReference(l1: 8);
        loop.Runner.Step();

        var power = await GetAsync(factory, "/api/power");

        Assert.Equal(["L1", "L2", "L3"], power.GetProperty("phases").EnumerateArray().Select(p => p.GetProperty("phase").GetString()));
        Assert.Equal(["heater_l1", "heater_l2", "heater_l3", "ev_charger"], power.GetProperty("devices").EnumerateArray().Select(d => d.GetProperty("id").GetString()));
        var heater = power.GetProperty("devices")[0];
        Assert.Equal(("rated_model", 1500.0), (heater.GetProperty("source").GetString(), heater.GetProperty("totalW").GetDouble()));
        Assert.Equal("unknown", power.GetProperty("devices")[3].GetProperty("source").GetString());
    }

    [Theory]
    [InlineData("from=2026-10-24T00:00:00Z")]
    [InlineData("to=2026-10-24T00:00:00Z")]
    [InlineData("from=yesterday&to=today")]
    [InlineData("from=2026-10-24T00:00:00&to=2026-10-25T00:00:00")]
    [InlineData("from=2026-10-25T00:00:00Z&to=2026-10-24T00:00:00Z")]
    [InlineData("from=2026-10-20T00:00:00Z&to=2026-10-25T00:00:00Z")]
    [InlineData("from=2026-10-24T00:00:00Z&to=2026-10-25T00:00:00Z&to=2026-10-25T01:00:00Z")]
    [InlineData("day=2026-10-24")]
    public async Task A_malformed_statistics_range_is_400(string query)
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);

        var context = await SendAsync(factory, "GET", "/api/statistics?" + query);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Fact]
    public async Task Statistics_range_returns_only_its_buckets_and_their_sums_and_the_last_hour_is_an_hour_of_buckets()
    {
        // The browser asks for its local day; only those buckets travel, with server-side sums. The
        // last hour is 60 min of buckets whatever the bucket length.
        using var root = new TempWebRoot();
        var clock = new SteppableTime(Noon + TimeSpan.FromHours(2));
        using var factory = new SwitchboardHostFactory(root.Path, signalTime: clock);
        var hub = factory.Services.GetRequiredService<PresentationHub>();
        var at = Noon;
        for (; at <= Noon + TimeSpan.FromHours(2); at += TimeSpan.FromSeconds(10))
        {
            hub.Observe(TestSteps.Step(at));
        }

        var all = await GetAsync(factory, "/api/statistics");
        var range = await GetAsync(factory, "/api/statistics?from=2026-10-24T12:30:00Z&to=2026-10-24T13:00:00.000Z");

        Assert.Equal(24, all.GetProperty("buckets").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, all.GetProperty("range").ValueKind);
        Assert.Equal(6, range.GetProperty("buckets").GetArrayLength());
        Assert.Equal(6, range.GetProperty("range").GetProperty("buckets").GetInt32());
        Assert.Equal(1800, range.GetProperty("range").GetProperty("totals").GetProperty("unobservedS").GetDouble(), 6);
        Assert.Equal(12, all.GetProperty("lastHour").GetProperty("buckets").GetInt32());
        Assert.Equal("2026-10-24T13:00:00+00:00", all.GetProperty("lastHour").GetProperty("from").GetString());
    }

    [Fact]
    public async Task The_last_hour_after_downtime_is_the_last_hour_not_the_evening_before()
    {
        // The Home Assistant host was off from 22:00 to 07:00: after the restart no bucket exists for
        // those nine hours. At 07:10 "where PV went in the last hour" covers 06:10 onwards, so it holds
        // the two buckets since the restart, never last evening's PV as "since 21:20".
        using var root = new TempWebRoot();
        var evening = new DateTimeOffset(2026, 10, 24, 21, 0, 0, TimeSpan.Zero);
        var morning = new DateTimeOffset(2026, 10, 25, 7, 0, 0, TimeSpan.Zero);
        var before = TestStatistics.Hub();
        for (var at = evening; at <= evening + TimeSpan.FromHours(1); at += TimeSpan.FromSeconds(10))
        {
            before.Observe(TestSteps.Step(at));
        }

        var clock = new SteppableTime(morning + TimeSpan.FromMinutes(10));
        using var factory = new SwitchboardHostFactory(root.Path, signalTime: clock);
        var hub = factory.Services.GetRequiredService<PresentationHub>();
        var saved = before.Statistics.ToState(evening + TimeSpan.FromHours(1));
        hub.Restore(new PresentationLoad<StatisticsState>(saved, StateFileStatus.Loaded, null), new PresentationLoad<ExcursionState>(null, StateFileStatus.Fresh, null), morning);
        for (var at = morning; at <= morning + TimeSpan.FromMinutes(10); at += TimeSpan.FromSeconds(10))
        {
            hub.Observe(TestSteps.Step(at));
        }

        var lastHour = (await GetAsync(factory, "/api/statistics")).GetProperty("lastHour");

        Assert.Equal("2026-10-25T06:10:00+00:00", lastHour.GetProperty("from").GetString());
        Assert.Equal(2, lastHour.GetProperty("buckets").GetInt32());
        Assert.Equal(600, lastHour.GetProperty("totals").GetProperty("observedS").GetDouble() + lastHour.GetProperty("totals").GetProperty("unobservedS").GetDouble(), 6);
    }

    [Fact]
    public async Task After_a_clock_step_back_the_last_hour_never_starts_after_it_ends()
    {
        // The signal clock stepped back across a bucket edge (NTP, or a development replay restarted
        // on the same data folder): the newest bucket starts after now. The SPA refuses a range whose
        // from is after its to, which would blank three screens; such a bucket is not "the last hour".
        using var root = new TempWebRoot();
        var clock = new SteppableTime(Noon + TimeSpan.FromMinutes(30));
        using var factory = new SwitchboardHostFactory(root.Path, signalTime: clock);
        var hub = factory.Services.GetRequiredService<PresentationHub>();
        for (var at = Noon; at <= Noon + TimeSpan.FromHours(2); at += TimeSpan.FromSeconds(10))
        {
            hub.Observe(TestSteps.Step(at));
        }

        var all = await GetAsync(factory, "/api/statistics");
        var hour = await GetAsync(factory, "/api/statistics?from=2026-10-24T12:00:00Z&to=2026-10-24T13:00:00Z");

        var lastHour = all.GetProperty("lastHour");
        var from = lastHour.GetProperty("from").GetDateTimeOffset();
        var to = lastHour.GetProperty("to").GetDateTimeOffset();
        Assert.True(from <= to, $"{from:O} after {to:O}");
        Assert.Equal(Noon + TimeSpan.FromMinutes(30), to);
        Assert.Equal(7, lastHour.GetProperty("buckets").GetInt32());

        // The stretch the clock has not reached again is not shown at all (live dev replay restart:
        // the dashboard's hour said "observed 60 min of 19 min"); it returns once the clock passes it.
        Assert.All(all.GetProperty("buckets").EnumerateArray(), b => Assert.True(b.GetProperty("start").GetDateTimeOffset() <= to));
        Assert.Equal(7, hour.GetProperty("range").GetProperty("buckets").GetInt32());
        Assert.True(hour.GetProperty("range").GetProperty("totals").GetProperty("unobservedS").GetDouble() <= TimeSpan.FromMinutes(35).TotalSeconds);
    }

    [Fact]
    public async Task A_lost_phase_voltage_still_answers_power_with_finite_numbers()
    {
        // A tripped L3 reports 0 V: exactly when the dashboard must keep answering. Converting at 0 V
        // gave NaN, which the serializer refuses, so every poll failed for as long as the loss lasted.
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        var loop = new Switchboard.Tests.Application.Presentation.PresentationLoop(TestDevices.Document());
        Switchboard.Tests.Application.Presentation.PowerViewBuilderTests.PushMeter(loop, "-700", "-700", "0", pv: "0", pvCurrents: "0", currentL3: "0", voltageL3: "0");
        factory.Services.GetRequiredService<PresentationHub>().Observe(loop.Step());

        var power = await GetAsync(factory, "/api/power");

        var l3 = power.GetProperty("phases").EnumerateArray().Single(p => p.GetProperty("phase").GetString() == "L3");
        Assert.False(l3.GetProperty("voltageMeasured").GetBoolean());
        Assert.NotEmpty(l3.GetProperty("bar").EnumerateArray());
    }

    [Fact]
    public async Task Devices_lists_the_declared_entities_only_from_a_valid_configuration()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        var empty = await GetAsync(factory, "/api/devices");

        var replay = new ReplayHarness(ReplayFixtures.SyntheticData, TestConfigurations.Example);
        replay.RunUntil(ReplayFixtures.SyntheticData.Start + TimeSpan.FromSeconds(30));
        factory.Services.GetRequiredService<SignalSnapshotHolder>().Publish(replay.Current);
        var devices = await GetAsync(factory, "/api/devices");

        Assert.Equal(("missing", 0), (empty.GetProperty("configuration").GetString(), empty.GetProperty("devices").GetArrayLength()));
        Assert.Equal("valid", devices.GetProperty("configuration").GetString());
        var charger = devices.GetProperty("devices").EnumerateArray().Single(d => d.GetProperty("id").GetString() == "ev_charger");
        Assert.Equal("number.goe_422752_amp", charger.GetProperty("stages")[0].GetProperty("actuator").GetString());
        Assert.Equal("sensor.goe_422752_nrg_4", charger.GetProperty("draw").GetProperty("current")[0].GetProperty("entity").GetString());
        Assert.Equal("on_change", charger.GetProperty("stateReports").GetString());
        Assert.Equal(JsonValueKind.Null, charger.GetProperty("stateMaxAgeS").ValueKind);
        Assert.Equal("Ładowarka go-e", charger.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Kind_filter_returns_only_that_kind_and_every_decision_carries_its_kind()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        var recorder = factory.Services.GetRequiredService<IDecisionRecorder>();
        recorder.Record([
            TestDecisions.Shed(Noon, "heater_l1", Phase.L1),
            TestDecisions.Event(Noon, DecisionAction.KillSwitchEngaged, ReasonCode.KillSwitch),
            TestSteps.Device(Noon, "ev_charger", DecisionAction.SetSetpoint, ReasonCode.ReserveThreshold, null, origin: DecisionOrigin.Dispatch),
            TestSteps.Device(Noon, "heater_l2", DecisionAction.MarkedUnresponsive, ReasonCode.DeviceUnresponsive, null, origin: DecisionOrigin.Dispatch),
        ]);

        var dispatch = await GetAsync(factory, "/api/decisions?kind=dispatch");
        var system = await GetAsync(factory, "/api/decisions?kind=system");
        var all = await GetAsync(factory, "/api/decisions");

        Assert.Equal("ev_charger", Assert.Single(dispatch.GetProperty("items").EnumerateArray()).GetProperty("device").GetString());
        Assert.Equal("kill_switch_engaged", Assert.Single(system.GetProperty("items").EnumerateArray()).GetProperty("action").GetString());
        Assert.Equal(
            ["verification", "dispatch", "system", "protection"],
            all.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("kind").GetString()));
    }

    [Theory]
    [InlineData("kind=guard")]
    [InlineData("kind=Dispatch")]
    [InlineData("kind=foreign-load")]
    [InlineData("kind=")]
    public async Task Unknown_kind_is_400(string query)
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);

        var context = await SendAsync(factory, "GET", "/api/decisions?" + query);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    private static async Task<JsonElement> GetAsync(SwitchboardHostFactory factory, string pathAndQuery)
    {
        var context = await SendAsync(factory, "GET", pathAndQuery);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var json = JsonDocument.Parse(context.Response.Body);
        return json.RootElement.Clone();
    }

    internal static Task<HttpContext> SendAsync(SwitchboardHostFactory factory, string method, string pathAndQuery) =>
        factory.Server.SendAsync(context =>
        {
            var split = pathAndQuery.Split('?', 2);
            context.Request.Method = method;
            context.Request.Path = split[0];
            context.Request.QueryString = split.Length > 1 ? new QueryString("?" + split[1]) : QueryString.Empty;
            context.Connection.RemoteIpAddress = IPAddress.Parse(Supervisor);
        });
}
