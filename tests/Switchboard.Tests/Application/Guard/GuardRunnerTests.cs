using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Switchboard.Application.Decisions;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Signals;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Guard;

/// <summary>
/// The guard runner as wired in production: the real signal store, the protection port, the
/// effector gate and the recorder. Its steps are atomic, it wakes on its own deadlines, and nothing
/// a recorder or a listener does can stop protection.
/// </summary>
public sealed class GuardRunnerTests
{
    [Fact]
    public void First_step_records_the_mode_of_this_start()
    {
        // MODE-01: every start says what was asked for and what runs; in this version always shadow.
        var h = new GuardRunnerHarness(TestDevices.Document(), ceiling: OperatingMode.Active, uiChoice: OperatingMode.Active);
        h.PushReference();

        h.Runner.Step();
        h.Runner.Step();

        var mode = Assert.Single(h.Recorder.Decisions, d => d.Action == DecisionAction.ModeResolved);
        Assert.Equal((DecisionScope.System, ReasonCode.ModeShadow, OperatingMode.Shadow), (mode.Scope, mode.Reason, mode.Mode));
        Assert.Equal(new ModeDecisionInputs(OperatingMode.Active, OperatingMode.Active, ModeNote.ActiveNotEnabled), mode.Inputs.Mode);
        Assert.Equal(OperatingMode.Shadow, h.Status.Current.Mode.Effective);
    }

    [Fact]
    public void Recorder_failure_never_stops_protection()
    {
        // Fail-safe row "decision log unwritable": the guard keeps evaluating and publishing.
        var h = new GuardRunnerHarness(TestDevices.Document());
        h.Recorder.Fail = true;
        h.PushReference(l1: 37.5);

        h.Runner.Step();

        Assert.Equal(DeviceGuardState.Shed, h.Status.Current.Devices.Single(d => d.Id.Value == "heater_l1").GuardState);
        Assert.False(h.Recorder.Status.Writable);
    }

    [Fact]
    public void A_failing_step_listener_never_stops_protection()
    {
        var h = new GuardRunnerHarness(TestDevices.Document());
        h.Runner.Stepped += (_, _) => throw new InvalidOperationException("listener broke");
        h.PushReference(l1: 37.5);

        h.Runner.Step();
        h.Time.Advance(TimeSpan.FromSeconds(1));
        h.Push(("sensor.meter_current_b", "40", "A"));
        h.Runner.Step();

        Assert.Equal(DeviceGuardState.Shed, h.Status.Current.Devices.Single(d => d.Id.Value == "heater_l2").GuardState);
    }

    [Fact]
    public void A_failing_status_listener_never_stops_protection_or_the_other_listeners()
    {
        // Status listeners (MQTT entities, an API waiter) run on the guard's thread: one that
        // throws must not end the guard loop, nor keep the next listener from the status.
        var h = new GuardRunnerHarness(TestDevices.Document());
        var seen = new List<GuardStatus>();
        h.Status.Published += (_, _) => throw new InvalidOperationException("listener broke");
        h.Status.Published += (_, status) => seen.Add(status);
        h.PushReference(l1: 37.5);

        h.Runner.Step();
        h.Time.Advance(TimeSpan.FromSeconds(1));
        h.Push(("sensor.meter_current_b", "40", "A"));
        h.Runner.Step();

        Assert.Equal(2, seen.Count);
        Assert.Equal(DeviceGuardState.Shed, h.Status.Current.Devices.Single(d => d.Id.Value == "heater_l2").GuardState);
        Assert.Contains(h.Recorder.Decisions, d => d.Device?.Value == "heater_l2" && d.Action == DecisionAction.SwitchOff);
    }

