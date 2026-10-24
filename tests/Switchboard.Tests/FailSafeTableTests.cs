using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application;
using Switchboard.Application.Connectivity;
using Switchboard.Application.Decisions;
using Switchboard.Application.Guard;
using Switchboard.Application.Health;
using Switchboard.Application.State;
using Switchboard.Domain.Signals;
using Switchboard.Infrastructure.Mqtt;
using Switchboard.Infrastructure.Persistence;
using Switchboard.Tests.Support;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Switchboard.Tests;

/// <summary>
/// Rows of the fail-safe table (VER-03) whose reaction lives outside the guard engine: the guard
/// must not notice a broker outage at all, and a data folder it cannot write must cost the record
/// on disk, never protection. The other rows are covered where their mechanism lives; the table in
/// 04-ALGORITHMS names the test of every row.
/// </summary>
public sealed class FailSafeTableTests
{
    [Fact]
    public async Task Mqtt_outage_reports_unhealthy_and_leaves_guard_decisions_unchanged()
    {
        // Row "MQTT disconnected": the reaction is on the Home Assistant side. Every CONNECT carries
        // the retained "offline" will on the availability topic (so a dead link turns every entity
        // unavailable), the link never reports connected, and health is false although Home
        // Assistant is connected, so the heartbeat sends the health entity OFF (HeartbeatLoopTests)
        // and the user's shed automation fires. The guard does not depend on MQTT at all: the same
        // overload with the broker down is decided exactly as with nothing listening.
        var quiet = Run(null);
        var broker = new BrokerDown();
        broker.HomeAssistant.Set(ConnectionState.Connected);
        var outage = Run(broker);
        await broker.Transport.UntilAsync(o => o.Kind == "connect-failed").Soon();

        Assert.All(broker.Transport.Connects, c => Assert.Equal(("switchboard/availability", "offline", true), (c.WillTopic, c.WillPayload, c.WillRetain)));
        Assert.NotEqual(ConnectionState.Connected, broker.Mqtt.Current);
        Assert.False(broker.Health.IsHealthy);
        Assert.NotEmpty(quiet);
        Assert.Equal(quiet, outage);
    }

    [Fact]
    public async Task Unwritable_data_path_keeps_the_guard_running()
    {
        // Standalone Docker without a writable /data (here: a file where the folder should be).
        using var data = new TempDataDirectory();
        var blocked = Path.Combine(data.Path, "not-a-folder");
        await File.WriteAllTextAsync(blocked, "x", TestContext.Current.CancellationToken);
        var options = TestDecisions.Options();
        var log = new JsonlDecisionLog(new DecisionFileSystem(), blocked, options, () => 30, TimeProvider.System, NullLogger<JsonlDecisionLog>.Instance);
        var stateFile = new RuntimeStateFile(blocked, options, NullLogger<RuntimeStateFile>.Instance);
        var state = new RuntimeStateCoordinator(stateFile, stateFile.Load(), () => null, MsOptions.Create(options), TimeProvider.System, NullLogger<RuntimeStateCoordinator>.Instance);
        var scenario = new GuardScenario(TestDevices.Document());

        log.Start();
        scenario.Evaluate();
        scenario.Time.Advance(TimeSpan.FromSeconds(10));
        scenario.SampleMultiple(Phase.L1, 1.32);
        log.Record(scenario.Decisions);
        await log.FlushAsync(CancellationToken.None);
        state.OnKillSwitchChanged(new KillSwitchStatus(true, scenario.Now, KillSwitchSource.Api));
        await state.FlushAsync(CancellationToken.None);

        // Protection decided as always; the record is in memory and both writers say why not on disk.
        Assert.Contains(scenario.Sheds, i => i.Device.Value == "heater_l1");
        Assert.Contains(log.Query(new DecisionQuery(null, null, null, 50, null)).Items, d => d.Device?.Value == "heater_l1");
        Assert.False(log.Status.Writable);
        Assert.False(state.Report.Writable);
        Assert.True(state.Current.KillSwitch);
    }

    private static List<(DateTimeOffset, string?, DecisionAction, Switchboard.Domain.Decisions.ReasonCode)> Run(BrokerDown? broker)
    {
        var harness = new GuardRunnerHarness(TestDevices.Document());
        using var stop = new CancellationTokenSource();
        if (broker is not null)
        {
            var entities = new GuardEntities(harness.Status, harness.Recorder, harness.KillSwitch, new MemoryLedger(), () => null, MsOptions.Create(TestOptions.Mqtt()), new AppVersion("0.3.0"), NullLogger<GuardEntities>.Instance);
            var link = new MqttLink(
                broker.Transport,
                new FakeCredentialSource(),
                broker.Mqtt,
                broker.Health,
                MsOptions.Create(TestOptions.Mqtt()),
                MsOptions.Create(TestOptions.Heartbeat()),
                new AppVersion("0.3.0"),
                [entities],
                new MemoryLedger(),
                new ObservableTimeProvider(),
                NullLogger<MqttLink>.Instance);
            _ = link.RunAsync(stop.Token);
        }

        harness.PushReference(l1: 8);
        harness.Runner.Step();
        foreach (var amps in new[] { 30.0, 33.0, 37.0, 8.0 })
        {
            harness.Time.Advance(TimeSpan.FromSeconds(10));
            harness.PushReference(l1: amps);
            harness.Runner.Step();
        }

        stop.Cancel();
        return [.. harness.Recorder.Decisions.Select(d => (d.At, d.Device?.Value, d.Action, d.Reason))];
    }

    /// <summary>A broker that refuses every connection, with the link's states as health reads them.</summary>
    private sealed class BrokerDown
    {
        public BrokerDown() => Health = new HealthEvaluator(HomeAssistant, Mqtt);

        public FakeMqttTransport Transport { get; } = new(new MqttTopicGuard("homeassistant", "switchboard")) { FailConnects = true };

        public MqttConnectionState Mqtt { get; } = new();

        public HomeAssistantConnectionState HomeAssistant { get; } = new();

        public HealthEvaluator Health { get; }
    }
}
