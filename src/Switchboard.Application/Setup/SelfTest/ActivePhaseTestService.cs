using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Configuration;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Signals;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Setup.SelfTest;

/// <summary>Why the active phase self-test cannot start now (a closed list; the wizard words each).</summary>
public enum ActiveTestRefusalCode
{
    /// <summary>The effective mode is not active (option, control switch, configuration or state).</summary>
    ControlNotActive,
    KillSwitch,
    SafeState,

    /// <summary>The device is not in the configuration in force (a draft device must be saved first).</summary>
    NotDeclared,

    /// <summary>Only relay devices (onoff, stepped) can be switched for the test.</summary>
    TypeNotSupported,

    /// <summary>The stage loads more than one phase: its step cannot point to one phase.</summary>
    MultiPhaseStage,

    /// <summary>The stage is on: the test switches a device that is off, so its last command is always an off.</summary>
    DeviceOn,

    /// <summary>The device's state is not known now.</summary>
    StateUnknown,

    /// <summary>The stage's min_off lockout still runs; the refusal says how long.</summary>
    LockoutMinOff,

    /// <summary>A phase (of the whole topology, any circuit group) has less headroom than the stage's rating plus the reserve.</summary>
    HeadroomInsufficient,

    /// <summary>The guard does not allow increases somewhere.</summary>
    IncreasesStopped,

    /// <summary>The device has a manual override.</summary>
    OverrideSet,

    /// <summary>The device is marked unresponsive (DISP-07).</summary>
    Unresponsive,

    /// <summary>Another test runs.</summary>
    TestRunning,

    /// <summary>A command window is still open (another change in flight would spoil the attribution).</summary>
    ChangeLimit,

    /// <summary>The meter's refresh is not detected yet: no step window, no verdict possible.</summary>
    RefreshUnknown,
}

/// <param name="Phase">The phase concerned (headroom_insufficient).</param>
/// <param name="Remaining">How long until the condition ends (lockout_min_off).</param>
public sealed record ActiveTestRefusal(ActiveTestRefusalCode Code, Phase? Phase = null, TimeSpan? Remaining = null);

/// <param name="ExpectedDuration">How long the test takes when it starts now: remaining min_off + reaction + max(min_on, window) + reaction + window; null without a step window.</param>
public sealed record ActiveTestPreconditions(bool Ok, ImmutableArray<ActiveTestRefusal> Refusals, TimeSpan? ExpectedDuration);

public enum ActiveTestState
{
    Running,
    Finished,
    Aborted,
}

public enum ActiveTestPhase
{
    /// <summary>Asked to switch on; waiting until the device reports on.</summary>
    PendingOn,

    /// <summary>On; kept on for max(min_on, window) so the meter sees the step.</summary>
    On,

    /// <summary>Asked to switch off; waiting until the device reports off.</summary>
    PendingOff,

    /// <summary>Off again; waiting for the window after the off before judging.</summary>
    Judging,

    Done,
}

public enum ActiveTestAbortReason
{
    /// <summary>The guard shed or reduced something during the test: protection wins, the test ends.</summary>
    GuardActed,
    SafeState,
    KillSwitch,
    IncreasesStopped,

    /// <summary>The device did not report the commanded state in time.</summary>
    NotConfirmed,
    Cancelled,

    /// <summary>The effective mode left active (control switched off, configuration invalid): the test's own off is its last command.</summary>
    ControlLeftActive,

    /// <summary>The device's declaration changed or disappeared.</summary>
    ConfigurationChanged,
}

/// <summary>One active self-test as the wizard reads it.</summary>
public sealed record ActiveTestView(
    Guid Id,
    DeviceName Device,
    int Stage,
    EntityId StateEntity,
    Phase ExpectedPhase,
    ActiveTestState State,
    ActiveTestPhase Phase,
    DateTimeOffset StartedAt,
    DateTimeOffset? OnAt,
    DateTimeOffset? OffAt,
    TimeSpan ExpectedDuration,
    PhaseJudgement? Judgement,
    ActiveTestAbortReason? AbortReason);

