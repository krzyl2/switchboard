using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Decisions;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Signals;
using Switchboard.Domain.Signals;
using Switchboard.Infrastructure.Replay;

namespace Switchboard.Tests.Support;

/// <summary>
/// The control loop on recorded history (VER-04): the production signal pipeline of
/// <see cref="ReplayHarness"/> feeds the production guard runner (guard, dispatch, effector gate)
/// and a recording decision recorder, all on one virtual clock. The clock jumps to whichever comes
/// first: the next recorded row, the store's next deadline or the runner's (a cycle boundary, a band
/// timer, a budget threshold, a settling timeout). Every protection snapshot, step and decision is
/// kept for the analysis.
/// </summary>
internal sealed class GuardReplayHarness
{
    private DateTimeOffset? _deadline;

    /// <param name="dispatch">False runs protection alone (dispatch sees no setup and wants nothing): the Stage 2 view of a tier walk.</param>
    /// <param name="mode">Shadow unless a test drives active semantics (with <paramref name="port"/>, the recording double).</param>
    /// <param name="followSetpoints">
    /// A setpoint command shows in the device's state at once (the charger's number entity reports the
    /// value it was set to), which the recording cannot do; relays still never follow (an unresponsive effector).
    /// </param>
    /// <param name="selfTest">The self-test slot the gate and the runner share (the active phase self-test).</param>
    public GuardReplayHarness(ReplayData data, string yaml, bool dispatch = true, IModeSource? mode = null, IDeviceCommandPort? port = null, bool followSetpoints = false, SelfTestSlot? selfTest = null, Switchboard.Application.Planning.IPlanView? plan = null)
    {
        Replay = new ReplayHarness(data, yaml);
        var clock = new SignalClock(Replay.Time);
        var protection = (IProtectionInputSource)Replay.Holder;
        protection.Published += (_, inputs) => Observations.Add(inputs);
        Observations.Add(protection.Current);

        var inputs = (IControlInputSource)Replay.Holder;
        Loop = new ControlLoop(dispatch ? inputs : new GuardOnlyInputs(inputs), clock, mode ?? TestModes.State(OperatingMode.Shadow, false), "replay", port, selfTest: selfTest, plan: plan);
        Runner.Stepped += (_, step) => Steps.Add(step);
        if (followSetpoints && port is RecordingDeviceCommandPort recording)
        {
            var devices = TestConfigurations.Valid(yaml).Configuration!.Devices;
            recording.OnSubmit = command =>
            {
                if (command.Kind == DeviceCommandKind.Setpoint && devices.FirstOrDefault(d => d.Id == command.Device) is { } device)
                {
                    var value = command.Setpoint!.Value.Value / SignalUnits.Factor(device.RangeUnit ?? "A");
                    Replay.Source.Inject(device.Stages[0].StateEntity, value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                }
            };
        }

        StepGuard();
    }

    public ControlLoop Loop { get; }

    public ReplayHarness Replay { get; }

    public GuardInbox Inbox => Loop.Inbox;

    public IModeSource Mode => Loop.Mode;

    public KillSwitchState KillSwitch => Loop.KillSwitch;

    public LockoutClocks Lockouts => Loop.Lockouts;

    public GuardEngine Engine => Loop.Engine;

    public EffectorGate Gate => Loop.Gate;

    public GuardStatusHolder Status => Loop.Status;

    public RecordingDecisionRecorder Recorder => Loop.Recorder;

    public GuardRunner Runner => Loop.Runner;

    /// <summary>Every protection snapshot the store published, in order.</summary>
    public List<ProtectionInputs> Observations { get; } = [];

    public List<GuardStep> Steps { get; } = [];

    public IReadOnlyList<Decision> Decisions => Recorder.Decisions;

    public DateTimeOffset Now => Replay.Now;

    public void RunUntil(DateTimeOffset end) =>
        Replay.RunUntil(end, consumerDeadline: _ => _deadline, afterStep: () =>
        {
            if (Inbox.Reader.TryPeek(out _) || (_deadline is { } due && Now >= due))
            {
                StepGuard();
            }
        });

    /// <summary>The guard status at the step at or before <paramref name="at"/>.</summary>
    public GuardStatus StatusAt(DateTimeOffset at) => Steps.Last(s => s.Evaluation.At <= at).Evaluation.Status;

    /// <summary>The same control inputs without the dispatch setup: dispatch then wants nothing.</summary>
    private sealed class GuardOnlyInputs(IControlInputSource inner) : IControlInputSource
    {
        public ControlInputs Current => inner.Current with { DispatchSetup = null };

        public event EventHandler<ControlInputs>? Published
        {
            add => inner.Published += value;
            remove => inner.Published -= value;
        }
    }

    private void StepGuard()
    {
        var now = Runner.Step();
        _deadline = Runner.NextDeadline(now);
    }
}
