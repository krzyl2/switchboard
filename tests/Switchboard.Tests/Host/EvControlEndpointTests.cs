using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Switchboard.Application.Ev;
using Switchboard.Domain.Ev;
using Switchboard.Host.Web;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// POST api/ev/mode, POST and DELETE api/ev/charge-now and the <c>manual</c> block of GET api/ev (EV-09, docs/EV.md S6). Why it matters: these are the user's
/// controls of a car charger, so like POST api/goals they are reachable only through ingress, take only a strict bounded JSON body (a cross-site form cannot send
/// one), answer each refusal with a closed code the UI words, and change only the add-on's own state: the endpoint talks to <see cref="IEvControl"/> and to nothing
/// that commands a device.
/// </summary>
public sealed class EvControlEndpointTests
{
    private const string Supervisor = "172.30.32.2";

    private sealed class FakeControl : IEvControl
    {
        public EvManual Manual { get; set; } = EvManual.Default;

        public bool? Configured { get; set; } = true;

        public double? DefaultChargeNowTargetPercent => 90;

        public EvControlResult Result { get; set; } = EvControlResult.Done;

        public List<string> Calls { get; } = [];

        public event EventHandler? Changed { add { } remove { } }

        public EvControlResult SetMode(EvMode mode)
        {
            Calls.Add($"mode {mode}");
            if (Result == EvControlResult.Done)
            {
                Manual = Manual with { Mode = mode };
            }

            return Result;
        }

        public EvControlResult StartChargeNow(double? targetPercent, TimeSpan? duration)
        {
            Calls.Add($"start {targetPercent?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"} {duration?.TotalMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}");
            if (Result == EvControlResult.Done)
            {
                var now = DateTimeOffset.Parse("2026-10-30T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
                Manual = Manual with { ChargeNow = new EvChargeNow(now, targetPercent ?? 90, now + (duration ?? TimeSpan.FromHours(12))) };
            }

            return Result;
        }

        public EvControlResult CancelChargeNow()
        {
            Calls.Add("cancel");
            return Result;
        }
    }

