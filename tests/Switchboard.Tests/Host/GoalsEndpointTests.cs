using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Switchboard.Application.Goals;
using Switchboard.Application.Planning;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Goals;
using Switchboard.Host.Hosting;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// POST api/goals and DELETE api/goals/{device} (PLAN-19): the "want now" action. Why it matters: it is the one place a person can ask the add-on to plan
/// energy for a device, so it must be reachable only through ingress, must take only a strict JSON body (a cross-site form cannot send one without a CORS
/// preflight, which the add-on never answers), must refuse anything that is not a declared device, must change only the add-on's own state (no entity), and
/// must ask the planner to look again when the goal changed (and only then).
/// </summary>
public sealed class GoalsEndpointTests
{
    private const string Supervisor = "172.30.32.2";

    private const string Tariff = "economics:\n  currency: PLN\n  tariff:\n    default_zone: offpeak\n    zones:\n      - id: offpeak\n        price: 0.60\n        when: []\n  settlement:\n    model: hourly_netting\n    export_price: 0.35\n";

    private const string Heater = "  - id: heater_l1\n    type: onoff\n    actuator: switch.heater_1\n    phase: L1\n    rated_current_a: 6.6\n    rated_power_w: 1500\n    value_per_kwh: 0.35\n";

    private const string Car = "  - id: ev_charger\n    type: modulating\n    actuator: number.charger_current\n    phase: L1\n    min: 6\n    max: 16\n    step: 1\n    value_per_kwh: 2.03\n";

    private const string SocGoal = "    goal:\n      kind: soc\n      capacity_kwh: 18\n      soc: { entity: sensor.car_battery_level, max_age: 30m }\n      target: { value: 90 }\n";

    private sealed class RecordingTrigger : IPlanTrigger
    {
        public int Wakes { get; private set; }

        public void Wake() => Wakes++;
    }

    private static string Deadline(double hours) => TimeProvider.System.GetUtcNow().AddHours(hours).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static string Body(string device = "ev_charger", string kind = "energy", string target = "8", string? deadline = null, string hard = "true") =>
        $$"""{"device":"{{device}}","kind":"{{kind}}","target":{{target}},"deadline":"{{deadline ?? Deadline(5)}}","hard":{{hard}}}""";

