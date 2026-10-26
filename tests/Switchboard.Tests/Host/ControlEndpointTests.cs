using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Switchboard.Application.State;
using Switchboard.Host.Hosting;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// MODE-02 over HTTP: the control switch is set only with a JSON body (a cross-site form cannot send
/// one without a preflight), is written to state.json at once, and the answer reflects the mode in
/// force. An untrusted state.json locks control off with the kill switch on until the user
/// confirms; the mode endpoint is read-only since 0.7.0.
/// </summary>
public sealed class ControlEndpointTests
{
    [Fact]
    public async Task A_fresh_install_reports_control_off_and_why_it_does_not_command()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);

        var control = Json(await Send(factory, "GET", "/api/control"));

        Assert.Equal((false, "default", false), (control.GetProperty("controlAllowed").GetBoolean(), control.GetProperty("source").GetString(), control.GetProperty("locked").GetBoolean()));
        var missing = control.GetProperty("mode").GetProperty("missing").EnumerateArray().Select(m => m.GetString()).ToList();
        Assert.Contains("option_shadow", missing);
        Assert.Contains("control_off", missing);
        Assert.Equal("shadow", control.GetProperty("mode").GetProperty("effective").GetString());
    }

    [Fact]
    public async Task Control_is_allowed_with_a_json_post_and_written_to_state_json_at_once()
    {
        using var root = new TempWebRoot();
        using var factory = WithGuard(root, active: true);
        _ = factory.Server;
        var coordinator = factory.Services.GetRequiredService<RuntimeStateCoordinator>();

        // Subscribed before each change: the writer may finish before the request returns.
        using var allowedSaved = new SaveWatch(coordinator, allowed: true);
        var allowed = await Send(factory, "POST", "/api/control", "application/json", """{"allowed":true}""");

        Assert.Equal(StatusCodes.Status200OK, allowed.Response.StatusCode);
        var body = Json(allowed);
        Assert.Equal((true, "api"), (body.GetProperty("controlAllowed").GetBoolean(), body.GetProperty("source").GetString()));
        Assert.Equal("active", body.GetProperty("mode").GetProperty("ceiling").GetString());
        await StateShowsAsync(root, allowedSaved, allowed: true);

        using var offSaved = new SaveWatch(coordinator, allowed: false);
        var off = Json(await Send(factory, "POST", "/api/control", "application/json", """{"allowed":false}"""));
        Assert.False(off.GetProperty("controlAllowed").GetBoolean());
        await StateShowsAsync(root, offSaved, allowed: false);
    }

    [Theory]
    [InlineData("text/plain", """{"allowed":true}""")]
    [InlineData("application/x-www-form-urlencoded", "allowed=true")]
    [InlineData("application/json", """{"allowed":"yes"}""")]
    [InlineData("application/json", """{"allowed":true,"mode":"active"}""")]
    [InlineData("application/json", """{}""")]
    public async Task Anything_but_the_exact_json_body_is_refused_and_changes_nothing(string contentType, string body)
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path, new Dictionary<string, string?> { ["SWITCHBOARD_MODE"] = "active" });

        var refused = await Send(factory, "POST", "/api/control", contentType, body);

        Assert.Equal(StatusCodes.Status400BadRequest, refused.Response.StatusCode);
        Assert.False(Json(await Send(factory, "GET", "/api/control")).GetProperty("controlAllowed").GetBoolean());
    }

    [Fact]
    public async Task The_mode_endpoint_is_read_only()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path, new Dictionary<string, string?> { ["SWITCHBOARD_MODE"] = "active" });

        var posted = await Send(factory, "POST", "/api/mode", "application/json", """{"uiChoice":"active"}""");
        var mode = Json(await Send(factory, "GET", "/api/mode"));

        Assert.Contains(posted.Response.StatusCode, new[] { StatusCodes.Status404NotFound, StatusCodes.Status405MethodNotAllowed });
        Assert.Equal(
            ["ceiling", "controlAllowed", "configurationValid", "stateConfirmed", "effective", "missing", "note"],
            mode.EnumerateObject().Select(p => p.Name));
        Assert.False(mode.GetProperty("controlAllowed").GetBoolean());
    }

    [Fact]
    public async Task An_untrusted_state_file_locks_control_off_with_the_kill_switch_on_until_confirmed()
    {
        using var root = new TempWebRoot();
        Directory.CreateDirectory(Path.Combine(root.Path, "data"));
        File.WriteAllText(Path.Combine(root.Path, "data", "state.json"), "{ not json");
        using var factory = WithGuard(root, active: true);
        _ = factory.Server;

        var control = Json(await Send(factory, "GET", "/api/control"));
        Assert.Equal((true, "state_file_corrupt", false), (control.GetProperty("locked").GetBoolean(), control.GetProperty("lockReason").GetString(), control.GetProperty("controlAllowed").GetBoolean()));
        var guard = Json(await Send(factory, "GET", "/api/guard")).GetProperty("killSwitch");
        Assert.Equal((true, true, "corrupt_state"), (guard.GetProperty("engaged").GetBoolean(), guard.GetProperty("locked").GetBoolean(), guard.GetProperty("source").GetString()));

        var refused = await Send(factory, "POST", "/api/control", "application/json", """{"allowed":true}""");
        Assert.Equal((StatusCodes.Status409Conflict, "state_unconfirmed"), (refused.Response.StatusCode, Json(refused).GetProperty("code").GetString()));
        var release = await Send(factory, "POST", "/api/guard/kill-switch", "application/json", """{"engaged":false}""");
        Assert.Equal(StatusCodes.Status409Conflict, release.Response.StatusCode);

        var confirmed = await Send(factory, "POST", "/api/control/confirm", "application/json", """{"confirm":true}""");
        Assert.Equal(StatusCodes.Status200OK, confirmed.Response.StatusCode);
        var confirmedBody = Json(confirmed);
        Assert.Equal((false, false), (confirmedBody.GetProperty("locked").GetBoolean(), confirmedBody.GetProperty("controlAllowed").GetBoolean()));
        Assert.False(Json(await Send(factory, "GET", "/api/guard")).GetProperty("killSwitch").GetProperty("engaged").GetBoolean());

        var again = await Send(factory, "POST", "/api/control/confirm", "application/json", """{"confirm":true}""");
        Assert.Equal((StatusCodes.Status409Conflict, "not_locked"), (again.Response.StatusCode, Json(again).GetProperty("code").GetString()));
    }

    [Fact]
    public async Task The_development_sink_exists_only_in_development_replay()
    {
        using var live = new TempWebRoot();
        using var addOn = new SwitchboardHostFactory(live.Path);
        Assert.Equal(StatusCodes.Status404NotFound, (await Send(addOn, "GET", "/api/dev/sink")).Response.StatusCode);

        using var root = new TempWebRoot();
        using var replay = SetupEndpointTests.ReplayHost(root, new SteppableTime(SetupEndpointTests.At(7, 39)));
        var sink = await Send(replay, "GET", "/api/dev/sink");
        Assert.Equal(StatusCodes.Status200OK, sink.Response.StatusCode);
        Assert.Equal(JsonValueKind.Array, Json(sink).ValueKind);
    }

    /// <summary>
    /// The writer saves a control change without waiting for the save interval, on its own thread.
    /// The save is awaited as an event (<see cref="RuntimeStateCoordinator.Saved"/>, hang-guarded),
    /// not by polling the file: a reader opening state.json while the writer replaces it made the
    /// writer's atomic replace fail on Windows, and its retry comes only after WriteRetryInterval,
    /// which is longer than any polling bound (the flaky failure of 0.7.0 to 0.9.0). Once the save is
    /// reported, the file is read exactly once.
    /// </summary>
    private static async Task StateShowsAsync(TempWebRoot root, SaveWatch saved, bool allowed)
    {
        await saved.Saved.Soon();

        var path = Path.Combine(root.Path, "data", "state.json");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                // Shared for writing and deletion, so a later write of the same state is never held up.
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
                using var reader = new StreamReader(stream);
                using var state = JsonDocument.Parse(await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
                Assert.Equal(allowed, state.RootElement.GetProperty("control").GetProperty("allowed").GetBoolean());
                return;
            }
            catch (IOException) when (attempt < 100)
            {
                // A later save is replacing the file right now (atomic rename): read it again at once.
                await Task.Yield();
            }
        }
    }

    /// <summary>Completes when a state with the control switch at <c>allowed</c> was written; subscribe before the change is made.</summary>
    private sealed class SaveWatch : IDisposable
    {
        private readonly RuntimeStateCoordinator _state;
        private readonly bool _allowed;
        private readonly TaskCompletionSource _saved = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SaveWatch(RuntimeStateCoordinator state, bool allowed)
        {
            _state = state;
            _allowed = allowed;
            _state.Saved += OnSaved;
        }

        public Task Saved => _saved.Task;

        public void Dispose() => _state.Saved -= OnSaved;

        private void OnSaved(object? sender, RuntimeState written)
        {
            if (written.Control.Allowed == _allowed)
            {
                _saved.TrySetResult();
            }
        }
    }

    /// <summary>The host with the guard running (GuardService restores or locks the control switch and writes state.json), nothing else in the background.</summary>
    private static SwitchboardHostFactory WithGuard(TempWebRoot root, bool active) => new(
        root.Path,
        active ? new Dictionary<string, string?> { ["SWITCHBOARD_MODE"] = "active" } : null,
        replaceLinks: services =>
        {
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType != typeof(GuardService)).ToList())
            {
                services.Remove(descriptor);
            }
        });

    private static Task<HttpContext> Send(SwitchboardHostFactory factory, string method, string path, string? contentType = null, string? body = null) =>
        SetupEndpointTests.SendAsync(factory, method, path, contentType, body);

    private static JsonElement Json(HttpContext context) => SetupEndpointTests.Json(context);
}
