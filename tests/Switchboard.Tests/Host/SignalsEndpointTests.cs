using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Switchboard.Application.Signals;
using Switchboard.Host.Composition;
using Switchboard.Host.Web;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Infrastructure.Mqtt;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// GET api/signals feeds the diagnostics page (roadmap Stage 1: "the diagnostics page shows correct
/// live values and ages"). Its shape is a contract with the SPA's strict parser.
/// </summary>
public sealed class SignalsEndpointTests
{
    private const string Supervisor = "172.30.32.2";
    private static readonly SignalSourceSelection Live = new(SignalSourceKind.HomeAssistant, null, 1);

    [Fact]
    public async Task Signals_endpoint_shape_matches_spa_contract()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        _ = factory.Server;
        factory.Services.GetRequiredService<SignalSnapshotHolder>().Publish(Diagnostics(out _));

        var context = await SendAsync(factory, "/api/signals");
        using var json = JsonDocument.Parse(context.Response.Body);
        var body = json.RootElement;

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(["generatedAt", "snapshotAt", "sequence", "source", "configuration", "connection", "phases", "signals", "rejections"], Names(body));
        Assert.Equal(["kind", "status"], Names(body.GetProperty("source")));
        Assert.Equal(["state", "path", "loadedAt", "errors", "warnings"], Names(body.GetProperty("configuration")));
        Assert.Equal(["topology", "breakerRatingA", "characteristic", "voltageV"], Names(body.GetProperty("connection")));
        var phase = body.GetProperty("phases")[0];
        Assert.Equal(
            ["phase", "protectionDataValid", "invalidReason", "currentA", "direction", "netImportW", "generationW", "loadW", "residualW", "residualAt", "balanceRejections", "reserve"],
            Names(phase));
        Assert.Equal(["configuredW", "minimumSafeW", "belowMinimum", "refreshS", "refreshDetected", "blindTimeS", "tierMultiple"], Names(phase.GetProperty("reserve")));
        var signal = body.GetProperty("signals").EnumerateArray().First(s => s.GetProperty("name").GetString() == "grid_current_l1");
        Assert.Equal(
            ["name", "entity", "kind", "role", "phase", "state", "detail", "value", "unit", "raw", "lastReported", "ageS", "maxAgeS", "refreshS", "refreshIntervals", "direction", "rejectedCount", "dependentDevices"],
            Names(signal));
        Assert.Equal(["state", "unit"], Names(signal.GetProperty("raw")));
        Assert.Equal(["source", "direction", "from"], Names(signal.GetProperty("direction")));
        Assert.Equal(["total", "balanceResidual", "invalidSample"], Names(body.GetProperty("rejections")));
    }

    [Fact]
    public void Values_use_snake_case_enums_internal_units_and_monospace_worthy_entity_ids()
    {
        var response = SignalsEndpoint.Build(Diagnostics(out var now), now, Live, TestConfigurations.Path);

        Assert.Equal(("home_assistant", "connected"), (response.Source.Kind, response.Source.Status));
        Assert.Equal(("three_phase", "B", 25.0, 230.0), (response.Connection!.Topology, response.Connection.Characteristic, response.Connection.BreakerRatingA, response.Connection.VoltageV));
        var current = response.Signals.Single(s => s.Name == "grid_current_l1");
        Assert.Equal(("sensor.meter_current_a", "current", "phase_current", "L1", "fresh", "none"), (current.Entity, current.Kind, current.Role, current.Phase, current.State, current.Detail));
        Assert.Equal((-9.0, "A"), ((double)current.Value!, current.Unit));
        Assert.Equal(new RawDto("9.0", "A"), current.Raw);
        Assert.Equal(new DirectionDto("signed_signal", "export", "grid_power_l1"), current.Direction);
        var unmapped = response.Signals.First(s => s.State == "unmapped");
        Assert.Equal((null, null, null), (unmapped.Name, unmapped.Entity, unmapped.Value));
        var l1 = response.Phases.Single(p => p.Phase == "L1");
        Assert.Equal((true, (string?)null, -9.0, "export"), (l1.ProtectionDataValid, l1.InvalidReason, l1.CurrentA, l1.Direction));
        Assert.Equal("current_awaiting", response.Phases.Single(p => p.Phase == "L2").InvalidReason);
    }

    [Fact]
    public void Ages_computed_from_last_reported_at_request_time()
    {
        var diagnostics = Diagnostics(out var now);

        var early = SignalsEndpoint.Build(diagnostics, now, Live, TestConfigurations.Path);
        var later = SignalsEndpoint.Build(diagnostics, now.AddSeconds(42), Live, TestConfigurations.Path);

        Assert.Equal(0, early.Signals.Single(s => s.Name == "grid_current_l1").AgeS);
        Assert.Equal(42, later.Signals.Single(s => s.Name == "grid_current_l1").AgeS);
        Assert.Equal(early.SnapshotAt, later.SnapshotAt);
    }

    [Fact]
    public void Invalid_configuration_reported_with_path_and_line()
    {
        var store = new SignalStore(new SignalStoreSettings(TimeSpan.FromSeconds(5)));
        var result = TestConfigurations.Parse(TestConfigurations.ThreePhase().Replace("max_age: 90s", "max_agee: 90s", StringComparison.Ordinal));
        var now = StoreHarness.Start;
        store.ApplyConfiguration(result, now);

        var response = SignalsEndpoint.Build(new SignalDiagnostics(store.Snapshot(now), result, [], []), now, Live, TestConfigurations.Path);

        Assert.Equal(("invalid", TestConfigurations.Path), (response.Configuration.State, response.Configuration.Path));
        var error = response.Configuration.Errors.First();
        Assert.Equal("unknown_key", error.Code);
        Assert.NotNull(error.Line);
        Assert.Empty(response.Signals);
        Assert.Null(response.Connection);
        Assert.All(response.Phases, p => Assert.Equal((false, "configuration_invalid"), (p.ProtectionDataValid, p.InvalidReason)));
    }

    [Fact]
    public void Before_the_first_configuration_the_page_says_missing_and_where_it_looks()
    {
        var store = new SignalStore(new SignalStoreSettings(TimeSpan.FromSeconds(5)));

        var response = SignalsEndpoint.Build(new SignalDiagnostics(store.Snapshot(StoreHarness.Start), null, [], []), StoreHarness.Start, Live, "/config/switchboard.yaml");

        Assert.Equal(("missing", "/config/switchboard.yaml"), (response.Configuration.State, response.Configuration.Path));
    }

    [Fact]
    public void State_signals_report_text_values()
    {
        var yaml = TestConfigurations.ThreePhase(extra: "  car_connected:\n    entity: binary_sensor.car\n    kind: state\n    max_age: 1h\n");
        var harness = new StoreHarness(yaml);
        harness.Push("binary_sensor.car", "on");

        var response = SignalsEndpoint.Build(new SignalDiagnostics(harness.Snapshot(), harness.Configuration, [], []), harness.Now, Live, TestConfigurations.Path);

        var car = response.Signals.Single(s => s.Name == "car_connected");
        Assert.Equal(("on", (string?)null), ((string)car.Value!, car.Unit));
    }

    [Fact]
    public async Task The_running_host_loads_the_configuration_file_and_reports_it()
    {
        using var root = new TempWebRoot();
        var config = Path.Combine(root.Path, "switchboard.yaml");
        await File.WriteAllTextAsync(config, TestConfigurations.Example, TestContext.Current.CancellationToken);
        var factory = new SwitchboardHostFactory(root.Path, new Dictionary<string, string?> { ["SWITCHBOARD_CONFIG_PATH"] = config }, services =>
        {
            services.RemoveAll<IWebSocketTransportFactory>();
            services.AddSingleton<IWebSocketTransportFactory>(new ScriptedTransportFactory());
            services.RemoveAll<IMqttTransport>();
            services.AddSingleton<IMqttTransport>(new FakeMqttTransport(new MqttTopicGuard("homeassistant", "switchboard")));
        });
        await using (factory)
        {
            var holder = factory.Services.GetRequiredService<SignalSnapshotHolder>();
            await WaitForAsync(holder, d => d.Configuration is not null);

            using var json = JsonDocument.Parse((await SendAsync(factory, "/api/signals")).Response.Body);

            Assert.Equal("valid", json.RootElement.GetProperty("configuration").GetProperty("state").GetString());
            // 13 mapped signals and the applied-state signals of the 4 example devices (Stage 2).
            Assert.Equal(17, json.RootElement.GetProperty("signals").EnumerateArray().Count(s => s.GetProperty("name").ValueKind == JsonValueKind.String));
            Assert.All(json.RootElement.GetProperty("phases").EnumerateArray(), p => Assert.Equal("source_disconnected", p.GetProperty("invalidReason").GetString()));
        }
    }

    internal static async Task WaitForAsync(SignalSnapshotHolder holder, Func<SignalDiagnostics, bool> condition)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(object? sender, Switchboard.Domain.Signals.ProtectionInputs inputs)
        {
            if (condition(holder.Current))
            {
                reached.TrySetResult();
            }
        }

        ((IProtectionInputSource)holder).Published += Check;
        Check(null, holder.Current.Snapshot.Protection);
        await reached.Task.Soon();
        ((IProtectionInputSource)holder).Published -= Check;
    }

    private static SignalDiagnostics Diagnostics(out DateTimeOffset now)
    {
        var harness = new StoreHarness(TestConfigurations.ThreePhase(inverterCurrents: false));
        harness.Push("sensor.meter_power_a", "2070");
        harness.Push("sensor.meter_current_a", "9.0");
        now = harness.Now;
        var configuration = harness.Configuration.Configuration!;
        return new SignalDiagnostics(
            harness.Snapshot(),
            harness.Configuration,
            configuration.Signals.ToDictionary(s => s.Name, s => s.Entity).ToImmutableDictionary(),
            []);
    }

    private static List<string> Names(JsonElement element) => [.. element.EnumerateObject().Select(p => p.Name)];

    private static Task<HttpContext> SendAsync(SwitchboardHostFactory factory, string path) =>
        factory.Server.SendAsync(context =>
        {
            context.Request.Method = "GET";
            context.Request.Path = path;
            context.Connection.RemoteIpAddress = IPAddress.Parse(Supervisor);
        });
}
