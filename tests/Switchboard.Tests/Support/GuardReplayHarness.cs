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
/// The guard on recorded history (VER-04): the production signal pipeline of <see cref="ReplayHarness"/>
/// feeds the production guard runner, effector gate and a recording decision recorder, all on one
/// virtual clock. The clock jumps to whichever comes first: the next recorded row, the store's next
/// deadline or the guard's (a cycle boundary, a band timer, a budget threshold). Every protection
/// snapshot, guard step and decision is kept for the analysis.
/// </summary>
internal sealed class GuardReplayHarness
{
    private DateTimeOffset? _deadline;

    public GuardReplayHarness(ReplayData data, string yaml)
    {
        Replay = new ReplayHarness(data, yaml);
        var clock = new SignalClock(Replay.Time);
        var protection = (IProtectionInputSource)Replay.Holder;
        protection.Published += (_, inputs) => Observations.Add(inputs);
        Observations.Add(protection.Current);

        Mode = new ModeState(OperatingMode.Shadow, OperatingMode.Shadow);
        KillSwitch = new KillSwitchState(Inbox, clock, NullLogger<KillSwitchState>.Instance);
        Engine = new GuardEngine(Lockouts, NullLogger<GuardEngine>.Instance);
        Gate = new EffectorGate(Lockouts, new DecisionIdGenerator("replay"), NullLogger<EffectorGate>.Instance);
        Status = new GuardStatusHolder(GuardStatus.Initial(Replay.Now, Mode.Current), NullLogger<GuardStatusHolder>.Instance);
        Runner = new GuardRunner(Inbox, protection, clock, Engine, Gate, Lockouts, Recorder, Status, Mode, KillSwitch, NullLogger<GuardRunner>.Instance);
        Runner.Stepped += (_, step) => Steps.Add(step);
        StepGuard();
    }

    public ReplayHarness Replay { get; }

    public GuardInbox Inbox { get; } = new();

    public ModeState Mode { get; }

    public KillSwitchState KillSwitch { get; }

    public LockoutClocks Lockouts { get; } = new();

    public GuardEngine Engine { get; }

    public EffectorGate Gate { get; }

    public GuardStatusHolder Status { get; }

    public RecordingDecisionRecorder Recorder { get; } = new();

    public GuardRunner Runner { get; }

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

    private void StepGuard()
    {
        var now = Runner.Step();
        _deadline = Engine.NextDeadline(now);
    }
}