/// <summary>
/// The active phase self-test (CONN-05, maintainer survey 2026-10-25: "if we have headroom,
/// Switchboard may switch the device on and off to identify phases"). On demand only, for a relay
/// stage on one phase that is off now, only in effective active mode, with headroom of at least the
/// stage's rating left on every phase of the topology and every circuit group (the dispatch
/// envelope already keeps the reserve and the estimate out), its min_off over and nothing else in
/// flight. It asks the effector gate, through the self-test slot, to switch the stage on (an ordinary
/// increase: min_off and the guard's permission apply), keeps it on for max(min_on, window), switches
/// it off (min_on applies), and judges the evidence recorder's two active observations with the
/// passive self-test's classifier and its min_concordant rule (the on and the off). Protection
/// outranks it: when the guard acts on any phase, safe state or the kill switch come, increases stop
/// or the device does not confirm, the test aborts and, if it holds the device on, switches it off.
/// The device is always left off, as it was: after an abort the test keeps asking for the off until
/// the effector no longer intends the stage on and the device reports it off, bounded by the off's
/// own deadline (min_on, reaction, confirmation, one window) so dispatch is never held back for good.
/// Runs on the guard thread (a step listener) and the API.
/// </summary>
public sealed class ActivePhaseTestService : IActiveTestTransitions
{
    private readonly SelfTestSlot _slot;
    private readonly GuardInbox _inbox;
    private readonly PhaseEvidenceStore _evidence;
    private readonly ISetupRuntimeView _runtime;
    private readonly SetupDefaults _defaults;
    private readonly TimeProvider _time;
    private readonly ILogger<ActivePhaseTestService> _logger;
    private readonly object _gate = new();
    private readonly HashSet<(DeviceName Device, int Stage, DateTimeOffset At)> _switchings = [];
    private GuardStep? _last;
    private Test? _test;