    [Fact]
    public void Store_snapshots_drive_the_guard_with_device_states()
    {
        // The guard reads the device states the store derived from the state entities: the charger
        // reports 16 A, so reducing it relieves 10 A on L1.
        var h = new GuardRunnerHarness(TestDevices.Document());
        h.PushReference(l1: 30);
        h.Runner.Step();
        h.Time.Advance(TimeSpan.FromSeconds(30));
        h.Push(("sensor.meter_current_a", "30.1", "A"));
        h.Runner.Step();

        var l1 = h.Status.Current.Phases.Single(p => p.Phase == Phase.L1);
        Assert.Equal(new SetpointApplied(new Switchboard.Domain.Units.Amperes(16)), h.Status.Current.Devices.Single(d => d.Id.Value == "ev_charger").Stages.Single().Applied);
        Assert.Contains(h.Recorder.Decisions, d => d.Device?.Value == "heater_l1" && d.Action == DecisionAction.SwitchOff);
        Assert.Equal(30.1 - 6.6, l1.EffectiveCurrent!.Value.Value, 6);
    }

    [Fact]
    public async Task Shutdown_is_evaluated_at_once_and_marks_a_clean_stop()
    {
        var h = new GuardRunnerHarness(TestDevices.Document());
        h.PushReference();
        h.Runner.Step();
        GuardStep? last = null;
        h.Runner.Stepped += (_, step) => last = step;

        await h.Runner.ShutdownAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, h.Recorder.Decisions.Count(d => d.Action == DecisionAction.SwitchOff && d.Reason == ReasonCode.SafeState));
        Assert.True(last!.Memory.CleanShutdown);
    }

    [Fact]
    public async Task Runner_wakes_at_the_cycle_boundary_without_a_new_snapshot()
    {
        // 1.20 × In waits for the next boundary; only the runner's own timer can get it there.
        var time = new FakeTimeProvider(GuardScenario.Start + TimeSpan.FromSeconds(5));
        var clock = new SignalClock(time);
        var store = new SignalStore(new SignalStoreSettings(TimeSpan.FromSeconds(5)));
        store.ApplyConfiguration(TestConfigurations.Valid(TestDevices.Document()), time.GetUtcNow());
        store.ApplySourceStatus(SignalSourceStatus.Connected);
        var now = time.GetUtcNow();
        store.Apply(new EntityUpdateBatch(
            BatchOrigin.Change,
            [.. new[] { ("switch.heater_1", "on", (string?)null), ("switch.heater_2", "on", null), ("switch.heater_3", "on", null), ("number.charger_current", "16", "A"), ("sensor.meter_current_a", "30", "A"), ("sensor.meter_current_b", "8", "A"), ("sensor.meter_current_c", "8", "A") }
                .Select(s => new EntityUpdate(EntityId.Parse(s.Item1), s.Item2, s.Item3, now, now))],
            [],
            []), now);
        var holder = new SignalSnapshotHolder(new SignalDiagnostics(store.Snapshot(now), store.ConfigurationResult, [], []));
        var lockouts = new LockoutClocks();
        lockouts.Observe(Switchboard.Application.Configuration.ProtectionSetupFactory.Build(store.ConfigurationResult!.Configuration!), [], now - TimeSpan.FromHours(1));
        var loop = new ControlLoop(holder, clock, new ModeState(OperatingMode.Shadow, OperatingMode.Shadow), "boot2", lockouts: lockouts);
        var status = loop.Status;
        var runner = loop.Runner;
        var shed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        status.Published += (_, s) =>
        {
            if (s.Devices.Any(d => d.GuardState == DeviceGuardState.Shed))
            {
                shed.TrySetResult();
            }
        };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Stepped += (_, _) => started.TrySetResult();
        using var stop = new CancellationTokenSource();

        var run = runner.RunAsync(stop.Token);
        await started.Task.Soon();
        Assert.False(shed.Task.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(25));
        await shed.Task.Soon();

        Assert.Equal(GuardScenario.Start + TimeSpan.FromSeconds(30), status.Current.At);
        await stop.CancelAsync();
        await run.Soon();
        runner.Dispose();
    }
}
