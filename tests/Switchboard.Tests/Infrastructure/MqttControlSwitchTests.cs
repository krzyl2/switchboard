using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Signals;
using Switchboard.Domain.Units;
using Switchboard.Infrastructure.Mqtt;
using Switchboard.Tests.Support;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// MODE-02 from Home Assistant: the control switch is an MQTT switch under the frozen identity,
/// commanded with exactly ON and OFF. A refused ON (the state after an untrusted state.json is not
/// confirmed) puts Home Assistant's switch back to what is in force instead of leaving it showing
/// a control that is not allowed. The kill switch says whether it switches anything now.
/// </summary>
public sealed class MqttControlSwitchTests
{
    [Fact]
    public void The_control_switch_is_a_switch_under_the_frozen_identity_and_starts_off()
    {
        var (loop, entities) = Build(OperatingMode.Active);
        loop.Runner.Step();

        var entity = entities.Current().Entities.Single(e => e.ObjectId == GuardEntities.ControlAllowedObjectId);
        using var config = JsonDocument.Parse(entity.Config);

        Assert.Equal(("switch", "OFF"), (entity.Component, entity.State));
        Assert.Equal("switchboard_control_allowed", config.RootElement.GetProperty("unique_id").GetString());
        Assert.Equal("switchboard/control_allowed/set", config.RootElement.GetProperty("command_topic").GetString());
        Assert.False(config.RootElement.GetProperty("optimistic").GetBoolean());
        Assert.Contains("switchboard/control_allowed/set", entities.CommandTopics);
        Assert.True(new MqttTopicGuard("homeassistant", "switchboard").IsSubscriptionAllowed("switchboard/control_allowed/set"));
        using var attributes = JsonDocument.Parse(entity.Attributes!);
        Assert.Equal(["control_off"], attributes.RootElement.GetProperty("missing").EnumerateArray().Select(m => m.GetString()));
    }

    [Fact]
    public void Commands_are_exactly_ON_and_OFF_and_are_recorded_as_coming_from_home_assistant()
    {
        var (loop, entities) = Build(OperatingMode.Active);

        entities.HandleCommand("switchboard/control_allowed/set", "on");
        Assert.False(loop.Control!.Status.Allowed);
        entities.HandleCommand("switchboard/control_allowed/set", "ON");
        Assert.Equal((true, ControlSwitchSource.Mqtt), (loop.Control.Status.Allowed, loop.Control.Status.Source));
        loop.Runner.Step();
        Assert.Equal("ON", entities.Current().Entities.Single(e => e.ObjectId == GuardEntities.ControlAllowedObjectId).State);
        entities.HandleCommand("switchboard/control_allowed/set", "OFF");
        Assert.False(loop.Control.Status.Allowed);
    }

    [Fact]
    public void A_refused_ON_sets_home_assistants_switch_back()
    {
        var (loop, entities) = Build(OperatingMode.Active);
        loop.Control!.Lock(ControlLockReason.StateFileCorrupt);
        loop.Runner.Step();
        var republished = 0;
        entities.Changed += (_, _) => republished++;

        entities.HandleCommand("switchboard/control_allowed/set", "ON");
        entities.HandleCommand("switchboard/kill_switch/set", "OFF");

        Assert.Equal(2, republished);
        Assert.False(loop.Control.Status.Allowed);
        Assert.True(loop.KillSwitch.Status.Engaged);
        var control = entities.Current().Entities.Single(e => e.ObjectId == GuardEntities.ControlAllowedObjectId);
        Assert.Equal("OFF", control.State);
        using var attributes = JsonDocument.Parse(control.Attributes!);
        Assert.True(attributes.RootElement.GetProperty("locked").GetBoolean());
    }

    [Fact]
    public void The_kill_switch_says_whether_it_switches_anything_now()
    {
        var (loop, entities) = Build(OperatingMode.Active);
        loop.Runner.Step();
        Assert.False(KillSwitchAttribute(entities).GetProperty("switches_devices").GetBoolean());
        Assert.Equal("nothing is switched: control is not active", KillSwitchAttribute(entities).GetProperty("note").GetString());
    }

    private static JsonElement KillSwitchAttribute(GuardEntities entities)
    {
        using var json = JsonDocument.Parse(entities.Current().Entities.Single(e => e.ObjectId == GuardEntities.KillSwitchObjectId).Attributes!);
        return json.RootElement.Clone();
    }

    private static (ControlLoop Loop, GuardEntities Entities) Build(OperatingMode ceiling)
    {
        var time = new SteppableTime(GuardScenario.Start);
        var store = new SignalStore(new SignalStoreSettings(TimeSpan.FromSeconds(5)));
        store.ApplyConfiguration(TestConfigurations.Parse(TestDevices.Document()), time.GetUtcNow());
        var holder = new SignalSnapshotHolder(new SignalDiagnostics(store.Snapshot(time.GetUtcNow()), null, [], []));
        var loop = ControlLoop.WithControl(holder, new SignalClock(time), ceiling, "mqttctl");
        var entities = new GuardEntities(
            loop.Status,
            loop.Recorder,
            loop.KillSwitch,
            new MemoryLedger(),
            () => new Volts(230),
            MsOptions.Create(TestOptions.Mqtt()),
            new AppVersion("0.7.0"),
            NullLogger<GuardEntities>.Instance,
            loop.Control);
        return (loop, entities);
    }
}
