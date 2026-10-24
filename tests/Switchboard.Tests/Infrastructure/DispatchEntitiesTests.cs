using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application;
using Switchboard.Application.Connectivity;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Guard;
using Switchboard.Application.Health;
using Switchboard.Application.Signals;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Infrastructure.Mqtt;
using Switchboard.Tests.Support;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// Dispatch's MQTT entities (SAFE-04, SAFE-05, PKG-03): per declared device its dispatch state and
/// its override select, under the frozen identity scheme. The select is the second way into the
/// add-on after the kill switch, so its command topic is listened to only for a device of the
/// current valid configuration, exactly, never through a wildcard, and never from a retained
/// message; the dispatch state always says that nothing was commanded.
/// </summary>
public sealed class DispatchEntitiesTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = GuardScenario.Start;
    private static readonly TimeSpan Cycle = TimeSpan.FromSeconds(30);

    private readonly GuardScenario _scenario = DispatchScenario.Create(DispatchDevices.Heaters, ["heater_l1"], "  dispatch:\n    persistence_up_cycles: 1\n");
    private readonly DispatchStatusHolder _status;
    private readonly FakeOverrides _overrides = new();
    private readonly MemoryLedger _ledger = new();
    private readonly ObservableTimeProvider _time = new();
    private readonly MqttConnectionState _mqttState = new();
    private readonly CancellationTokenSource _stop = new();
    private IReadOnlyCollection<DeviceName>? _declared;
    private FakeMqttTransport? _transport;
    private Task? _run;

    public DispatchEntitiesTests()
    {
        _status = new DispatchStatusHolder(DispatchStatus.Initial(_scenario.Now, _scenario.Mode), NullLogger<DispatchStatusHolder>.Instance);
        _declared = [.. _scenario.Setup.Devices.Select(d => d.Id)];
    }

    [Fact]
    public void Dispatch_and_override_entities_follow_the_frozen_identity()
    {
        Publish();
        var guard = new MqttTopicGuard("homeassistant", "switchboard", () => _declared);

        var entities = Entities().Current().Entities;

        Assert.Equal(6, entities.Length);
        foreach (var entity in entities)
        {
            using var config = JsonDocument.Parse(entity.Config);
            var root = config.RootElement;
            Assert.Equal($"switchboard_{entity.ObjectId}", root.GetProperty("unique_id").GetString());
            Assert.Equal($"{entity.Component}.switchboard_{entity.ObjectId}", root.GetProperty("default_entity_id").GetString());
            Assert.Equal($"switchboard/{entity.ObjectId}/state", root.GetProperty("state_topic").GetString());
            Assert.Equal("switchboard/availability", root.GetProperty("availability_topic").GetString());
            Assert.Equal(["switchboard"], root.GetProperty("device").GetProperty("identifiers").EnumerateArray().Select(e => e.GetString()));
            Assert.True(guard.IsAllowed($"homeassistant/{entity.Component}/switchboard/{entity.ObjectId}/config"), entity.ObjectId);
            Assert.True(guard.IsAllowed($"switchboard/{entity.ObjectId}/state"), entity.ObjectId);
        }

        var select = entities.Single(e => e is { Component: "select", ObjectId: "device_heater_l1_override" });
        using var selectConfig = JsonDocument.Parse(select.Config);
        Assert.Equal("switchboard/device_heater_l1_override/set", selectConfig.RootElement.GetProperty("command_topic").GetString());
        Assert.Equal(["auto", "force_on", "force_off"], selectConfig.RootElement.GetProperty("options").EnumerateArray().Select(o => o.GetString()));
        Assert.False(selectConfig.RootElement.GetProperty("retain").GetBoolean());
        Assert.Contains(entities, e => e is { Component: "sensor", ObjectId: "device_heater_l3_dispatch" });

        // Home Assistant may command the select; the add-on itself may never publish there.
        Assert.True(guard.IsSubscriptionAllowed("switchboard/device_heater_l1_override/set"));
        Assert.False(guard.IsAllowed("switchboard/device_heater_l1_override/set"));
    }

    [Fact]
    public void Dispatch_state_is_the_intended_state_and_never_claims_a_command()
    {
        // Shadow mode: dispatch would start heater_l1 (reported off). The entity shows the intended
        // "on", the reported "off" beside it, and commanded false: Switchboard switched nothing.
        _scenario.PollUntil(T0 + Cycle, 3, 3, 3);
        Publish();

        var entities = Entities().Current().Entities.ToDictionary(e => e.ObjectId);

        Assert.Equal("on", entities["device_heater_l1_dispatch"].State);
        using var attributes = JsonDocument.Parse(entities["device_heater_l1_dispatch"].Attributes!);
        var root = attributes.RootElement;
        Assert.Equal(("off", false, false, "headroom_available", "dispatch"), (root.GetProperty("reported").GetString(), root.GetProperty("matches").GetBoolean(), root.GetProperty("commanded").GetBoolean(), root.GetProperty("reason").GetString(), root.GetProperty("origin").GetString()));
        Assert.Equal("on", entities["device_heater_l2_dispatch"].State);
        Assert.Equal("auto", entities["device_heater_l1_override"].State);
    }

    [Fact]
    public void Override_commands_are_exactly_the_options_and_only_for_declared_devices()
    {
        var entities = Entities();

        entities.HandleCommand("switchboard/device_heater_l1_override/set", "FORCE_ON");
        entities.HandleCommand("switchboard/device_heater_l1_override/set", "on");
        entities.HandleCommand("switchboard/device_garage_override/set", "force_on");
        entities.HandleCommand("switchboard/kill_switch/set", "force_on");
        Assert.Empty(_overrides.Calls);

        entities.HandleCommand("switchboard/device_heater_l1_override/set", "force_off");
        entities.HandleCommand("switchboard/device_heater_l1_override/set", "auto");

        Assert.Equal(
            [(DeviceName.Parse("heater_l1"), OverrideMode.ForceOff, OverrideSource.Mqtt), (DeviceName.Parse("heater_l1"), OverrideMode.Auto, OverrideSource.Mqtt)],
            _overrides.Calls);
    }

    [Fact]
    public void Without_a_valid_configuration_earlier_entities_stay_unknown_and_no_override_is_listened_to()
    {
        // A broken switchboard.yaml at start: entities announced by an earlier run stay, unknown,
        // nothing is retracted, and no override command topic is subscribed.
        _declared = null;
        _ledger.Replace(["sensor/device_heater_l2_dispatch", "select/device_heater_l2_override", "sensor/device_heater_l2_state"]);
        var entities = Entities();

        var set = entities.Current();

        Assert.False(set.Authoritative);
        Assert.Equal(["device_heater_l2_dispatch", "device_heater_l2_override"], set.Entities.Select(e => e.ObjectId));
        Assert.All(set.Entities, e => Assert.Equal("None", e.State));
        Assert.Empty(entities.CommandTopics);
        entities.HandleCommand("switchboard/device_heater_l2_override/set", "force_on");
        Assert.Empty(_overrides.Calls);
    }

    [Fact]
    public void Override_command_topics_are_subscribable_only_for_declared_devices()
    {
        // The declared set is re-read at every check: a device removed from the configuration can
        // no longer be listened to, a new one can; nothing else ever, and no wildcard.
        var guard = new MqttTopicGuard("homeassistant", "switchboard", () => _declared);

        Assert.True(guard.IsSubscriptionAllowed("switchboard/device_heater_l2_override/set"));
        Assert.False(guard.IsSubscriptionAllowed("switchboard/device_garage_override/set"));
        Assert.False(guard.IsSubscriptionAllowed("switchboard/+/set"));
        Assert.False(guard.IsSubscriptionAllowed("switchboard/device_heater_l2_override/#"));
        Assert.False(guard.IsSubscriptionAllowed("switchboard/device_heater_l2_override/set/"));
        Assert.False(guard.IsSubscriptionAllowed("switchboard/device_heater_l2_override/state"));
        Assert.False(guard.IsSubscriptionAllowed("switchboard/device_heater_l2_dispatch/set"));

        _declared = [DeviceName.Parse("heater_l1")];
        Assert.False(guard.IsSubscriptionAllowed("switchboard/device_heater_l2_override/set"));
        _declared = null;
        Assert.False(guard.IsSubscriptionAllowed("switchboard/device_heater_l1_override/set"));
        Assert.True(guard.IsSubscriptionAllowed("switchboard/kill_switch/set"));
        Assert.False(new MqttTopicGuard("homeassistant", "switchboard").IsSubscriptionAllowed("switchboard/device_heater_l1_override/set"));
    }

    [Fact]
    public async Task Override_subscriptions_and_entities_follow_the_declared_devices()
    {
        Publish();
        var session = await StartAsync();
        Assert.Equal(
            ["subscribe homeassistant/status", "subscribe switchboard/device_heater_l1_override/set", "subscribe switchboard/device_heater_l2_override/set", "subscribe switchboard/device_heater_l3_override/set"],
            session.Where(o => o.Kind == "subscribe").Select(o => o.ToString()));

        // heater_l2 leaves the configuration: its command topic is unsubscribed and both of its
        // entities are retracted (07: a published entity must be retracted).
        _declared = [DeviceName.Parse("heater_l1"), DeviceName.Parse("heater_l3")];
        Publish();

        var after = await _transport!.UntilAsync(o => o.Kind == "unsubscribe");
        Assert.Equal("unsubscribe switchboard/device_heater_l2_override/set", after[^1].ToString());
        Assert.Contains(after, o => o.Topic == "homeassistant/select/switchboard/device_heater_l2_override/config" && o.Payload.Length == 0 && o.Retain);
        Assert.Contains(after, o => o.Topic == "homeassistant/sensor/switchboard/device_heater_l2_dispatch/config" && o.Payload.Length == 0 && o.Retain);
        Assert.DoesNotContain("select/device_heater_l2_override", _ledger.Discovered);
    }

    [Fact]
    public async Task Override_command_reaches_the_overrides_but_a_retained_one_never_does()
    {
        Publish();
        await StartAsync();

        await _transport!.DeliverAsync("switchboard/device_heater_l1_override/set", "force_on", retained: true);
        Assert.Empty(_overrides.Calls);

        await _transport.DeliverAsync("switchboard/device_heater_l1_override/set", "force_on");
        Assert.Equal([(DeviceName.Parse("heater_l1"), OverrideMode.ForceOn, OverrideSource.Mqtt)], _overrides.Calls);

        // Home Assistant waits for the select's state after a command.
        await _transport.UntilAsync(o => o.Topic == "switchboard/device_heater_l1_override/state" && o.Payload == "force_on");
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

    private DispatchEntities Entities() => new(
        _status,
        _overrides,
        _ledger,
        () => _declared,
        MsOptions.Create(TestOptions.Mqtt()),
        new AppVersion("0.4.0"),
        NullLogger<DispatchEntities>.Instance);

    private void Publish()
    {
        if (_scenario.Evaluations.Count == 0)
        {
            _scenario.Evaluate();
        }

        _status.Publish(DispatchStatusFactory.Build(
            _scenario.Last,
            _scenario.Dispatches[^1],
            _scenario.Targets,
            _overrides.Current,
            _scenario.Lockouts,
            new ControlInputs(_scenario.Inputs(), _scenario.DispatchInputs(), _scenario.DispatchSetup),
            _scenario.Mode,
            _scenario.Now));
    }

    private async Task<List<MqttOperation>> StartAsync()
    {
        _transport = new FakeMqttTransport(new MqttTopicGuard("homeassistant", "switchboard", () => _declared));
        var link = new MqttLink(
            _transport,
            new FakeCredentialSource(),
            _mqttState,
            new HealthEvaluator(new HomeAssistantConnectionState(), _mqttState),
            MsOptions.Create(TestOptions.Mqtt()),
            MsOptions.Create(TestOptions.Heartbeat()),
            new AppVersion("0.4.0"),
            [Entities()],
            _ledger,
            _time,
            NullLogger<MqttLink>.Instance);
        _run = link.RunAsync(_stop.Token);
        return await _transport.UntilAsync(o => o.Kind == "subscribe" && o.Topic == "switchboard/device_heater_l3_override/set");
    }

    /// <summary>Records override requests and holds them as the current snapshot.</summary>
    private sealed class FakeOverrides : IDeviceOverrideControl
    {
        private readonly List<(DeviceName, OverrideMode, OverrideSource)> _calls = [];

        public IReadOnlyList<(DeviceName Device, OverrideMode Mode, OverrideSource Source)> Calls
        {
            get
            {
                lock (_calls)
                {
                    return [.. _calls];
                }
            }
        }

        public OverrideSnapshot Current { get; private set; } = OverrideSnapshot.None;

        public OverrideResult Set(DeviceName device, OverrideMode mode, OverrideSource source)
        {
            lock (_calls)
            {
                _calls.Add((device, mode, source));
                Current = new OverrideSnapshot(Current.Version + 1, mode == OverrideMode.Auto ? Current.Devices.Remove(device) : Current.Devices.SetItem(device, new DeviceOverride(mode, T0, source)));
            }

            return OverrideResult.Applied;
        }
    }
}
