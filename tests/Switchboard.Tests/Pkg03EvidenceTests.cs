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

namespace Switchboard.Tests;

/// <summary>
/// PKG-03 checked against its text: MQTT discovery publishes the runtime entities per-phase headroom,
/// overload budget, mode, health and per-device state, under the frozen identity
/// (unique_id switchboard_&lt;object_id&gt;). The mode entity follows a UI mode choice without a
/// restart (MODE-02), while it keeps showing the effective mode (shadow in this version).
/// </summary>
public sealed class Pkg03EvidenceTests
{
    [Fact]
    public void Runtime_entities_cover_headroom_budget_mode_health_device_state()
    {
        var (loop, entities) = Build();
        loop.Runner.Step();

        var ids = entities.Current().Entities.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var phase in new[] { "l1", "l2", "l3" })
        {
            Assert.Contains($"sensor/{phase}_headroom", ids);
            Assert.Contains($"sensor/{phase}_breaker_headroom", ids);
            Assert.Contains($"sensor/{phase}_overload_budget", ids);
        }

        Assert.Contains("sensor/mode", ids);
        Assert.Contains("sensor/last_decision", ids);
        Assert.Contains("sensor/device_heater_l1_state", ids);
        Assert.Contains("sensor/device_ev_charger_state", ids);
        Assert.Equal("healthy", HealthEntityDiscovery.ObjectId);
        using var health = JsonDocument.Parse(HealthEntityDiscovery.BuildConfig(TestOptions.Mqtt(), TestOptions.Heartbeat(), new AppVersion("0.6.0")));
        Assert.Equal("switchboard_healthy", health.RootElement.GetProperty("unique_id").GetString());
    }

    [Fact]
    public void Mode_entity_follows_the_control_switch_without_restart()
    {
        var (loop, entities) = Build();
        loop.Runner.Step();
        Assert.Equal(("shadow", false), (entities.Current().Entities.Single(e => e.ObjectId == GuardEntities.ModeObjectId).State, Attributes(entities).GetProperty("control_allowed").GetBoolean()));

        loop.Control!.Set(true, ControlSwitchSource.Api);
        loop.Runner.Step();

        var entity = entities.Current().Entities.Single(e => e.ObjectId == GuardEntities.ModeObjectId);
        Assert.Equal("active", entity.State);
        Assert.True(Attributes(entities).GetProperty("control_allowed").GetBoolean());
        Assert.Equal("active", Attributes(entities).GetProperty("ceiling").GetString());
    }

    [Fact]
    public void Mode_entity_attributes_have_no_ui_choice()
    {
        // Cleanup 0.7.1: the 0.6.x UI choice (always shadow since the control switch replaced it)
        // is gone from the mode sensor; control_allowed says what it used to stand for.
        var (loop, entities) = Build();
        loop.Runner.Step();

        Assert.Equal(["ceiling", "note", "control_allowed", "missing"], Attributes(entities).EnumerateObject().Select(p => p.Name));
    }

    private static JsonElement Attributes(GuardEntities entities)
    {
        var attributes = entities.Current().Entities.Single(e => e.ObjectId == GuardEntities.ModeObjectId).Attributes!;
        using var json = JsonDocument.Parse(attributes);
        return json.RootElement.Clone();
    }

    private static (ControlLoop Loop, GuardEntities Entities) Build()
    {
        var time = new SteppableTime(GuardScenario.Start);
        var store = new SignalStore(new SignalStoreSettings(TimeSpan.FromSeconds(5)));
        store.ApplyConfiguration(TestConfigurations.Parse(TestDevices.Document()), time.GetUtcNow());
        var holder = new SignalSnapshotHolder(new SignalDiagnostics(store.Snapshot(time.GetUtcNow()), null, [], []));
        var loop = ControlLoop.WithControl(holder, new SignalClock(time), OperatingMode.Active, "pkg03");
        var entities = new GuardEntities(
            loop.Status,
            loop.Recorder,
            loop.KillSwitch,
            new MemoryLedger(),
            () => new Volts(230),
            MsOptions.Create(TestOptions.Mqtt()),
            new AppVersion("0.6.0"),
            NullLogger<GuardEntities>.Instance,
            loop.Control);
        return (loop, entities);
    }
}
