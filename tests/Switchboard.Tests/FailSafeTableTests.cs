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

    [Fact]
    public void Stale_draw_through_the_store_relieves_nothing_and_holds_nothing_off()
    {
        // Row "draw signal stale": the real store and runner. The charger's draw current goes stale
        // after its 60 s max_age: it is no longer trusted for relief (a held charger then relieves
        // nothing) nor for an increase, but a draw is not a protection input, so nothing is held off.
        var harness = new GuardRunnerHarness(TestDevices.Document(TestDevices.ReferenceWithChargerDraw));
        harness.Push(("sensor.charger_draw_current", "16", "A"), ("sensor.charger_draw_power", "3680", "W"));
        harness.PushReference(l1: 8);
        harness.Runner.Step();
        Assert.True(Assert.Single(harness.Holder.Current.Snapshot.Protection.DeviceDraws).Fresh);

        harness.Time.Advance(TimeSpan.FromSeconds(61));
        harness.PushReference(l1: 8);
        harness.Runner.Step();

        Assert.False(Assert.Single(harness.Holder.Current.Snapshot.Protection.DeviceDraws).Fresh);
        Assert.Equal(DeviceGuardState.Allowed, harness.Status.Current.Devices.Single(d => d.Id.Value == "ev_charger").GuardState);
        Assert.False(harness.Loop.DispatchStatus.Current.Devices.Single(d => d.Id.Value == "ev_charger").Draw!.Fresh);
        Assert.Equal(8, harness.Status.Current.Phases.Single(p => p.Phase == Phase.L1).EffectiveCurrent!.Value.Value, 9);
    }

    [Fact]
    public void Kill_switch_outranks_a_forced_on_device_and_the_override_waits_for_its_release()
    {
        // Row "override with the kill switch engaged": the real store and runner. A force_on set
        // while the kill switch holds everything changes nothing (protection outranks an override);
        // after the release the override acts only once the holds were let go after
        // persistence_up_cycles good boundaries, and it is recorded as a manual override, never
        // commanded in this version.
        var harness = new GuardRunnerHarness(TestDevices.Document());
        var heater = Switchboard.Domain.Devices.DeviceName.Parse("heater_l1");
        PushQuiet(harness);
        harness.KillSwitch.Set(true, KillSwitchSource.Api);
        harness.Runner.Step();
        Assert.Equal(Switchboard.Application.Dispatch.OverrideResult.Applied, harness.Loop.Overrides.Set(heater, Switchboard.Application.Dispatch.OverrideMode.ForceOn, Switchboard.Application.Dispatch.OverrideSource.Api));

        Advance(harness, TimeSpan.FromMinutes(3));
        Assert.DoesNotContain(harness.Recorder.Decisions, d => d.Device == heater && d.Action == DecisionAction.SwitchOn);
        Assert.Contains(harness.Recorder.Decisions, d => d.Device == heater && d.Action == DecisionAction.OverrideSet);

        harness.KillSwitch.Set(false, KillSwitchSource.Api);
        var released = harness.Now;
        Advance(harness, TimeSpan.FromMinutes(4));

        var start = Assert.Single(harness.Recorder.Decisions, d => d.Device == heater && d.Action == DecisionAction.SwitchOn);
        Assert.Equal((Switchboard.Domain.Decisions.ReasonCode.ManualOverride, Switchboard.Application.Decisions.DecisionOrigin.Override, false), (start.Reason, start.Origin!.Value, start.Commanded));
        Assert.True(start.At >= released + TimeSpan.FromSeconds(150), $"started at {start.At:O}, released at {released:O}");
    }

    [Fact]
    public void Kill_switch_sends_the_select_stop_within_one_cycle()
    {
        // SAFE-03 with a select stop (0.7.1): the kill switch's step itself sends the stop option.
        var rig = new SelectStopRig().Begin();

        rig.Harness.KillSwitch.Set(true, KillSwitchSource.Api);
        rig.Harness.Runner.Step();

        Assert.Equal(["1"], rig.SelectOptions);
        Assert.Contains(rig.Harness.Recorder.Decisions, d => d.Device?.Value == "ev_charger" && d.Action == DecisionAction.SwitchOff && d.Reason == Switchboard.Domain.Decisions.ReasonCode.KillSwitch && d.Commanded);
    }

    [Fact]
    public void Stale_meter_sends_the_select_stop()
    {
        // SAFE-01 "stale signal": the meter falls silent beyond its 90 s max_age; the safe state
        // stops the charger through its select like any controlled device.
        var rig = new SelectStopRig().Begin();

        rig.Harness.Time.Advance(TimeSpan.FromSeconds(91));
        rig.Harness.Publish();
        rig.Harness.Runner.Step();

        Assert.Equal(["1"], rig.SelectOptions);
        Assert.Contains(rig.Harness.Recorder.Decisions, d => d.Action == DecisionAction.SafeStateEntered);
    }

    [Fact]
    public void HA_loss_stop_is_reasserted_after_reconnect()
    {
        // SAFE-01 "HA disconnected": the safe state's stop cannot leave while the link is down; the
        // new link's states still show the charger running, so the stop is sent again, once.
        var rig = new SelectStopRig().Begin();
        rig.Disconnect();
        rig.Harness.Publish();
        rig.Harness.Runner.Step();
        Assert.Equal(["1"], rig.SelectOptions);

        rig.Harness.Time.Advance(TimeSpan.FromSeconds(30));
        rig.Reconnect();
        rig.Poll(2);
        rig.Hold(2, TimeSpan.FromSeconds(30));

        Assert.Equal(["1", "1"], rig.SelectOptions);
        Assert.Contains(rig.Harness.Recorder.Decisions, d => d.Device?.Value == "ev_charger" && d.Inputs.Dispatch?.Reasserted == true && d.Commanded);
    }

    [Fact]
    public void Stale_select_state_still_gets_the_protective_stop()
    {
        // The go-e's link drops: its select turns unavailable and its options are no longer known.
        // Its state is unknown, so protection holds it off, and the stop is sent anyway: unknown
        // options never block a protective stop.
        var rig = new SelectStopRig().Begin();
        rig.Option = "unavailable";

        rig.Poll(2);

        Assert.Equal(["1"], rig.SelectOptions);
        Assert.Empty(rig.Path.Refused);
    }

    [Fact]
    public void A_stop_acknowledged_in_the_step_of_the_first_states_still_resumes_the_users_level()
    {
        // After a restart the charger's states arrive after the safe state's stop left and was
        // acknowledged; the first report (its old last_reported, from before the stop) shows the
        // user's "0" at 10 A. It is the level to give back, so the charger is resumed after the
        // release (0.7.0 dropped it when the acknowledgement came first, and left the charger stopped).
        var rig = new SelectStopRig().BeginWithoutCharger();
        Assert.Equal(["1"], rig.SelectOptions);
        var before = rig.Harness.Now - TimeSpan.FromHours(3);

        rig.Harness.Time.Advance(TimeSpan.FromSeconds(10));
        rig.ReportCharger(before);
        rig.Harness.Publish();
        rig.Harness.Runner.Step();
        Assert.NotNull(rig.Ledger.Baseline);

        rig.Hold(2, TimeSpan.FromMinutes(12));

        Assert.Equal(["1", "0"], rig.SelectOptions);
    }

    /// <summary>heater_l1 reported off, the other loads on, every phase at a quiet 8 A.</summary>
    private static void PushQuiet(GuardRunnerHarness harness) => harness.Push(
        ("switch.heater_1", "off", null),
        ("switch.heater_2", "on", null),
        ("switch.heater_3", "on", null),
        ("number.charger_current", "16", "A"),
        ("sensor.meter_current_a", "8", "A"),
        ("sensor.meter_current_b", "8", "A"),
        ("sensor.meter_current_c", "8", "A"));

    /// <summary>The meter reporting every 10 s for <paramref name="duration"/>, one runner step per report.</summary>
    private static void Advance(GuardRunnerHarness harness, TimeSpan duration)
    {
        var end = harness.Now + duration;
        while (harness.Now < end)
        {
            harness.Time.Advance(TimeSpan.FromSeconds(10));
            harness.Push(("sensor.meter_current_a", "8", "A"), ("sensor.meter_current_b", "8", "A"), ("sensor.meter_current_c", "8", "A"));
            harness.Runner.Step();
        }
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
