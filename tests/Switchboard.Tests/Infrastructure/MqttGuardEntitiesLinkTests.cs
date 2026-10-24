using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application;
using Switchboard.Application.Connectivity;
using Switchboard.Application.Guard;
using Switchboard.Application.Health;
using Switchboard.Application.Signals;
using Switchboard.Infrastructure.Mqtt;
using Switchboard.Tests.Support;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// The guard's entities through the real MQTT link: announced with the session, kept current
/// without repeating unchanged states, retracted only when a valid configuration drops them (07:
/// a published entity must be retracted; identity is an API, so a broken file must never delete
/// one), and the kill switch reachable from Home Assistant, but never by a retained message.
/// </summary>
public sealed class MqttGuardEntitiesLinkTests : IAsyncDisposable
{
    private const string KillSwitchCommand = "switchboard/kill_switch/set";

    private readonly ObservableTimeProvider _time = new();
    private readonly FakeMqttTransport _transport = new(new MqttTopicGuard("homeassistant", "switchboard"));
    private readonly HomeAssistantConnectionState _homeAssistant = new();
    private readonly MqttConnectionState _mqttState = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly GuardScenario _scenario = new(TestDevices.Document());
    private readonly GuardStatusHolder _holder;
    private readonly KillSwitchState _killSwitch;
    private readonly RecordingDecisionRecorder _decisions = new();
    private MemoryLedger _ledger = new();
    private Task? _run;

    public MqttGuardEntitiesLinkTests()
    {
        _holder = new GuardStatusHolder(GuardStatus.Initial(_scenario.Now, _scenario.Mode), NullLogger<GuardStatusHolder>.Instance);
        _killSwitch = new KillSwitchState(new GuardInbox(), new SignalClock(_scenario.Time), NullLogger<KillSwitchState>.Instance);
    }

    [Fact]
    public async Task Guard_entities_are_announced_with_the_session_and_states_follow()
    {
        _holder.Publish(_scenario.Evaluate().Status);

        var session = await StartAsync();

        var discovery = session.Where(o => o.Topic.EndsWith("/config", StringComparison.Ordinal)).ToList();
        Assert.Equal(26, discovery.Count);
        Assert.All(discovery, o => Assert.True(o.Retain));
        Assert.Contains(session, o => o.Topic == "switchboard/l1_overload_budget/state" && o.Payload == "100.0" && !o.Retain);
        Assert.Contains(session, o => o.Topic == "switchboard/kill_switch/state" && o.Payload == "OFF");
        Assert.Equal(["subscribe homeassistant/status", $"subscribe {KillSwitchCommand}"], session.Where(o => o.Kind == "subscribe").Select(o => o.ToString()));

        // Every state after every discovery: HA ignores a state that arrives before its config.
        var lastConfig = session.FindLastIndex(o => o.Topic.EndsWith("/config", StringComparison.Ordinal));
        var firstState = session.FindIndex(o => o.Topic.EndsWith("/state", StringComparison.Ordinal));
        Assert.True(lastConfig < firstState);
        Assert.Equal(["sensor/device_ev_charger_state", "sensor/l1_headroom", "switch/kill_switch"], _ledger.Discovered.Where(k => k is "sensor/device_ev_charger_state" or "sensor/l1_headroom" or "switch/kill_switch"));
    }

    [Fact]
    public async Task Kill_switch_command_reaches_the_guard()
    {
        _holder.Publish(_scenario.Evaluate().Status);
        await StartAsync();

        await _transport.DeliverAsync(KillSwitchCommand, "ON");

        Assert.Equal((true, KillSwitchSource.Mqtt), (_killSwitch.Status.Engaged, _killSwitch.Status.Source!.Value));

        // The guard's evaluation then reaches Home Assistant as the switch's state.
        _scenario.KillSwitch = _killSwitch.Status;
        _holder.Publish(_scenario.Evaluate(GuardTrigger.KillSwitch).Status);
        await _transport.UntilAsync(o => o.Topic == "switchboard/kill_switch/state" && o.Payload == "ON");
    }

    [Fact]
    public async Task Retained_command_is_ignored()
    {
        // A retained ON left in the broker would otherwise engage the kill switch at every connect.
        _holder.Publish(_scenario.Evaluate().Status);
        await StartAsync();

        await _transport.DeliverAsync(KillSwitchCommand, "ON", retained: true);

        Assert.False(_killSwitch.Status.Engaged);
    }

