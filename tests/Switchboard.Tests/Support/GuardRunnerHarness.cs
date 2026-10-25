using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Configuration;
using Switchboard.Application.Decisions;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Signals;
using Switchboard.Domain.Signals;

namespace Switchboard.Tests.Support;

/// <summary>
/// The control loop as production wires it: the real signal store publishes snapshots into a
/// holder, the guard runner reads them through the control port, runs guard, dispatch and the
/// effector gate, and a recording decision recorder takes the result. Steps are explicit on a
/// steppable virtual clock.
/// </summary>
internal sealed class GuardRunnerHarness
{
    /// <param name="port">A recording command port: with it the loop runs with the real control switch and mode state (control starts off), as GuardService composes them.</param>
    /// <param name="selfTest">The self-test slot the gate and the runner share (active self-test scenarios).</param>
    public GuardRunnerHarness(string yaml, OperatingMode ceiling = OperatingMode.Shadow, OperatingMode uiChoice = OperatingMode.Shadow, IDeviceCommandPort? port = null, SelfTestSlot? selfTest = null)
    {
        Time = new SteppableTime(GuardScenario.Start);
        var clock = new SignalClock(Time);
        Store = new SignalStore(new SignalStoreSettings(TimeSpan.FromSeconds(5)));
        Configuration = TestConfigurations.Parse(yaml);
        Store.ApplyConfiguration(Configuration, Now);
        Store.ApplySourceStatus(SignalSourceStatus.Connected);
        Holder = new SignalSnapshotHolder(new SignalDiagnostics(Store.Snapshot(Now), null, [], []));
        Loop = port is null && selfTest is null
            ? new ControlLoop(Holder, clock, TestModes.State(ceiling, uiChoice == OperatingMode.Active), "boot1")
            : ControlLoop.WithControl(Holder, clock, ceiling, "boot1", port, selfTest);
    }

    /// <summary>The real control switch (only with a port).</summary>
    public ControlSwitchState Control => Loop.Control ?? throw new InvalidOperationException("The harness was built without a command port.");

    public ControlLoop Loop { get; }

    public SteppableTime Time { get; }

    public DateTimeOffset Now => Time.GetUtcNow();

    public SignalStore Store { get; }

    public ConfigurationResult Configuration { get; }

    public SignalSnapshotHolder Holder { get; }

    public GuardInbox Inbox => Loop.Inbox;

    public IModeSource Mode => Loop.Mode;

    public KillSwitchState KillSwitch => Loop.KillSwitch;

    public LockoutClocks Lockouts => Loop.Lockouts;

    public GuardEngine Engine => Loop.Engine;

    public EffectorGate Gate => Loop.Gate;

    public GuardStatusHolder Status => Loop.Status;

    public RecordingDecisionRecorder Recorder => Loop.Recorder;

    public GuardRunner Runner => Loop.Runner;

    /// <summary>Pushes states as Home Assistant would (reported now) and publishes one store cycle.</summary>
    public void Push(params (string Entity, string State, string? Unit)[] states)
    {
        Store.Apply(new EntityUpdateBatch(BatchOrigin.Change, [.. states.Select(s => new EntityUpdate(EntityId.Parse(s.Entity), s.State, s.Unit, Now, Now))], [], []), Now);
        Publish();
    }

    /// <summary>The reference devices on, the charger at 16 A, and the three meter currents.</summary>
    public void PushReference(double l1 = 8, double l2 = 8, double l3 = 8) => Push(
        ("switch.heater_1", "on", null),
        ("switch.heater_2", "on", null),
        ("switch.heater_3", "on", null),
        ("number.charger_current", "16", "A"),
        ("sensor.meter_current_a", Format(l1), "A"),
        ("sensor.meter_current_b", Format(l2), "A"),
        ("sensor.meter_current_c", Format(l3), "A"));

    public void Publish() => Holder.Publish(new SignalDiagnostics(Store.Snapshot(Now), Store.ConfigurationResult, [], []));

    private static string Format(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
