using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Effector;
using Switchboard.Application.Modes;
using Switchboard.Application.Signals;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Host.Hosting;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// GET api/dispatch is what Stage 4's screens and the Limits page build on: per device the intended
/// state next to the reported one, why, what waits (deferral, settling, unresponsive) and the
/// manual override. Its shape is a contract with the SPA's strict parser, so it is pinned here key
/// by key. POST api/devices/{id}/override is SAFE-04 from the UI: a JSON body only, a declared
/// device only (SAFE-05), answered after the guard runner evaluated it.
/// </summary>
public sealed class DispatchEndpointTests
{
    private const string Supervisor = "172.30.32.2";
    private static readonly DateTimeOffset At = new(2026, 10, 24, 12, 0, 30, TimeSpan.Zero);

    [Fact]
    public async Task Dispatch_status_shape_is_the_contract_for_the_spa()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        _ = factory.Server;
        factory.Services.GetRequiredService<DispatchStatusHolder>().Publish(Full());

        var context = await SendAsync(factory, "GET", "/api/dispatch");
        using var json = JsonDocument.Parse(context.Response.Body);
        var body = json.RootElement;

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(["at", "mode", "commandPath", "phases", "devices"], Names(body));
        Assert.Equal(["ceiling", "uiChoice", "effective", "note"], Names(body.GetProperty("mode")));
        Assert.False(body.GetProperty("commandPath").GetBoolean());
        var phase = body.GetProperty("phases")[0];
        Assert.Equal(["phase", "envelopeA", "inFlightA", "dataValid", "invalidReason", "foreignW", "foreignBaselineW", "rank", "groups"], Names(phase));
        Assert.Equal(["group", "envelopeA", "inFlightA", "increasesAllowed"], Names(phase.GetProperty("groups")[0]));
        var device = body.GetProperty("devices")[0];
        Assert.Equal(
            ["id", "type", "phases", "rotationGroup", "circuitGroup", "intended", "reported", "reportedFresh", "matches", "reason", "origin", "startRank", "pending",
                "lockout", "deferred", "settling", "unresponsive", "mismatches", "override", "draw", "history", "start", "baseline", "commandWarning", "selfStarted"],
            Names(device));
        Assert.Equal(("restore_only", "headroom"), (device.GetProperty("start").GetString(), body.GetProperty("devices")[1].GetProperty("start").GetString()));
        Assert.Equal(["level", "since", "commanded"], Names(device.GetProperty("baseline")));
        Assert.Equal(["since"], Names(device.GetProperty("commandWarning")));
        Assert.Equal(["relays", "setpointA", "enabled"], Names(device.GetProperty("intended")));
        Assert.Equal(["minOnRemainingS", "minOffRemainingS"], Names(device.GetProperty("lockout")));
        Assert.Equal(["target", "reason", "refusedBy", "since", "dueAt"], Names(device.GetProperty("deferred")));
        Assert.Equal(["since", "retryAt"], Names(device.GetProperty("unresponsive")));
        Assert.Equal(["mode", "changedAt", "source"], Names(device.GetProperty("override")));
        Assert.Equal(["currentA", "powerW", "fresh"], Names(body.GetProperty("devices")[1].GetProperty("draw")));
        Assert.Equal(["at", "kind", "expected", "measured", "unit", "verdict", "commandedAt", "from", "to"], Names(device.GetProperty("history")[0]));
    }

    [Fact]
    public async Task Values_are_the_ledgers_in_the_ui_vocabulary()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        _ = factory.Server;
        factory.Services.GetRequiredService<DispatchStatusHolder>().Publish(Full());

        using var json = JsonDocument.Parse((await SendAsync(factory, "GET", "/api/dispatch")).Response.Body);
        var heater = json.RootElement.GetProperty("devices")[0];
        var charger = json.RootElement.GetProperty("devices")[1];

        Assert.Equal(("on_off", "cwu_heaters", false, "headroom_available", "dispatch"), (heater.GetProperty("type").GetString(), heater.GetProperty("rotationGroup").GetString(), heater.GetProperty("matches").GetBoolean(), heater.GetProperty("reason").GetString(), heater.GetProperty("origin").GetString()));
        Assert.Equal([true], heater.GetProperty("intended").GetProperty("relays").EnumerateArray().Select(r => r.GetBoolean()));
        Assert.Equal([false], heater.GetProperty("reported").GetProperty("relays").EnumerateArray().Select(r => r.GetBoolean()));
        Assert.Equal(("lockout_min_off", "open", "force_on", "api"), (heater.GetProperty("deferred").GetProperty("refusedBy").GetString(), heater.GetProperty("settling").GetString(), heater.GetProperty("override").GetProperty("mode").GetString(), heater.GetProperty("override").GetProperty("source").GetString()));
        Assert.Equal(42, heater.GetProperty("lockout").GetProperty("minOffRemainingS").GetDouble());

        // Newest first, as the device card lists them.
        Assert.Equal(["observed", "command"], heater.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("kind").GetString()));
        Assert.Equal(("watts", "mismatch"), (heater.GetProperty("history")[1].GetProperty("unit").GetString(), heater.GetProperty("history")[1].GetProperty("verdict").GetString()));

        Assert.Equal(JsonValueKind.Null, charger.GetProperty("intended").GetProperty("relays").ValueKind);
        Assert.Equal(13, charger.GetProperty("intended").GetProperty("setpointA").GetDouble());
        Assert.Equal(JsonValueKind.Null, charger.GetProperty("deferred").ValueKind);
        Assert.Equal(("auto", JsonValueKind.Null), (charger.GetProperty("override").GetProperty("mode").GetString(), charger.GetProperty("override").GetProperty("source").ValueKind));
    }

    [Fact]
    public async Task Before_the_first_evaluation_nothing_is_claimed()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        _ = factory.Server;

        using var json = JsonDocument.Parse((await SendAsync(factory, "GET", "/api/dispatch")).Response.Body);

        Assert.Equal((false, 0, 0), (json.RootElement.GetProperty("commandPath").GetBoolean(), json.RootElement.GetProperty("phases").GetArrayLength(), json.RootElement.GetProperty("devices").GetArrayLength()));
        Assert.Equal("shadow", json.RootElement.GetProperty("mode").GetProperty("effective").GetString());
    }

    [Theory]
    [InlineData("text/plain", """{"mode":"force_on"}""")]
    [InlineData("application/x-www-form-urlencoded", "mode=force_on")]
    [InlineData("application/json", """{"mode":"FORCE_ON"}""")]
    [InlineData("application/json", """{"mode":"on"}""")]
    [InlineData("application/json", """{"mode":"force_on","until":"never"}""")]
    [InlineData("application/json", """{"mode":1}""")]
    [InlineData("application/json", "\"force_on\"")]
    [InlineData("application/json", "{")]
    public async Task Override_post_requires_json_with_one_of_the_three_modes(string contentType, string body)
    {
        // A form post from another page cannot send a JSON body without a CORS preflight.
        using var root = new TempWebRoot();
        using var factory = WithGuard(root.Path);
        _ = factory.Server;
        Configure(factory);

        var context = await SendAsync(factory, "POST", "/api/devices/heater_l1/override", contentType, body);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Empty(factory.Services.GetRequiredService<IDeviceOverrideView>().Current.Devices);
    }

    [Theory]
    [InlineData("garage_door")]
    [InlineData("switch.heater_1")]
    [InlineData("Heater_L1")]
    public async Task Override_of_an_undeclared_device_is_404(string id)
    {
        // SAFE-05: only a device the user declared can be addressed, and an entity ID never.
        using var root = new TempWebRoot();
        using var factory = WithGuard(root.Path);
        _ = factory.Server;
        Configure(factory);

        var context = await SendAsync(factory, "POST", $"/api/devices/{id}/override", "application/json", """{"mode":"force_on"}""");

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Empty(factory.Services.GetRequiredService<IDeviceOverrideView>().Current.Devices);
    }

    [Fact]
    public async Task Override_without_a_valid_configuration_is_409()
    {
        using var root = new TempWebRoot();
        using var factory = WithGuard(root.Path);
        _ = factory.Server;

        var context = await SendAsync(factory, "POST", "/api/devices/heater_l1/override", "application/json", """{"mode":"force_off"}""");

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        Assert.Empty(factory.Services.GetRequiredService<IDeviceOverrideView>().Current.Devices);
    }

    [Fact]
    public async Task Override_post_answers_with_the_evaluated_status_and_is_recorded()
    {
        // The guard runner runs in the host: the answer comes after its step, which recorded the
        // override (manual_override) and shows it in the device's dispatch state.
        using var root = new TempWebRoot();
        using var factory = WithGuard(root.Path);
        _ = factory.Server;
        Configure(factory);

        var context = await SendAsync(factory, "POST", "/api/devices/heater_l2/override", "application/json", """{"mode":"force_off"}""");
        using var json = JsonDocument.Parse(context.Response.Body);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var heater = json.RootElement.GetProperty("devices").EnumerateArray().Single(d => d.GetProperty("id").GetString() == "heater_l2");
        Assert.Equal(("force_off", "api"), (heater.GetProperty("override").GetProperty("mode").GetString(), heater.GetProperty("override").GetProperty("source").GetString()));
        var recorded = factory.Services.GetRequiredService<IDecisionRecorder>().Query(new DecisionQuery(DeviceName.Parse("heater_l2"), null, null, 50, null)).Items;
        Assert.Contains(recorded, d => d.Action == DecisionAction.OverrideSet && d.Reason == ReasonCode.ManualOverride && d.Origin == DecisionOrigin.Override);

        using var decisions = JsonDocument.Parse((await SendAsync(factory, "GET", "/api/decisions?device=heater_l2")).Response.Body);
        var line = decisions.RootElement.GetProperty("items").EnumerateArray().First(d => d.GetProperty("action").GetString() == "override_set");
        Assert.Equal(("override", "force_off"), (line.GetProperty("origin").GetString(), line.GetProperty("inputs").GetProperty("dispatch").GetProperty("override").GetString()));
    }

    [Fact]
    public async Task Override_set_through_the_api_survives_a_restart_of_the_host()
    {
        // SAFE-04 across a real restart, through the production wiring (GuardService attaches the
        // overrides to state.json and restores them before the first step): a forced-off heater is
        // written to state.json by the clean stop and is forced off again, as restored, in the next
        // run, before anyone sets it again.
        using var root = new TempWebRoot();
        using (var first = WithGuard(root.Path))
        {
            _ = first.Server;
            Configure(first);
            var context = await SendAsync(first, "POST", "/api/devices/heater_l2/override", "application/json", """{"mode":"force_off"}""");
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        }

        using (var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root.Path, "data", "state.json"), TestContext.Current.CancellationToken)))
        {
            Assert.Equal("force_off", saved.RootElement.GetProperty("overrides").GetProperty("heater_l2").GetProperty("mode").GetString());
        }

        using var second = WithGuard(root.Path);
        _ = second.Server;
        var restored = second.Services.GetRequiredService<IDeviceOverrideView>().Current.For(DeviceName.Parse("heater_l2"));
        Assert.Equal((OverrideMode.ForceOff, OverrideSource.Restored), (restored.Mode, restored.Source!.Value));

        var source = second.Services.GetRequiredService<IDispatchStatusSource>();
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Published += (_, status) =>
        {
            if (status.Devices.Any(d => d.Id.Value == "heater_l2"))
            {
                published.TrySetResult();
            }
        };
        Configure(second);
        await published.Task.Soon();

        using var json = JsonDocument.Parse((await SendAsync(second, "GET", "/api/dispatch")).Response.Body);
        var heater = json.RootElement.GetProperty("devices").EnumerateArray().Single(d => d.GetProperty("id").GetString() == "heater_l2");
        Assert.Equal(("force_off", "restored"), (heater.GetProperty("override").GetProperty("mode").GetString(), heater.GetProperty("override").GetProperty("source").GetString()));
    }

    /// <summary>The reference configuration through the real store, published as the host's current snapshot.</summary>
    private static void Configure(SwitchboardHostFactory factory)
    {
        var harness = new GuardRunnerHarness(TestDevices.Document());
        harness.PushReference(l1: 8);
        factory.Services.GetRequiredService<SignalSnapshotHolder>().Publish(harness.Holder.Current);
    }

    /// <summary>A status with every nested object present: a deferred, settling, overridden heater with history, and a charger with a draw.</summary>
    internal static DispatchStatus Full()
    {
        var mode = ModeState.Resolve(OperatingMode.Active, controlAllowed: false, configurationValid: true, stateConfirmed: true);
        var group = GroupName.Parse("carport");
        return new DispatchStatus(
            At,
            mode,
            false,
            [new DispatchPhaseStatus(Phase.L1, new Amperes(9.1), new Amperes(6.6), true, null, new Watts(850), new Watts(400), 1, [new DispatchGroupEnvelope(group, new Amperes(4), Amperes.Zero, true)])],
            [
                new DispatchDeviceStatus(
                    DeviceName.Parse("heater_l1"), DeviceType.OnOff, [Phase.L1], GroupName.Parse("cwu_heaters"), null,
                    DeviceLevel.Relay(true), DeviceLevel.Relay(false), true, false,
                    ReasonCode.HeadroomAvailable, DecisionOrigin.Dispatch, 3, null,
                    TimeSpan.Zero, TimeSpan.FromSeconds(42),
                    new DeferredStatus(DeviceLevel.Relay(true), ReasonCode.HeadroomAvailable, ReasonCode.LockoutMinOff, At - TimeSpan.FromSeconds(30), At + TimeSpan.FromSeconds(42)),
                    SettlingStatus.Open,
                    new UnresponsiveStatus(At - TimeSpan.FromMinutes(5), At + TimeSpan.FromMinutes(25)),
                    3,
                    new DeviceOverride(OverrideMode.ForceOn, At - TimeSpan.FromMinutes(1), OverrideSource.Api),
                    null,
                    [
                        new VerificationRecord(At - TimeSpan.FromMinutes(6), HistoryKind.Command, 1500, 0, MeasureUnit.Watts, Verdict.Mismatch)
                        {
                            CommandedAt = At - TimeSpan.FromMinutes(6.5),
                            From = DeviceLevel.Relay(false),
                            To = DeviceLevel.Relay(true),
                        },
                        new VerificationRecord(At - TimeSpan.FromMinutes(2), HistoryKind.Observed, 1500, 1460, MeasureUnit.Watts, Verdict.Match),
                    ])
                {
                    Start = StartPolicy.RestoreOnly,
                    Baseline = new Switchboard.Application.Effector.StartBaseline(DeviceLevel.Relay(true), DeviceLevel.Relay(false), At - TimeSpan.FromMinutes(4), true),
                    UnconfirmedSince = At - TimeSpan.FromSeconds(40),
                    SelfStarted = At - TimeSpan.FromMinutes(20),
                },
                new DispatchDeviceStatus(
                    DeviceName.Parse("ev_charger"), DeviceType.Modulating, [Phase.L1], null, group,
                    DeviceLevel.At(new Amperes(13)), DeviceLevel.At(new Amperes(13)), true, true,
                    null, null, 1, ReasonCode.AwaitingConfirmation,
                    TimeSpan.Zero, TimeSpan.Zero,
                    null,
                    SettlingStatus.None,
                    null,
                    0,
                    DeviceOverride.Auto,
                    new DrawStatus(new Amperes(12.4), new Watts(2860), true),
                    [])
                {
                    Start = StartPolicy.Headroom,
                },
            ]);
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

    private static Task<HttpContext> SendAsync(SwitchboardHostFactory factory, string method, string pathAndQuery, string? contentType = null, string? body = null) =>
        factory.Server.SendAsync(context =>
        {
            context.Request.Method = method;
            var query = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
            context.Request.Path = query < 0 ? pathAndQuery : pathAndQuery[..query];
            context.Request.QueryString = query < 0 ? QueryString.Empty : new QueryString(pathAndQuery[query..]);
            context.Connection.RemoteIpAddress = IPAddress.Parse(Supervisor);
            if (contentType is not null)
            {
                context.Request.ContentType = contentType;
                context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body ?? string.Empty));
            }
        });
}