    [Fact]
    public async Task Removed_device_retracted()
    {
        _holder.Publish(_scenario.Evaluate().Status);
        await StartAsync();

        _scenario.Reconfigure(TestDevices.Document(string.Join('\n', TestDevices.Reference.Split('\n').TakeWhile(l => !l.Contains("id: ev_charger", StringComparison.Ordinal)))));
        _holder.Publish(_scenario.Evaluate().Status);

        var retraction = await _transport.UntilAsync(o => o.Topic == "homeassistant/sensor/switchboard/device_ev_charger_state/config");
        Assert.Equal(("", true), (retraction[^1].Payload, retraction[^1].Retain));
        Assert.DoesNotContain("sensor/device_ev_charger_state", _ledger.Discovered);
        Assert.Contains("sensor/device_heater_l1_state", _ledger.Discovered);
    }

    [Fact]
    public async Task Invalid_configuration_never_retracts_entities()
    {
        // A broken switchboard.yaml at start: no valid configuration in this process. The entities
        // announced by an earlier run (ledger) stay, unknown; none is retracted.
        // One object from an older version too, which no entity of this version reproduces.
        _ledger = new MemoryLedger("sensor/device_heater_l1_state", "sensor/l1_headroom", "sensor/l2_old_metric", "switch/kill_switch");

        var session = await StartAsync();

        Assert.DoesNotContain(session, o => o.Kind == "publish" && o.Payload.Length == 0);
        Assert.Contains(session, o => o.Topic == "homeassistant/sensor/switchboard/device_heater_l1_state/config" && o.Payload.Length > 0);
        Assert.Contains(session, o => o.Topic == "switchboard/device_heater_l1_state/state" && o.Payload == "None");
        Assert.Contains("sensor/device_heater_l1_state", _ledger.Discovered);
        Assert.Contains("sensor/l2_old_metric", _ledger.Discovered);
    }

    [Fact]
    public async Task Entity_dropped_before_a_restart_is_retracted_at_connect()
    {
        _ledger = new MemoryLedger("sensor/device_old_boiler_state", "sensor/l1_headroom");
        _holder.Publish(_scenario.Evaluate().Status);

        var session = await StartAsync();

        Assert.Contains(session, o => o.Topic == "homeassistant/sensor/switchboard/device_old_boiler_state/config" && o.Payload.Length == 0 && o.Retain);
        Assert.DoesNotContain("sensor/device_old_boiler_state", _ledger.Discovered);
    }

    [Fact]
    public async Task Unchanged_states_are_not_published_again()
    {
        _holder.Publish(_scenario.Evaluate().Status);
        await StartAsync();
        var before = _transport.Log.Count;

        // The same picture again, then one real change: only that change goes out.
        _holder.Publish(_scenario.Last.Status);
        _scenario.Time.Advance(TimeSpan.FromSeconds(10));
        _holder.Publish(_scenario.Sample(Switchboard.Domain.Signals.Phase.L2, 9).Status);
        var after = await _transport.UntilAsync(o => o.Topic == "switchboard/l2_headroom/state");

        Assert.DoesNotContain(_transport.Log.Skip(before), o => o.Topic.EndsWith("/config", StringComparison.Ordinal));
        Assert.DoesNotContain(after, o => o.Topic == "switchboard/l3_headroom/state");
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_run is not null)
        {
            await _run.Soon();
        }

        _stop.Dispose();
    }

    private async Task<List<MqttOperation>> StartAsync()
    {
        var entities = new GuardEntities(
            _holder,
            _decisions,
            _killSwitch,
            _ledger,
            () => new Switchboard.Domain.Units.Volts(230),
            MsOptions.Create(TestOptions.Mqtt()),
            new AppVersion("0.3.0"),
            NullLogger<GuardEntities>.Instance);
        var link = new MqttLink(
            _transport,
            new FakeCredentialSource(),
            _mqttState,
            new HealthEvaluator(_homeAssistant, _mqttState),
            MsOptions.Create(TestOptions.Mqtt()),
            MsOptions.Create(TestOptions.Heartbeat()),
            new AppVersion("0.3.0"),
            [entities],
            _ledger,
            _time,
            NullLogger<MqttLink>.Instance);
        _run = link.RunAsync(_stop.Token);
        return await _transport.UntilAsync(o => o.Topic == KillSwitchCommand && o.Kind == "subscribe");
    }
}