    private static SwitchboardHostFactory Host(string root, RecordingTrigger trigger, bool guard = false) => new(root, replaceLinks: services =>
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService) && (!guard || d.ImplementationType != typeof(GuardService))).ToList())
        {
            services.Remove(descriptor);
        }

        services.AddSingleton<IPlanTrigger>(trigger);
    });

    private static void Configure(SwitchboardHostFactory factory, string carGoal = "")
    {
        var harness = new GuardRunnerHarness(TestDevices.Document(Heater + Car + carGoal, extra: Tariff));
        factory.Services.GetRequiredService<SignalSnapshotHolder>().Publish(harness.Holder.Current with { Configuration = harness.Configuration });
    }

    [Fact]
    public async Task A_valid_post_stores_the_goal_in_the_add_ons_state_and_asks_the_planner_to_look_again()
    {
        using var root = new TempWebRoot();
        var trigger = new RecordingTrigger();
        using var factory = Host(root.Path, trigger);
        _ = factory.Server;
        Configure(factory);

        var context = await Send(factory, "POST", "/api/goals", "application/json", Body(target: "8.5"));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var json = Json(context);
        Assert.Equal(["device", "kind", "target", "deadline", "hard", "since", "deliveredKwh", "created"], json.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("ev_charger", "energy", 8.5, true, 0.0, true), (json.GetProperty("device").GetString(), json.GetProperty("kind").GetString(), json.GetProperty("target").GetDouble(), json.GetProperty("hard").GetBoolean(), json.GetProperty("deliveredKwh").GetDouble(), json.GetProperty("created").GetBoolean()));
        var stored = factory.Services.GetRequiredService<IManualGoalControl>().Current.Goals[DeviceName.Parse("ev_charger")];
        Assert.Equal((GoalKind.Energy, 8.5, true), (stored.Kind, stored.Target, stored.Hard));
        Assert.Equal(1, trigger.Wakes);
    }

    [Fact]
    public async Task Asking_for_the_same_goal_again_is_not_created_and_does_not_wake_the_planner_a_second_time()
    {
        using var root = new TempWebRoot();
        var trigger = new RecordingTrigger();
        using var factory = Host(root.Path, trigger);
        _ = factory.Server;
        Configure(factory);
        var body = Body();

        await Send(factory, "POST", "/api/goals", "application/json", body);
        var again = await Send(factory, "POST", "/api/goals", "application/json", body);

        Assert.Equal(StatusCodes.Status200OK, again.Response.StatusCode);
        Assert.False(Json(again).GetProperty("created").GetBoolean());
        Assert.Equal(1, trigger.Wakes);
    }

    [Theory]
    [InlineData("text/plain", """{"device":"ev_charger","kind":"energy","target":8,"deadline":"2099-01-01T00:00:00Z","hard":true}""")]
    [InlineData("application/x-www-form-urlencoded", "device=ev_charger&kind=energy&target=8")]
    [InlineData("application/json", "{")]
    [InlineData("application/json", "[]")]
    [InlineData("application/json", "{}")]
    [InlineData("application/json", """{"device":"ev_charger","kind":"energy","target":8,"deadline":"2099-01-01T00:00:00Z"}""")]
    [InlineData("application/json", """{"device":"ev_charger","kind":"energy","target":8,"deadline":"2099-01-01T00:00:00Z","hard":true,"entity":"switch.x"}""")]
    [InlineData("application/json", """{"device":"ev_charger","kind":"power","target":8,"deadline":"2099-01-01T00:00:00Z","hard":true}""")]
    [InlineData("application/json", """{"device":"ev_charger","kind":"energy","target":"8","deadline":"2099-01-01T00:00:00Z","hard":true}""")]
    [InlineData("application/json", """{"device":"ev_charger","kind":"energy","target":8,"deadline":"2099-01-01T00:00:00Z","hard":"yes"}""")]
    [InlineData("application/json", """{"device":5,"kind":"energy","target":8,"deadline":"2099-01-01T00:00:00Z","hard":true}""")]
    [InlineData("application/json", """{"device":"ev_charger","kind":"energy","target":8,"deadline":"2099-01-01T00:00:00","hard":true}""")]
    [InlineData("application/json", """{"device":"ev_charger","kind":"energy","target":8,"deadline":"tomorrow","hard":true}""")]
    [InlineData("application/json", """{"device":"ev_charger","kind":"energy","target":8,"deadline":1,"hard":true}""")]
    public async Task Anything_but_the_exact_json_body_is_refused_and_changes_nothing(string contentType, string body)
    {
        // Why: strict parsing and the JSON requirement are the protection of a POST that changes state (a bare local time would be read in a zone the add-on
        // does not have; an extra key could smuggle an entity).
        using var root = new TempWebRoot();
        var trigger = new RecordingTrigger();
        using var factory = Host(root.Path, trigger);
        _ = factory.Server;
        Configure(factory);

        var refused = await Send(factory, "POST", "/api/goals", contentType, body);

        Assert.Equal(StatusCodes.Status400BadRequest, refused.Response.StatusCode);
        Assert.Equal("bad_request", Json(refused).GetProperty("code").GetString());
        Assert.Empty(factory.Services.GetRequiredService<IManualGoalControl>().Current.Goals);
        Assert.Equal(0, trigger.Wakes);
    }

    [Fact]
    public async Task A_body_larger_than_the_bound_is_refused_without_being_parsed()
    {
        using var root = new TempWebRoot();
        var trigger = new RecordingTrigger();
        using var factory = Host(root.Path, trigger);
        _ = factory.Server;
        Configure(factory);
        var padded = Body(device: "ev_charger") .TrimEnd('}') + ",\"pad\":\"" + new string('x', 2000) + "\"}";

        var refused = await Send(factory, "POST", "/api/goals", "application/json", padded);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, refused.Response.StatusCode);
        Assert.Empty(factory.Services.GetRequiredService<IManualGoalControl>().Current.Goals);
    }

    [Theory]
    [InlineData("garage_door")]
    [InlineData("switch.heater_1")]
    [InlineData("Heater_L1")]
    public async Task A_device_the_configuration_does_not_declare_is_404_and_nothing_is_stored(string device)
    {
        // Why: hard rule: only a declared device is ever addressed, and an entity id never is.
        using var root = new TempWebRoot();
        var trigger = new RecordingTrigger();
        using var factory = Host(root.Path, trigger);
        _ = factory.Server;
        Configure(factory);

        var refused = await Send(factory, "POST", "/api/goals", "application/json", Body(device: device));

        Assert.Equal(StatusCodes.Status404NotFound, refused.Response.StatusCode);
        Assert.Equal("unknown_device", Json(refused).GetProperty("code").GetString());
        Assert.Empty(factory.Services.GetRequiredService<IManualGoalControl>().Current.Goals);
        Assert.Equal(0, trigger.Wakes);
    }

    [Fact]
    public async Task Without_a_valid_configuration_the_post_is_409()
    {
        using var root = new TempWebRoot();
        var trigger = new RecordingTrigger();
        using var factory = Host(root.Path, trigger);
        _ = factory.Server;

        var refused = await Send(factory, "POST", "/api/goals", "application/json", Body());

        Assert.Equal(StatusCodes.Status409Conflict, refused.Response.StatusCode);
        Assert.Equal("no_configuration", Json(refused).GetProperty("code").GetString());
        Assert.Empty(factory.Services.GetRequiredService<IManualGoalControl>().Current.Goals);
    }

    [Theory]
    [InlineData("energy", "0", 5, "target_out_of_range")]
    [InlineData("energy", "-2", 5, "target_out_of_range")]
    [InlineData("energy", "5000000", 5, "target_out_of_range")]
    [InlineData("energy", "8", -1, "deadline_passed")]
    [InlineData("energy", "8", 49, "deadline_beyond_horizon")]
    [InlineData("soc", "80", 5, "soc_not_configured")]
    public async Task A_goal_that_cannot_be_planned_is_422_with_its_reason(string kind, string target, double hours, string code)
    {
        // Why: the screen words each reason; a goal the plan cannot see or a SoC target with no SoC signal must say so rather than silently store.
        using var root = new TempWebRoot();
        var trigger = new RecordingTrigger();
        using var factory = Host(root.Path, trigger);
        _ = factory.Server;
        Configure(factory);

        var refused = await Send(factory, "POST", "/api/goals", "application/json", Body(kind: kind, target: target, deadline: Deadline(hours)));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, refused.Response.StatusCode);
        Assert.Equal(code, Json(refused).GetProperty("code").GetString());
        Assert.Empty(factory.Services.GetRequiredService<IManualGoalControl>().Current.Goals);
        Assert.Equal(0, trigger.Wakes);
    }

    [Fact]
    public async Task A_soc_goal_is_accepted_for_a_device_whose_goal_reads_a_soc()
    {
        using var root = new TempWebRoot();
        using var factory = Host(root.Path, new RecordingTrigger());
        _ = factory.Server;
        Configure(factory, SocGoal);

        var context = await Send(factory, "POST", "/api/goals", "application/json", Body(kind: "soc", target: "80"));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("soc", Json(context).GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Delete_cancels_the_goal_and_asks_the_planner_to_look_again()
    {
        using var root = new TempWebRoot();
        var trigger = new RecordingTrigger();
        using var factory = Host(root.Path, trigger);
        _ = factory.Server;
        Configure(factory);
        await Send(factory, "POST", "/api/goals", "application/json", Body());

        var deleted = await Send(factory, "DELETE", "/api/goals/ev_charger");

        Assert.Equal(StatusCodes.Status200OK, deleted.Response.StatusCode);
        Assert.Empty(factory.Services.GetRequiredService<IManualGoalControl>().Current.Goals);
        Assert.Equal(2, trigger.Wakes);

        var again = await Send(factory, "DELETE", "/api/goals/ev_charger");
        Assert.Equal(StatusCodes.Status404NotFound, again.Response.StatusCode);
        Assert.Equal("no_manual_goal", Json(again).GetProperty("code").GetString());
        Assert.Equal(2, trigger.Wakes);
    }

    [Fact]
    public async Task Delete_of_a_name_that_is_not_a_device_is_404()
    {
        using var root = new TempWebRoot();
        using var factory = Host(root.Path, new RecordingTrigger());
        _ = factory.Server;

        var refused = await Send(factory, "DELETE", "/api/goals/switch.heater_1");

        Assert.Equal(StatusCodes.Status404NotFound, refused.Response.StatusCode);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task Only_ingress_can_reach_the_goal_endpoints(string method)
    {
        // Why: no new port and no write beyond ingress (hard rule); a peer that skipped Home Assistant's authentication reaches nothing.
        using var root = new TempWebRoot();
        var trigger = new RecordingTrigger();
        using var factory = Host(root.Path, trigger);
        _ = factory.Server;
        Configure(factory);
        factory.Services.GetRequiredService<ManualGoalStore>().Begin(DeviceName.Parse("ev_charger"), GoalKind.Energy, 4, TimeProvider.System.GetUtcNow().AddHours(3), hard: false);
        var path = method == "POST" ? "/api/goals" : "/api/goals/ev_charger";

        var outsider = await Send(factory, method, path, "application/json", Body(target: "9"), "10.1.2.3");

        Assert.NotEqual(StatusCodes.Status200OK, outsider.Response.StatusCode);
        Assert.Equal(4.0, factory.Services.GetRequiredService<IManualGoalControl>().Current.Goals[DeviceName.Parse("ev_charger")].Target);
        Assert.Equal(0, trigger.Wakes);
    }

    [Fact]
    public async Task The_goal_shows_in_api_plan_as_manual_and_is_the_only_thing_the_post_changed()
    {
        // Why: PLAN-19 end to end through the real wiring: the store is a goal source the plan endpoint reads, with origin manual.
        using var root = new TempWebRoot();
        using var factory = Host(root.Path, new RecordingTrigger());
        _ = factory.Server;
        Configure(factory);
        await Send(factory, "POST", "/api/goals", "application/json", Body(target: "6"));

        var plan = Json(await Send(factory, "GET", "/api/plan"));

        var device = plan.GetProperty("devices").EnumerateArray().Single(d => d.GetProperty("device").GetString() == "ev_charger");
        var goal = device.GetProperty("goal");
        Assert.Equal(("manual", "energy", 6.0, true), (goal.GetProperty("source").GetString(), goal.GetProperty("kind").GetString(), goal.GetProperty("targetKwh").GetDouble(), goal.GetProperty("hard").GetBoolean()));
    }

    [Fact]
    public async Task A_manual_goal_survives_a_restart_of_the_host_through_state_json()
    {
        // Why: PLAN-19 through the production wiring (GuardService attaches the store to state.json and restores it before the first step): the goal is in
        // the file after the clean stop and in force again, with its deadline, in the next run.
        using var root = new TempWebRoot();
        var deadline = Deadline(6);
        using (var first = Host(root.Path, new RecordingTrigger(), guard: true))
        {
            _ = first.Server;
            Configure(first);
            Assert.Equal(StatusCodes.Status200OK, (await Send(first, "POST", "/api/goals", "application/json", Body(target: "7", deadline: deadline))).Response.StatusCode);
        }

        using (var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root.Path, "data", "state.json"), TestContext.Current.CancellationToken)))
        {
            Assert.Equal(7.0, saved.RootElement.GetProperty("manual_goals").GetProperty("ev_charger").GetProperty("target").GetDouble());
        }

        using var second = Host(root.Path, new RecordingTrigger(), guard: true);
        _ = second.Server;
        var restored = second.Services.GetRequiredService<IManualGoalControl>().Current.Goals[DeviceName.Parse("ev_charger")];
        Assert.Equal((7.0, DateTimeOffset.Parse(deadline, System.Globalization.CultureInfo.InvariantCulture)), (restored.Target, restored.Deadline));
    }

    private static JsonElement Json(HttpContext context) => SetupEndpointTests.Json(context);

    private static Task<HttpContext> Send(SwitchboardHostFactory factory, string method, string path, string? contentType = null, string? body = null, string from = Supervisor) =>
        factory.Server.SendAsync(context =>
        {
            context.Request.Method = method;
            context.Request.Path = path;
            context.Connection.RemoteIpAddress = IPAddress.Parse(from);
            if (contentType is not null)
            {
                context.Request.ContentType = contentType;
                context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body ?? string.Empty));
            }
        });
}