    private static SwitchboardHostFactory Host(string root, FakeControl control) => new(root, replaceLinks: services =>
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService)).ToList())
        {
            services.Remove(descriptor);
        }

        services.AddSingleton<IEvControl>(control);
    });

    [Fact]
    public async Task A_valid_mode_post_sets_the_mode_and_answers_with_it()
    {
        using var root = new TempWebRoot();
        var control = new FakeControl();
        using var factory = Host(root.Path, control);
        _ = factory.Server;

        var context = await Send(factory, "POST", "/api/ev/mode", "application/json", """{"mode":"pv_only"}""");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("pv_only", Json(context).GetProperty("mode").GetString());
        Assert.Equal(["mode PvOnly"], control.Calls);
    }

    [Theory]
    [InlineData("text/plain", """{"mode":"auto"}""")]
    [InlineData("application/x-www-form-urlencoded", "mode=auto")]
    [InlineData("application/json", "")]
    [InlineData("application/json", "{")]
    [InlineData("application/json", "[]")]
    [InlineData("application/json", "{}")]
    [InlineData("application/json", """{"mode":"off"}""")]
    [InlineData("application/json", """{"mode":"AUTO"}""")]
    [InlineData("application/json", """{"mode":1}""")]
    [InlineData("application/json", """{"mode":"auto","entity":"switch.x"}""")]
    public async Task Anything_but_exactly_one_known_mode_is_refused_and_sets_nothing(string contentType, string body)
    {
        // Why: strict parsing is the protection of a POST that changes state; "off" is deliberately not a mode (it is the device's own force_off override).
        using var root = new TempWebRoot();
        var control = new FakeControl();
        using var factory = Host(root.Path, control);
        _ = factory.Server;

        var refused = await Send(factory, "POST", "/api/ev/mode", contentType, body);

        Assert.Equal(StatusCodes.Status400BadRequest, refused.Response.StatusCode);
        Assert.Equal("bad_request", Json(refused).GetProperty("code").GetString());
        Assert.Empty(control.Calls);
    }

    [Fact]
    public async Task A_duration_too_long_for_a_timespan_is_still_positive_and_left_to_the_horizon_check()
    {
        // Why: "1e300 minutes" is not "not above zero"; it must reach the control as a long positive duration so the answer is deadline_beyond_horizon.
        using var root = new TempWebRoot();
        var control = new FakeControl { Result = EvControlResult.DeadlineBeyondHorizon };
        using var factory = Host(root.Path, control);
        _ = factory.Server;

        var refused = await Send(factory, "POST", "/api/ev/charge-now", "application/json", """{"durationMinutes":1e300}""");

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, refused.Response.StatusCode);
        Assert.Equal("deadline_beyond_horizon", Json(refused).GetProperty("code").GetString());
        var call = Assert.Single(control.Calls).Split(' ');
        Assert.True(double.Parse(call[2], System.Globalization.CultureInfo.InvariantCulture) > 0);
    }

    [Fact]
    public async Task Without_an_ev_section_the_mode_is_404()
    {
        using var root = new TempWebRoot();
        var control = new FakeControl { Result = EvControlResult.NotConfigured };
        using var factory = Host(root.Path, control);
        _ = factory.Server;

        var refused = await Send(factory, "POST", "/api/ev/mode", "application/json", """{"mode":"auto"}""");

        Assert.Equal(StatusCodes.Status404NotFound, refused.Response.StatusCode);
        Assert.Equal("not_configured", Json(refused).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Charge_now_passes_the_given_target_and_duration_and_answers_with_what_runs()
    {
        using var root = new TempWebRoot();
        var control = new FakeControl();
        using var factory = Host(root.Path, control);
        _ = factory.Server;

        var context = await Send(factory, "POST", "/api/ev/charge-now", "application/json", """{"targetPct":80,"durationMinutes":120}""");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var json = Json(context);
        Assert.Equal(["active", "targetPct", "until"], json.EnumerateObject().Select(p => p.Name));
        Assert.Equal((true, 80.0), (json.GetProperty("active").GetBoolean(), json.GetProperty("targetPct").GetDouble()));
        Assert.Equal(["start 80 120"], control.Calls);
    }

    [Fact]
    public async Task Charge_now_with_an_empty_object_takes_the_configured_values()
    {
        using var root = new TempWebRoot();
        var control = new FakeControl();
        using var factory = Host(root.Path, control);
        _ = factory.Server;

        var context = await Send(factory, "POST", "/api/ev/charge-now", "application/json", "{}");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(["start - -"], control.Calls);
    }

    [Theory]
    [InlineData("text/plain", "{}")]
    [InlineData("application/json", "")]
    [InlineData("application/json", "[]")]
    [InlineData("application/json", """{"targetPct":"80"}""")]
    [InlineData("application/json", """{"targetPct":80,"targetPct":70}""")]
    [InlineData("application/json", """{"target":80}""")]
    [InlineData("application/json", """{"durationMinutes":null}""")]
    [InlineData("application/json", """{"targetPct":80,"entity":"switch.x"}""")]
    public async Task Charge_now_refuses_anything_but_the_two_optional_numbers_and_starts_nothing(string contentType, string body)
    {
        using var root = new TempWebRoot();
        var control = new FakeControl();
        using var factory = Host(root.Path, control);
        _ = factory.Server;

        var refused = await Send(factory, "POST", "/api/ev/charge-now", contentType, body);

        Assert.Equal(StatusCodes.Status400BadRequest, refused.Response.StatusCode);
        Assert.Empty(control.Calls);
    }

    [Theory]
    [InlineData(EvControlResult.NotConfigured, 404, "not_configured")]
    [InlineData(EvControlResult.NeedsFullDispatch, 409, "needs_full_dispatch")]
    [InlineData(EvControlResult.NotPlugged, 409, "not_plugged")]
    [InlineData(EvControlResult.SocUnknown, 409, "soc_unknown")]
    [InlineData(EvControlResult.AlreadyAtTarget, 409, "already_at_target")]
    [InlineData(EvControlResult.TargetOutOfRange, 422, "target_out_of_range")]
    [InlineData(EvControlResult.DurationOutOfRange, 422, "duration_out_of_range")]
    [InlineData(EvControlResult.DeadlineBeyondHorizon, 422, "deadline_beyond_horizon")]
    public async Task Each_refusal_of_charge_now_is_its_own_status_and_closed_code(EvControlResult result, int status, string code)
    {
        // Why: the UI words each code; 409 means "not now, and here is why" (charge now at narrow or off answers 409 with the force_on hint), 422 "this value", 404 "no module".
        using var root = new TempWebRoot();
        var control = new FakeControl { Result = result };
        using var factory = Host(root.Path, control);
        _ = factory.Server;

        var refused = await Send(factory, "POST", "/api/ev/charge-now", "application/json", "{}");

        var json = Json(refused);
        Assert.Equal(status, refused.Response.StatusCode);
        Assert.Equal(code, json.GetProperty("code").GetString());
        if (result == EvControlResult.NeedsFullDispatch)
        {
            Assert.Contains("force_on", json.GetProperty("message").GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Cancelling_ends_it_and_cancelling_nothing_is_404()
    {
        using var root = new TempWebRoot();
        var control = new FakeControl();
        using var factory = Host(root.Path, control);
        _ = factory.Server;

        var ended = await Send(factory, "DELETE", "/api/ev/charge-now");
        control.Result = EvControlResult.NoChargeNow;
        var none = await Send(factory, "DELETE", "/api/ev/charge-now");

        Assert.Equal(StatusCodes.Status200OK, ended.Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, none.Response.StatusCode);
        Assert.Equal("no_charge_now", Json(none).GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_controls_are_reachable_only_through_ingress()
    {
        // Why: no new port and no exposure beyond ingress; a request from anywhere but the supervisor is refused before it reaches the endpoint.
        using var root = new TempWebRoot();
        var control = new FakeControl();
        using var factory = Host(root.Path, control);
        _ = factory.Server;

        var refused = await Send(factory, "POST", "/api/ev/mode", "application/json", """{"mode":"pv_only"}""", from: "192.168.10.50");

        Assert.NotEqual(StatusCodes.Status200OK, refused.Response.StatusCode);
        Assert.Empty(control.Calls);
    }

    [Fact]
    public void The_manual_block_of_the_answer_says_the_mode_and_whether_a_charge_now_runs()
    {
        // Why: the UI contract (docs/EV.md "As built S6"): manual { mode, chargeNow { active, targetPct, until } }, read from memory at once; an ended charge-now
        // (past its expiry, not yet cleared by the module's next computation) is not active.
        var rig = new EvRig(planner: "planner:\n  dispatch: full\n");
        rig.PlugInCar(40);
        rig.Tick();
        var now = rig.Now;

        var idle = Json(EvEndpoint.Build(rig.H.Configuration, now, manual: EvManual.Default)).GetProperty("manual");
        var running = Json(EvEndpoint.Build(rig.H.Configuration, now, manual: new EvManual(EvMode.PvOnly, new EvChargeNow(now, 80, now.AddHours(2))))).GetProperty("manual");
        var lapsed = Json(EvEndpoint.Build(rig.H.Configuration, now.AddHours(3), manual: new EvManual(EvMode.FullCharge, new EvChargeNow(now, 80, now.AddHours(2))))).GetProperty("manual");

        Assert.Equal(("auto", false, JsonValueKind.Null), (idle.GetProperty("mode").GetString(), idle.GetProperty("chargeNow").GetProperty("active").GetBoolean(), idle.GetProperty("chargeNow").GetProperty("targetPct").ValueKind));
        Assert.Equal(("pv_only", true, 80.0), (running.GetProperty("mode").GetString(), running.GetProperty("chargeNow").GetProperty("active").GetBoolean(), running.GetProperty("chargeNow").GetProperty("targetPct").GetDouble()));
        Assert.Equal(("full_charge", false), (lapsed.GetProperty("mode").GetString(), lapsed.GetProperty("chargeNow").GetProperty("active").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, Json(EvEndpoint.Build(rig.H.Configuration, now)).GetProperty("manual").ValueKind);
    }

    private static JsonElement Json(EvResponse response) => JsonSerializer.SerializeToElement(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));

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
