using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Signals;

namespace Switchboard.Tests.Support;

/// <summary>
/// The control loop as production composes it (guard, dispatch, effector gate without a command
/// port, kill switch, overrides, both status holders, a recording decision recorder), around a
/// given control input source and clock. A test that needs active semantics passes a recording
/// port, which reaches the gate through its test-only constructor.
/// </summary>
internal sealed class ControlLoop
{
    /// <param name="inbox">The guard's inbox, when the test built it first (a mode state that wakes the guard needs it).</param>
    public ControlLoop(IControlInputSource inputs, SignalClock clock, IModeSource mode, string boot, IDeviceCommandPort? port = null, LockoutClocks? lockouts = null, GuardInbox? inbox = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        Inbox = inbox ?? new GuardInbox();
        Lockouts = lockouts ?? new LockoutClocks();
        Mode = mode;
        Engine = new GuardEngine(Lockouts, Targets, NullLogger<GuardEngine>.Instance);
        var ids = new DecisionIdGenerator(boot);
        var settings = new EffectorSettings(20);
        Gate = port is null
            ? new EffectorGate(Lockouts, Targets, ids, settings, NullLogger<EffectorGate>.Instance)
            : new EffectorGate(Lockouts, Targets, ids, settings, NullLogger<EffectorGate>.Instance, port);
        KillSwitch = new KillSwitchState(Inbox, clock, NullLogger<KillSwitchState>.Instance);
        Overrides = new DeviceOverrides(Inbox, inputs, clock, NullLogger<DeviceOverrides>.Instance);
        var now = clock.Time.GetUtcNow();
        Status = new GuardStatusHolder(GuardStatus.Initial(now, mode.Current), NullLogger<GuardStatusHolder>.Instance);
        DispatchStatus = new DispatchStatusHolder(Switchboard.Application.Dispatch.DispatchStatus.Initial(now, mode.Current), NullLogger<DispatchStatusHolder>.Instance);
        Runner = new GuardRunner(Inbox, inputs, clock, Engine, Dispatch, Gate, Lockouts, Recorder, Status, DispatchStatus, mode, KillSwitch, Overrides, NullLogger<GuardRunner>.Instance);
    }

    public GuardInbox Inbox { get; }

    public IModeSource Mode { get; }

    public LockoutClocks Lockouts { get; }

    public DeviceTargets Targets { get; } = new();

    public GuardEngine Engine { get; }

    public DispatchEngine Dispatch { get; } = new();

    public EffectorGate Gate { get; }

    public KillSwitchState KillSwitch { get; }

    public DeviceOverrides Overrides { get; }

    public GuardStatusHolder Status { get; }

    public DispatchStatusHolder DispatchStatus { get; }

    public RecordingDecisionRecorder Recorder { get; } = new();

    public GuardRunner Runner { get; }
}