    public ActivePhaseTestService(SelfTestSlot slot, GuardInbox inbox, PhaseEvidenceStore evidence, ISetupRuntimeView runtime, SetupDefaults defaults, SignalClock clock, ILogger<ActivePhaseTestService> logger)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _slot = slot;
        _inbox = inbox;
        _evidence = evidence;
        _runtime = runtime;
        _defaults = defaults;
        _time = clock.Time;
        _logger = logger;
    }

    /// <summary>Follows the guard runner (its steps drive the test). Call once.</summary>
    public void Attach(GuardRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        runner.Stepped += (_, step) => OnStepped(step);
    }

    public ActiveTestPreconditions Check(DeviceName device, int stage)
    {
        lock (_gate)
        {
            return Preconditions(device, stage, _time.GetUtcNow());
        }
    }

    /// <summary>Starts a test when every precondition holds; otherwise only the refusals come back.</summary>
    public (ActiveTestView? Test, ActiveTestPreconditions Preconditions) Start(DeviceName device, int stage)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var preconditions = Preconditions(device, stage, now);
            if (!preconditions.Ok || _runtime.Configuration?.Devices.FirstOrDefault(d => d.Id == device) is not { } definition)
            {
                return (null, preconditions);
            }

            var step = definition.Stages[stage - 1];
            var window = Window(definition, step, _runtime.Configuration!)!.Value;
            _test = new Test(Guid.NewGuid(), device, stage, step.StateEntity, step.Phases[0], now, preconditions.ExpectedDuration!.Value)
            {
                Window = window,
                OnDeadline = now + definition.ReactionTime + _runtime.Configuration!.Control.Effector.ConfirmTimeout + window,
            };
            _slot.Set(new SelfTestTarget(_test.Id, device, stage, On: true));
            _inbox.Post(GuardTrigger.SelfTest);
            _logger.LogWarning("Active phase self-test of {Device} stage {Stage} started: Switchboard switches it on and off (declared {Phase})", device, stage, step.Phases[0]);
            return (View(_test), preconditions);
        }
    }

    public ActiveTestView? Get(Guid id)
    {
        lock (_gate)
        {
            return _test?.Id == id ? View(_test) : null;
        }
    }

    public bool Cancel(Guid id)
    {
        lock (_gate)
        {
            if (_test?.Id != id)
            {
                return false;
            }

            if (_test.State == ActiveTestState.Running)
            {
                Abort(_test, ActiveTestAbortReason.Cancelled);
                _inbox.Post(GuardTrigger.SelfTest);
            }

            return true;
        }
    }

    public bool IsActive(DeviceName device, int stage, DateTimeOffset at)
    {
        lock (_gate)
        {
            return _switchings.Contains((device, stage, at));
        }
    }

    /// <summary>One guard step (guard thread): the test's state machine moves on, or aborts.</summary>
    public void OnStepped(GuardStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        lock (_gate)
        {
            _last = step;
            if (_test is not { } test)
            {
                return;
            }

            var now = step.Evaluation.At;
            var sample = step.Inputs?.Protection.DeviceStates.IsDefault == false
                ? step.Inputs.Protection.DeviceStates.FirstOrDefault(s => s.Device == test.Device && s.Stage == test.Stage)
                : null;
            bool? on = sample is { Fresh: true, Applied: RelayApplied relay } ? relay.On : null;
            if (test.State != ActiveTestState.Running)
            {
                if (test.Cleanup)
                {
                    Clean(step, test, on, now);
                }

                return;
            }

            var configuration = _runtime.Configuration;
            var definition = configuration?.Devices.FirstOrDefault(d => d.Id == test.Device);
            if (AbortReason(step, test, definition, now) is { } reason)
            {
                Abort(test, reason);
                _inbox.Post(GuardTrigger.SelfTest);
                return;
            }

            switch (test.Phase)
            {
                case ActiveTestPhase.PendingOn when on == true:
                    test.OnAt = sample!.ReportedAt ?? now;
                    _switchings.Add((test.Device, test.Stage, test.OnAt.Value));
                    test.OffDue = test.OnAt.Value + Max(definition!.MinOn, test.Window);
                    test.Phase = ActiveTestPhase.On;
                    break;
                case ActiveTestPhase.On when now >= test.OffDue:
                    _slot.Set(new SelfTestTarget(test.Id, test.Device, test.Stage, On: false));
                    test.OffDeadline = now + definition!.ReactionTime + configuration!.Control.Effector.ConfirmTimeout + definition.MinOn;
                    test.Phase = ActiveTestPhase.PendingOff;
                    _inbox.Post(GuardTrigger.SelfTest);
                    break;
                case ActiveTestPhase.PendingOff when on == false:
                    test.OffAt = sample!.ReportedAt ?? now;
                    _switchings.Add((test.Device, test.Stage, test.OffAt.Value));
                    test.JudgeAt = test.OffAt.Value + test.Window;
                    test.Phase = ActiveTestPhase.Judging;
                    break;
                case ActiveTestPhase.Judging when now >= test.JudgeAt:
                    var observations = _evidence.For(test.StateEntity)
                        .Where(o => o.Source == ObservationSource.Active && (o.At == test.OnAt || o.At == test.OffAt))
                        .ToList();
                    if (observations.Count < 2 && now < test.JudgeAt + test.Window)
                    {
                        // The recorder stores an observation once its window has passed: give it one more window.
                        break;
                    }

                    var stage = definition!.Stages[test.Stage - 1];
                    test.Judgement = PhaseEvidence.Judge(
                        observations,
                        test.ExpectedPhase,
                        stage.RatedPower?.Value,
                        stage.RatedCurrent.Value,
                        PhaseEvidence.Terms(configuration!, SignalRole.PhasePower),
                        PhaseEvidence.Terms(configuration!, SignalRole.PhaseCurrent),
                        _defaults.SelfTest.Classifier);
                    test.State = ActiveTestState.Finished;
                    test.Phase = ActiveTestPhase.Done;
                    _slot.Clear(test.Id);
                    _logger.LogWarning(
                        "Active phase self-test of {Device} finished: {Verdict}{Observed}",
                        test.Device,
                        test.Judgement.Summary.Verdict,
                        test.Judgement.Summary.Observed is { } observed ? $" (stepped on {observed})" : string.Empty);
                    break;
            }
        }
    }

    private ActiveTestAbortReason? AbortReason(GuardStep step, Test test, DeviceDefinition? definition, DateTimeOffset now)
    {
        var status = step.Evaluation.Status;
        if (definition is null || test.Stage > definition.Stages.Length || definition.Stages[test.Stage - 1].Phases.FirstOrDefault() != test.ExpectedPhase)
        {
            return ActiveTestAbortReason.ConfigurationChanged;
        }

        if (step.Mode is not { Effective: OperatingMode.Active })
        {
            return ActiveTestAbortReason.ControlLeftActive;
        }

        if (status.KillSwitch.Engaged)
        {
            return ActiveTestAbortReason.KillSwitch;
        }

        if (status.SafeState.Active)
        {
            return ActiveTestAbortReason.SafeState;
        }

        // Any guard shed or reduction anywhere: the declared phase may be the wrong one, so a guard
        // action on another phase can be the tested device's own load.
        if (step.Evaluation.Intents.Any(i => i.Target != GuardTarget.Released) || !step.Evaluation.Limits.IsEmpty)
        {
            return ActiveTestAbortReason.GuardActed;
        }

        // The guard stops increases somewhere for a reason of its own: data, band, budget, or a
        // reduction it asks for. The reserve part of "increases allowed" is left out on purpose:
        // the test's own step counts as in flight until its window closes, so with a reading that
        // already shows it the estimate holds the step twice for a moment (preconditions demanded
        // the stage's rating of headroom everywhere, without the step in flight).
        if (test.Phase is ActiveTestPhase.PendingOn or ActiveTestPhase.On
            && step.Evaluation.Envelope.Entries.Any(e => !e.DataValid || !e.OverrideAllowed || e.ReductionRequested))
        {
            return ActiveTestAbortReason.IncreasesStopped;
        }

        if (step.DispatchStatus?.Devices.FirstOrDefault(d => d.Id == test.Device) is { UnconfirmedSince: not null }
            || (test.Phase == ActiveTestPhase.PendingOn && now > test.OnDeadline)
            || (test.Phase == ActiveTestPhase.PendingOff && now > test.OffDeadline))
        {
            return ActiveTestAbortReason.NotConfirmed;
        }

        return null;
    }

    /// <summary>
    /// After an abort the slot asks for the off (deferred by min_on if need be) until the effector no
    /// longer intends the stage on and the device reports it off: released earlier, a switch-on the
    /// device had not reported yet would show later as the target already in force, and the off
    /// waiting for min_on would be dropped, leaving the load on. Past the cleanup deadline the slot is
    /// released anyway (a warning): the effector keeps re-sending an off Home Assistant refused, and
    /// dispatch is no longer held back. A device no longer declared is released at once.
    /// </summary>
    private void Clean(GuardStep step, Test test, bool? on, DateTimeOffset now)
    {
        var declared = _runtime.Configuration?.Devices.Any(d => d.Id == test.Device) == true;
        var intended = step.DispatchStatus?.Devices.FirstOrDefault(d => d.Id == test.Device)?.Intended;
        var intendedOn = intended is { Relays.IsDefault: false } level && test.Stage - 1 < level.Relays.Length && level.Relays[test.Stage - 1];
        if (declared && !(on == false && !intendedOn))
        {
            if (now < test.CleanupDeadline)
            {
                return;
            }

            _logger.LogWarning(
                "Active phase self-test of {Device}: the device did not report its stage off by {Deadline}; the test lets go of it so dispatch is not held back (an off Home Assistant refused keeps being sent)",
                test.Device,
                test.CleanupDeadline);
        }

        test.Cleanup = false;
        _slot.Clear(test.Id);
    }

    /// <summary>The test ends; if it may hold the device on, the slot asks for the off (the gate sends it unless protection already holds it).</summary>
    private void Abort(Test test, ActiveTestAbortReason reason)
    {
        test.State = ActiveTestState.Aborted;
        test.AbortReason = reason;
        if (test.Phase is ActiveTestPhase.PendingOn or ActiveTestPhase.On or ActiveTestPhase.PendingOff)
        {
            test.Cleanup = true;
            var now = _time.GetUtcNow();
            test.CleanupDeadline = _runtime.Configuration is { } configuration && configuration.Devices.FirstOrDefault(d => d.Id == test.Device) is { } definition
                ? now + definition.MinOn + definition.ReactionTime + configuration.Control.Effector.ConfirmTimeout + test.Window
                : now;
            _slot.Set(new SelfTestTarget(test.Id, test.Device, test.Stage, On: false));
        }
        else
        {
            _slot.Clear(test.Id);
        }

        test.Phase = ActiveTestPhase.Done;
        _logger.LogWarning("Active phase self-test of {Device} aborted: {Reason}; the device is switched off if the test had switched it on", test.Device, reason);
    }

    private ActiveTestPreconditions Preconditions(DeviceName device, int stage, DateTimeOffset now)
    {
        var refusals = ImmutableArray.CreateBuilder<ActiveTestRefusal>();
        var step = _last;
        var mode = step?.Mode;
        if (mode is not { Effective: OperatingMode.Active })
        {
            refusals.Add(new ActiveTestRefusal(ActiveTestRefusalCode.ControlNotActive));
        }

        if (step?.Evaluation.Status is { } status)
        {
            if (status.KillSwitch.Engaged)
            {
                refusals.Add(new ActiveTestRefusal(ActiveTestRefusalCode.KillSwitch));
            }

            if (status.SafeState.Active)
            {
                refusals.Add(new ActiveTestRefusal(ActiveTestRefusalCode.SafeState));
            }
        }

        if (_test is { State: ActiveTestState.Running } || (_test is { Cleanup: true }))
        {
            refusals.Add(new ActiveTestRefusal(ActiveTestRefusalCode.TestRunning));
        }

        if (step is null)
        {
            // Nothing evaluated yet: control_not_active (no mode resolved) already says why.
            return new ActiveTestPreconditions(false, refusals.ToImmutable(), null);
        }

        var configuration = _runtime.Configuration;
        if (configuration?.Devices.FirstOrDefault(d => d.Id == device) is not { } definition)
        {
            refusals.Add(new ActiveTestRefusal(ActiveTestRefusalCode.NotDeclared));
            return new ActiveTestPreconditions(false, refusals.ToImmutable(), null);
        }

        if (definition.Type is not (DeviceType.OnOff or DeviceType.Stepped) || stage < 1 || stage > definition.Stages.Length)
        {
            refusals.Add(new ActiveTestRefusal(ActiveTestRefusalCode.TypeNotSupported));
            return new ActiveTestPreconditions(false, refusals.ToImmutable(), null);
        }

        var stageDefinition = definition.Stages[stage - 1];
        if (stageDefinition.Phases.Length != 1)
        {
            refusals.Add(new ActiveTestRefusal(ActiveTestRefusalCode.MultiPhaseStage));
        }

        var reported = step.Evaluation.Envelope.Device(device)?.Reported;
        if (reported is null)
        {
            refusals.Add(new ActiveTestRefusal(ActiveTestRefusalCode.StateUnknown));
        }
        else if (!reported.Relays.IsDefault && stage - 1 < reported.Relays.Length && reported.Relays[stage - 1])
        {
            refusals.Add(new ActiveTestRefusal(ActiveTestRefusalCode.DeviceOn));
        }

        var dispatch = step.DispatchStatus?.Devices.FirstOrDefault(d => d.Id == device);
        var elapsed = now > step.Evaluation.At ? now - step.Evaluation.At : TimeSpan.Zero;
        var minOff = dispatch is null ? TimeSpan.Zero : dispatch.MinOffRemaining > elapsed ? dispatch.MinOffRemaining - elapsed : TimeSpan.Zero;
        if (minOff > TimeSpan.Zero)
        {
            refusals.Add(new ActiveTestRefusal(ActiveTestRefusalCode.LockoutMinOff, Remaining: minOff));
        }

        // Headroom of at least the stage's rating on every phase of the topology and every circuit
        // group phase (not only the declared one: the declaration is what the test checks).
        foreach (var entry in step.Evaluation.Envelope.Entries)
        {
            if (entry.Available is not { } available || available < stageDefinition.RatedCurrent)
            {
                refusals.Add(new ActiveTestRefusal(ActiveTestRefusalCode.HeadroomInsufficient, entry.Key.Phase));
            }
        }

        if (step.Evaluation.Envelope.Entries.Any(e => e.DataValid && !e.IncreasesAllowed) || !step.Evaluation.Limits.IsEmpty)
        {
            refusals.Add(new ActiveTestRefusal(ActiveTestRefusalCode.IncreasesStopped));
        }

        if (dispatch is not null)
        {
            if (dispatch.Override.Mode != OverrideMode.Auto)
            {
                refusals.Add(new ActiveTestRefusal(ActiveTestRefusalCode.OverrideSet));
            }

            if (dispatch.Unresponsive is not null)
            {
                refusals.Add(new ActiveTestRefusal(ActiveTestRefusalCode.Unresponsive));
            }
        }

        if (step.DispatchStatus?.Devices.Any(d => d.Settling == SettlingStatus.Open) == true)
        {
            refusals.Add(new ActiveTestRefusal(ActiveTestRefusalCode.ChangeLimit));
        }

        var window = Window(definition, stageDefinition, configuration);
        if (window is null)
        {
            refusals.Add(new ActiveTestRefusal(ActiveTestRefusalCode.RefreshUnknown));
        }

        TimeSpan? duration = window is { } w ? minOff + definition.ReactionTime + Max(definition.MinOn, w) + definition.ReactionTime + w : null;
        return new ActiveTestPreconditions(refusals.Count == 0, [.. refusals.DistinctBy(r => (r.Code, r.Phase))], duration);
    }

    /// <summary>The step window of the declared phase's meter signal (phase power when every phase has one, else phase current), as the recorder uses it.</summary>
    private TimeSpan? Window(DeviceDefinition device, StageDefinition stage, SwitchboardConfiguration configuration)
    {
        _ = device;
        var phases = configuration.Connection.Phases;
        var usePower = phases.All(p => configuration.Signals.Any(s => s.Role == SignalRole.PhasePower && s.Phase == p));
        var role = usePower ? SignalRole.PhasePower : SignalRole.PhaseCurrent;
        var signal = configuration.Signals.FirstOrDefault(s => s.Role == role && s.Phase == stage.Phases.FirstOrDefault());
        TimeSpan? refresh = signal is not null && _runtime.DetectedRefresh.TryGetValue(signal.Entity, out var detected) ? detected : null;
        return StepWindow.Window(_defaults.SelfTest.Isolation, configuration.Control.Signals.CurrentLagPolls, refresh);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;

    private static ActiveTestView View(Test test) => new(
        test.Id,
        test.Device,
        test.Stage,
        test.StateEntity,
        test.ExpectedPhase,
        test.State,
        test.Phase,
        test.StartedAt,
        test.OnAt,
        test.OffAt,
        test.ExpectedDuration,
        test.Judgement,
        test.AbortReason);

    private sealed class Test(Guid id, DeviceName device, int stage, EntityId stateEntity, Phase expectedPhase, DateTimeOffset startedAt, TimeSpan expectedDuration)
    {
        public Guid Id { get; } = id;

        public DeviceName Device { get; } = device;

        public int Stage { get; } = stage;

        public EntityId StateEntity { get; } = stateEntity;

        public Phase ExpectedPhase { get; } = expectedPhase;

        public DateTimeOffset StartedAt { get; } = startedAt;

        public TimeSpan ExpectedDuration { get; } = expectedDuration;

        public TimeSpan Window { get; init; }

        public DateTimeOffset OnDeadline { get; init; }

        public ActiveTestState State { get; set; } = ActiveTestState.Running;

        public ActiveTestPhase Phase { get; set; } = ActiveTestPhase.PendingOn;

        public DateTimeOffset? OnAt { get; set; }

        public DateTimeOffset OffDue { get; set; }

        public DateTimeOffset OffDeadline { get; set; }

        public DateTimeOffset? OffAt { get; set; }

        public DateTimeOffset JudgeAt { get; set; }

        public PhaseJudgement? Judgement { get; set; }

        public ActiveTestAbortReason? AbortReason { get; set; }

        /// <summary>Aborted while it may hold the device on: the slot asks for the off until the device reports it.</summary>
        public bool Cleanup { get; set; }

        /// <summary>The latest moment the cleanup holds the slot: the off's min_on, reaction time, confirmation timeout and one window after the abort.</summary>
        public DateTimeOffset CleanupDeadline { get; set; }
    }
}
