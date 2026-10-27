using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Signals;

namespace Switchboard.Tests.Support;

/// <summary>
/// The control loop as production composes it (guard, dispatch, effector gate, kill switch,
/// overrides, both status holders, a recording decision recorder), around a given control input
/// source and clock. Without a port nothing can be commanded (shadow semantics); a test that needs
/// active semantics passes a recording port, and <see cref="WithControl"/> adds the real control
/// switch and mode state, as GuardService composes them.
/// </summary>
internal sealed class ControlLoop
{
    /// <param name="inbox">The guard's inbox, when the test built it first (a mode state that wakes the guard needs it).</param>
    /// <param name="gateLogger">The effector gate's logger, for a test that reads its warnings; silent otherwise.</param>
    public ControlLoop(
        IControlInputSource inputs,
        SignalClock clock,
        IModeSource mode,
        string boot,
        IDeviceCommandPort? port = null,
        LockoutClocks? lockouts = null,
        GuardInbox? inbox = null,
        KillSwitchState? killSwitch = null,
        ControlSwitchState? control = null,
        SelfTestSlot? selfTest = null,
        ILogger<EffectorGate>? gateLogger = null,
        Switchboard.Application.Planning.IPlanView? plan = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        Inbox = inbox ?? new GuardInbox();
        Lockouts = lockouts ?? new LockoutClocks();
        Dispatch = new DispatchEngine(TimeProvider.System, plan);
        Mode = mode;
        Port = port;
        SelfTest = selfTest;
        Engine = new GuardEngine(Lockouts, Targets, NullLogger<GuardEngine>.Instance);
        var ids = new DecisionIdGenerator(boot);
        var settings = new EffectorSettings(20);
        Gate = new EffectorGate(Lockouts, Targets, ids, settings, gateLogger ?? NullLogger<EffectorGate>.Instance, port, selfTest);
        KillSwitch = killSwitch ?? new KillSwitchState(Inbox, clock, NullLogger<KillSwitchState>.Instance);
        Control = control;
        Overrides = new DeviceOverrides(Inbox, inputs, clock, NullLogger<DeviceOverrides>.Instance);
        var now = clock.Time.GetUtcNow();
        Status = new GuardStatusHolder(GuardStatus.Initial(now, mode.Current), NullLogger<GuardStatusHolder>.Instance);
        DispatchStatus = new DispatchStatusHolder(Switchboard.Application.Dispatch.DispatchStatus.Initial(now, mode.Current), NullLogger<DispatchStatusHolder>.Instance);
        Runner = new GuardRunner(Inbox, inputs, clock, Engine, Dispatch, Gate, Lockouts, Recorder, Status, DispatchStatus, mode, KillSwitch, Overrides, NullLogger<GuardRunner>.Instance, control, selfTest);
    }

    public GuardInbox Inbox { get; }

    public IModeSource Mode { get; }

    public IDeviceCommandPort? Port { get; }

    public SelfTestSlot? SelfTest { get; }

    public LockoutClocks Lockouts { get; }

    public DeviceTargets Targets { get; } = new();

    public GuardEngine Engine { get; }

    public DispatchEngine Dispatch { get; }

    public EffectorGate Gate { get; }

    public KillSwitchState KillSwitch { get; }

    /// <summary>The control switch, when the loop was built <see cref="WithControl"/>.</summary>
    public ControlSwitchState? Control { get; }

    public DeviceOverrides Overrides { get; }

    public GuardStatusHolder Status { get; }

    public DispatchStatusHolder DispatchStatus { get; }

    public RecordingDecisionRecorder Recorder { get; } = new();

    public GuardRunner Runner { get; }

    /// <summary>
    /// The loop with the real control switch and mode state over <paramref name="inputs"/> (the
    /// configuration validity comes from them), as GuardService composes it; control starts off.
    /// </summary>
    public static ControlLoop WithControl(IControlInputSource inputs, SignalClock clock, OperatingMode ceiling, string boot, IDeviceCommandPort? port = null, SelfTestSlot? selfTest = null, ILogger<EffectorGate>? gateLogger = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var inbox = new GuardInbox();
        var killSwitch = new KillSwitchState(inbox, clock, NullLogger<KillSwitchState>.Instance);
        var control = new ControlSwitchState(inbox, killSwitch, clock, NullLogger<ControlSwitchState>.Instance);
        var mode = new ModeState(ceiling, control, inputs);
        return new ControlLoop(inputs, clock, mode, boot, port, inbox: inbox, killSwitch: killSwitch, control: control, selfTest: selfTest, gateLogger: gateLogger);
    }
}
