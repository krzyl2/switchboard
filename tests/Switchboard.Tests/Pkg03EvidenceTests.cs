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
        var (loop, _, entities) = Build();
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
    public void Mode_entity_follows_ui_choice_without_restart()
    {
        var (loop, mode, entities) = Build();
        loop.Runner.Step();
        Assert.Equal("active", Attribute(entities, "ui_choice"));

        mode.Choose(OperatingMode.Shadow);
        loop.Runner.Step();

        var entity = entities.Current().Entities.Single(e => e.ObjectId == GuardEntities.ModeObjectId);
        Assert.Equal("shadow", entity.State);
        Assert.Equal("shadow", Attribute(entities, "ui_choice"));
        Assert.Equal("active", Attribute(entities, "ceiling"));
    }

    private static string? Attribute(GuardEntities entities, string key)
    {
        var attributes = entities.Current().Entities.Single(e => e.ObjectId == GuardEntities.ModeObjectId).Attributes!;
        using var json = JsonDocument.Parse(attributes);
        return json.RootElement.GetProperty(key).GetString();
    }

    private static (ControlLoop Loop, ModeState Mode, GuardEntities Entities) Build()
    {
        var time = new SteppableTime(GuardScenario.Start);
        var store = new SignalStore(new SignalStoreSettings(TimeSpan.FromSeconds(5)));
        store.ApplyConfiguration(TestConfigurations.Parse(TestDevices.Document()), time.GetUtcNow());
        var holder = new SignalSnapshotHolder(new SignalDiagnostics(store.Snapshot(time.GetUtcNow()), null, [], []));
        var inbox = new GuardInbox();
        var mode = new ModeState(OperatingMode.Active, OperatingMode.Active, inbox);
        var loop = new ControlLoop(holder, new SignalClock(time), mode, "pkg03", inbox: inbox);
        var entities = new GuardEntities(
            loop.Status,
            loop.Recorder,
            loop.KillSwitch,
            new MemoryLedger(),
            () => new Volts(230),
            MsOptions.Create(TestOptions.Mqtt()),
            new AppVersion("0.6.0"),
            NullLogger<GuardEntities>.Instance);
        return (loop, mode, entities);
    }
}
