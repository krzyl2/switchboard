using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Switchboard.Application.Decisions;
using Switchboard.Application.Guard;
using Switchboard.Application.Signals;
using Switchboard.Domain.Signals;
using Switchboard.Host.Hosting;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// GET api/guard feeds the Limits page (BRK-04: budget and recovery shown in the UI; SIG-05: which
/// signal makes a device unsafe). Its shape is a contract with the SPA's strict parser. POST
/// api/guard/kill-switch is the API side of SAFE-03 and answers only after the guard evaluated it.
/// </summary>
public sealed class GuardEndpointTests
{
    private const string Supervisor = "172.30.32.2";

    [Fact]
    public async Task Guard_status_shape_matches_the_spa_contract()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        _ = factory.Server;
        Publish(factory, l1: 33);

        var context = await SendAsync(factory, "GET", "/api/guard");
        using var json = JsonDocument.Parse(context.Response.Body);
        var body = json.RootElement;

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(["at", "mode", "safeState", "killSwitch", "breaker", "phases", "devices", "decisionLog", "stateFile", "decisions"], Names(body));
        Assert.Equal(["ceiling", "uiChoice", "effective", "note"], Names(body.GetProperty("mode")));
        Assert.Equal(["active", "scope", "reason", "phases", "since"], Names(body.GetProperty("safeState")));
        Assert.Equal(["engaged", "changedAt", "source"], Names(body.GetProperty("killSwitch")));
        Assert.Equal(["ratingA", "characteristic", "continuousMultiple", "continuousA", "budgetRecoveryTimeS", "tiers"], Names(body.GetProperty("breaker")));
        Assert.Equal(["upTo", "upToA", "upToW", "allowedS", "reaction"], Names(body.GetProperty("breaker").GetProperty("tiers")[0]));
        Assert.Equal(
            ["phase", "dataValid", "invalidReason", "currentA", "effectiveCurrentA", "multiple", "band", "reaction", "continuousA", "reserveThresholdA",
                "headroomA", "headroomW", "breakerHeadroomA", "budget", "budgetTrend", "recoveryRemainingS", "increasesAllowed", "reductionRequested", "state", "direction", "reportedAt"],
            Names(body.GetProperty("phases")[0]));
        Assert.Equal(
            ["id", "type", "phases", "guardState", "reason", "stages", "shedRank", "valuePerKwh", "shedPriority", "noShed", "canSwitchOff", "minOnRemainingS", "minOffRemainingS", "unsafeSignals"],
            Names(body.GetProperty("devices")[0]));
        Assert.Equal(["stage", "applied", "setpointA", "fresh", "invalidReason", "limit", "lastKnown"], Names(body.GetProperty("devices")[0].GetProperty("stages")[0]));
        Assert.Equal(["writable", "lastError", "dropped"], Names(body.GetProperty("decisionLog")));
        Assert.Equal(["status", "warning"], Names(body.GetProperty("stateFile")));
        var decision = body.GetProperty("decisions")[0];
        Assert.Equal(["id", "at", "scope", "device", "stage", "phase", "action", "kind", "reason", "overridden", "mode", "commanded", "refusedBy", "origin", "group", "target", "inputs"], Names(decision));
        Assert.Equal(["phase", "device", "mode", "killSwitch", "safeStateCause", "dispatch"], Names(decision.GetProperty("inputs")));
    }

    [Fact]
    public async Task Budget_recovery_headroom_and_reasons_are_reported_in_the_ui_units()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        _ = factory.Server;
        Publish(factory, l1: 33);

        using var json = JsonDocument.Parse((await SendAsync(factory, "GET", "/api/guard")).Response.Body);
        var body = json.RootElement;
        var l1 = body.GetProperty("phases").EnumerateArray().Single(p => p.GetProperty("phase").GetString() == "L1");

        // Headroom from the measured 33 A (not the shadow estimate), in A and, at 230 V, in W.
        var threshold = 25 - (2500.0 / 230);
        Assert.Equal(threshold - 33, l1.GetProperty("headroomA").GetDouble(), 6);
        Assert.Equal((threshold - 33) * 230, l1.GetProperty("headroomW").GetDouble(), 6);
        // The measured band sheds one device; with the heater held off ("as if applied") what is
        // left is in the reduce band, so the phase shows as reducing.
        Assert.Equal("shed_one_immediately", l1.GetProperty("reaction").GetString());
        Assert.Equal("reducing", l1.GetProperty("state").GetString());
        Assert.Equal(33 - 6.6, l1.GetProperty("effectiveCurrentA").GetDouble(), 6);
        Assert.True(l1.GetProperty("recoveryRemainingS").GetDouble() >= 0);
        Assert.Equal("shadow", body.GetProperty("mode").GetProperty("effective").GetString());
        Assert.Equal("B", body.GetProperty("breaker").GetProperty("characteristic").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("breaker").GetProperty("tiers")[5].GetProperty("upTo").ValueKind);
        Assert.Equal(0, body.GetProperty("breaker").GetProperty("tiers")[5].GetProperty("allowedS").GetDouble());

        var heater = body.GetProperty("devices").EnumerateArray().Single(d => d.GetProperty("id").GetString() == "heater_l1");
        Assert.Equal(("shed", "guard_tier_exceeded"), (heater.GetProperty("guardState").GetString(), heater.GetProperty("reason").GetString()));
        Assert.Equal("off", heater.GetProperty("stages")[0].GetProperty("limit").GetString());
        var charger = body.GetProperty("devices").EnumerateArray().Single(d => d.GetProperty("id").GetString() == "ev_charger");
        Assert.False(charger.GetProperty("canSwitchOff").GetBoolean());
        Assert.Equal(16, charger.GetProperty("stages")[0].GetProperty("setpointA").GetDouble());
    }

    [Fact]
    public async Task Unsafe_signals_name_what_makes_a_device_unsafe()
    {
        // SIG-05 in the API: the L2 meter current is stale, so the L2 heater is unsafe because of it.
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        _ = factory.Server;
        var harness = Publish(factory, l1: 8);
        harness.Time.Advance(TimeSpan.FromSeconds(95));
        harness.Push(("sensor.meter_current_a", "8", "A"), ("sensor.meter_current_c", "8", "A"));
        harness.Runner.Step();
        factory.Services.GetRequiredService<SignalSnapshotHolder>().Publish(harness.Holder.Current);
        factory.Services.GetRequiredService<GuardStatusHolder>().Publish(harness.Status.Current);

        using var json = JsonDocument.Parse((await SendAsync(factory, "GET", "/api/guard")).Response.Body);
        var heater = json.RootElement.GetProperty("devices").EnumerateArray().Single(d => d.GetProperty("id").GetString() == "heater_l2");

        Assert.Equal("unsafe", heater.GetProperty("guardState").GetString());
        Assert.Contains("grid_current_l2", heater.GetProperty("unsafeSignals").EnumerateArray().Select(s => s.GetString()));
    }

    [Fact]
    public async Task A_stale_draw_is_never_named_as_making_its_device_unsafe()
    {
        // The charger's own draw has not reported: it is not trusted for relief or increases, but
        // it holds nothing off, so the charger stays allowed and the draw is not among the signals
        // that make it unsafe (the published copy must match the behaviour).
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        _ = factory.Server;
        var harness = new GuardRunnerHarness(TestDevices.Document(TestDevices.ReferenceWithChargerDraw));
        harness.PushReference(l1: 8);
        harness.Runner.Step();
        factory.Services.GetRequiredService<SignalSnapshotHolder>().Publish(harness.Holder.Current);
        factory.Services.GetRequiredService<GuardStatusHolder>().Publish(harness.Status.Current);
        Assert.Contains(harness.Holder.Current.Snapshot.Signals, s => s.Name?.Value == "device_ev_charger_current" && s.State != SignalState.Fresh);

        using var json = JsonDocument.Parse((await SendAsync(factory, "GET", "/api/guard")).Response.Body);
        var charger = json.RootElement.GetProperty("devices").EnumerateArray().Single(d => d.GetProperty("id").GetString() == "ev_charger");

        Assert.Equal("allowed", charger.GetProperty("guardState").GetString());
        Assert.Empty(charger.GetProperty("unsafeSignals").EnumerateArray());
    }

    [Theory]
    [InlineData("text/plain", """{"engaged":true}""")]
    [InlineData("application/x-www-form-urlencoded", "engaged=true")]
    [InlineData("application/json", """{"engaged":"yes"}""")]
    [InlineData("application/json", """{"engaged":true,"source":"api"}""")]
    [InlineData("application/json", "true")]
    [InlineData("application/json", "{")]
    public async Task Kill_switch_post_requires_json(string contentType, string body)
    {
        // Only a JSON body: a form post from another page cannot send one without a CORS preflight.
        using var root = new TempWebRoot();
        using var factory = WithGuard(root.Path);
        _ = factory.Server;

        var context = await SendAsync(factory, "POST", "/api/guard/kill-switch", contentType, body);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.False(factory.Services.GetRequiredService<KillSwitchState>().Status.Engaged);
    }

    [Fact]
    public async Task Kill_switch_post_answers_with_the_evaluated_status()
    {
        using var root = new TempWebRoot();
        using var factory = WithGuard(root.Path);
        _ = factory.Server;

        var context = await SendAsync(factory, "POST", "/api/guard/kill-switch", "application/json", """{"engaged":true}""");
        using var json = JsonDocument.Parse(context.Response.Body);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var killSwitch = json.RootElement.GetProperty("killSwitch");
        Assert.Equal((true, "api"), (killSwitch.GetProperty("engaged").GetBoolean(), killSwitch.GetProperty("source").GetString()));
        Assert.Contains(json.RootElement.GetProperty("decisions").EnumerateArray(), d => d.GetProperty("action").GetString() == "kill_switch_engaged" && d.GetProperty("reason").GetString() == "kill_switch");
    }

    /// <summary>The reference installation with 33 A (or <paramref name="l1"/>) on L1, through the real store and guard.</summary>
    private static GuardRunnerHarness Publish(SwitchboardHostFactory factory, double l1)
    {
        var harness = new GuardRunnerHarness(TestDevices.Document());
        harness.PushReference(l1: 8);
        harness.Runner.Step();
        harness.Time.Advance(TimeSpan.FromSeconds(10));
        harness.PushReference(l1: l1);
        harness.Runner.Step();
        factory.Services.GetRequiredService<SignalSnapshotHolder>().Publish(harness.Holder.Current);
        factory.Services.GetRequiredService<GuardStatusHolder>().Publish(harness.Status.Current);
        factory.Services.GetRequiredService<IDecisionRecorder>().Record(harness.Recorder.Decisions);
        return harness;
    }

    /// <summary>The host with the guard running (and nothing else in the background).</summary>
    private static SwitchboardHostFactory WithGuard(string root) => new(root, replaceLinks: services =>
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType != typeof(GuardService)).ToList())
        {
            services.Remove(descriptor);
        }
    });

    private static List<string> Names(JsonElement element) => [.. element.EnumerateObject().Select(p => p.Name)];

    private static Task<HttpContext> SendAsync(SwitchboardHostFactory factory, string method, string path, string? contentType = null, string? body = null) =>
        factory.Server.SendAsync(context =>
        {
            context.Request.Method = method;
            context.Request.Path = path;
            context.Connection.RemoteIpAddress = IPAddress.Parse(Supervisor);
            if (contentType is not null)
            {
                context.Request.ContentType = contentType;
                context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body ?? string.Empty));
            }
        });
}
