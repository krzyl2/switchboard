using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Signals;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Effector;

/// <param name="HistoryLength">Verifications and observations kept per device for the API (operational, appsettings).</param>
public sealed record EffectorSettings(int HistoryLength);

/// <summary>
/// The single place where an intent becomes a target and, while commands may leave, a command (03
/// EffectorGate). Per device and step it resolves, in this order: protection (kill switch, safe
/// state, guard holds: applied at once, outranking lockouts and change limits), the active phase
/// self-test, a manual override, then dispatch. It keeps the ledger (<see cref="DeviceTargets"/>):
/// the resolved target separate from the reported state (in shadow mode the intended state),
/// deferred intents (DISP-09: a change refused by a lockout is recorded once; a decrease is applied
/// when the lockout expires, an increase at the next cycle boundary with every gate re-checked, if
/// dispatch still wants it), settling windows (DISP-05), effector verification (DISP-07), the
/// confirmation of acknowledged commands and the restore baselines of start: restore_only. A
/// decision is recorded only when the resolved target changes, or once per deferral, so an
/// identical intent is never repeated (MODE-03).
/// <para>
/// Commands go to the command port only while the mode lets them leave (option active, control
/// allowed and a valid configuration; during an invalid configuration only the safe state's offs
/// and minimums to devices of the last valid document), only for a device of that setup (SAFE-05),
/// protective ones first. A decision with commands is recorded once every command has an outcome,
/// with its own id and time: commanded only when every one was acknowledged by Home Assistant.
/// </para>
/// </summary>
public sealed class EffectorGate
{
    private readonly LockoutClocks _lockouts;
    private readonly DeviceTargets _targets;
    private readonly ILogger<EffectorGate> _logger;
    private readonly IDeviceCommandPort? _port;
    private readonly SelfTestSlot? _selfTest;
    private readonly DecisionFactory _decisions;
    private readonly TicketTracker _tickets;
    private readonly SettlingVerifier _settling;
    private readonly RestoreBaselines _baselines;
    private readonly TransitionObserver _observer;
    private readonly Dictionary<(DeviceName Device, int? Stage), RecordedIntent> _recorded = [];
    private readonly Dictionary<DeviceName, (long Cycle, int Changes)> _changes = [];
    private long? _linkGeneration;
    private bool _activeLogged;

    /// <param name="port">The command port; without one (tests of shadow semantics) nothing can be commanded and every decision records shadow.</param>
    /// <param name="selfTest">The active phase self-test's slot; null where no test can run.</param>
    public EffectorGate(LockoutClocks lockouts, DeviceTargets targets, DecisionIdGenerator ids, EffectorSettings settings, ILogger<EffectorGate> logger, IDeviceCommandPort? port = null, SelfTestSlot? selfTest = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _lockouts = lockouts;
        _targets = targets;
        _logger = logger;
        _port = port;
        _selfTest = selfTest;
        _decisions = new DecisionFactory(ids, lockouts, port is not null);
        _tickets = new TicketTracker(targets, lockouts, logger);
        _settling = new SettlingVerifier(targets, _decisions, settings, logger);
        _baselines = new RestoreBaselines(targets, logger);
        _observer = new TransitionObserver(lockouts);
    }

    /// <summary>The ledger, for the dispatch status.</summary>
    public DeviceTargets Ledger => _targets;

    /// <summary>Decisions waiting for the outcome of their commands.</summary>
    public int PendingDecisions => _tickets.PendingCount;

    /// <summary>
    /// The mode decision (MODE-02), at every start and every change: what the add-on option, the
    /// control switch, the configuration and the state allow, and which condition is missing.
    /// </summary>
    public Decision ModeDecision(ModeStatus mode, ControlSwitchSource? source, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(mode);
        return _decisions.Mode(mode, source, now);
    }

    /// <summary>
    /// Dispatch entered its fault (dispatch_fault, recorded once per fault): it failed with an error, so the step applies the guard with no dispatch.
    /// Nothing is started, raised or lowered by dispatch until it evaluates again; protection holds, sheds and stops as always.
    /// </summary>
    public Decision DispatchFaultDecision(ModeStatus mode, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(mode);
        return _decisions.DispatchFault(mode, now);
    }

    /// <summary>
    /// The restore baselines and the devices dispatch started under start: headroom, as state.json
    /// held them at the last stop; applied before the first step. A baseline stands only while the
    /// device's first fresh report still shows the level it was shed to (otherwise the user acted
    /// while the add-on was down).
    /// </summary>
    public void Restore(ImmutableDictionary<DeviceName, StartBaseline> baselines, ImmutableDictionary<DeviceName, DateTimeOffset> selfStarted) =>
        _baselines.Restore(baselines, selfStarted);

    /// <summary>The commanded baselines and the devices started under start: headroom, for state.json (guard thread).</summary>
    public (ImmutableDictionary<DeviceName, StartBaseline> Baselines, ImmutableDictionary<DeviceName, DateTimeOffset> SelfStarted) Persisted() =>
        _baselines.Persisted();

    /// <summary>
    /// The mode's posture changed (the runner calls this before <see cref="Apply(GuardEvaluation, DispatchEvaluation?, OverrideSnapshot, ModeStatus, ControlInputs?, DateTimeOffset)"/>).
    /// Entering a commanding posture forgets the shadow world: every target starts unset, deferrals
    /// and windows end without a verdict, virtual baselines are dropped and the target lockouts
    /// restart now; the holds in force are then re-applied as protective changes and sent.
    /// Leaving it sends nothing and keeps the ledger, except the active self-test's own off.
    /// </summary>
    public void ModeChanged(ModeStatus? previous, ModeStatus next, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(next);
        var wasCommanding = previous?.Commands == true && _port is not null;
        var commanding = next.Commands && _port is not null;
        if (commanding && !wasCommanding)
        {
            foreach (var device in _targets.Devices)
            {
                _targets.TryGet(device, out var state);
                state.Unset();
                state.Deferrals.DeferredRecorded = false;
                state.Deferrals.LastDeferralRecord = null;
                state.Commands.ReassertPending = false;
                if (state.Baselines.Baseline is { Commanded: false } || state.Baselines.BaselinePending is { Commanded: false })
                {
                    state.Baselines.ClearBaseline();
                }
            }

            _recorded.Clear();
            _lockouts.RestartTargets(now);
            return;
        }

        if (wasCommanding && !commanding && _selfTest?.Current is { } test && _targets.TryGet(test.Device, out var tested)
            && tested is { Resolved.Origin: DecisionOrigin.SelfTest, Spec: { } spec } && tested.Resolved.Target is { } target && DeviceLevels.StageOn(spec, target, test.Stage))
        {
            // Control was switched off while the test's own switch-on still holds its device on
            // (running, or aborted with its off waiting for min_on): the test's off is the last
            // command, so no load Switchboard added for the test is left behind. This is the one
            // exception to "switching control off sends nothing", on the model of the final off of a
            // removed device Switchboard started (maintainer survey 2026-10-25).
            var off = WithStage(spec, target, test.Stage, on: false);
            if (tested.Deferrals.Deferred is { Origin: DecisionOrigin.SelfTest })
            {
                tested.Deferrals.Reset();
            }

            var ticket = _port!.Submit(new DeviceCommand(spec.Id, DeviceCommandKind.Relay, test.Stage, false, null, CommandPriority.Protective)
            {
                Revision = spec.ActuatorRevision,
                Decrease = true,
                Final = true,
            });
            _port.Flush();
            tested.Resolved.Target = off;
            _tickets.Track(ticket, spec.Id, CommandPriority.Protective, increase: false, target, null, null);
            tested.Commands.OpenTickets.Add(ticket.Sequence);
            _tickets.Pend(_decisions.SelfTestOff(spec, test.Stage, off, now), [ticket], null);
        }
    }

    /// <summary>
    /// The start of a step, before the guard: the outcomes of submitted commands are taken (a
    /// failed, timed-out or unsent command ends its window's relief at once, so the guard of this
    /// very step sees the measured current; a failed or unsent increase is taken back, so dispatch
    /// plans it again through every gate), then every open settling window whose closing set has
    /// reported after its required time (or whose hard timeout passed) is closed, so the guard's
    /// estimate already follows the measurement (DISP-06). A closed window is verified later in the
    /// same step (<see cref="Apply(GuardEvaluation, DispatchEvaluation?, OverrideSnapshot, ModeStatus, ControlInputs?, DateTimeOffset)"/>).
    /// </summary>
    public void BeginStep(ControlInputs? inputs, DateTimeOffset now)
    {
        if (inputs?.Protection.Setup is { } setup)
        {
            // A changed declaration starts unset before the guard and dispatch read the ledger: a
            // target of the old declaration (another stage count, another range) means nothing for
            // the new one. Removed devices leave the ledger in Apply, which records them.
            foreach (var device in setup.Devices)
            {
                if (_targets.TryGet(device.Id, out var known) && known.Spec is { } previous && !SpecIdentity.Same(previous, device))
                {
                    Redeclared(known, previous, device);
                }
            }
        }

        TakeResults(inputs, now);
        _settling.CloseWindows(inputs, now);
    }

    /// <summary>The guard's decisions alone (no dispatch in this step).</summary>
    public ImmutableArray<Decision> Apply(GuardEvaluation evaluation, ModeStatus mode, DateTimeOffset now) =>
        Apply(evaluation, null, OverrideSnapshot.None, mode, null, now);

    public ImmutableArray<Decision> Apply(GuardEvaluation evaluation, DispatchEvaluation? dispatch, OverrideSnapshot overrides, ModeStatus mode, ControlInputs? inputs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentNullException.ThrowIfNull(mode);
        if (mode.Commands && _port is null && !_activeLogged)
        {
            _logger.LogError("Active mode without a command path: nothing is commanded.");
            _activeLogged = true;
        }

        var decisions = ImmutableArray.CreateBuilder<Decision>();
        _tickets.MoveReady(decisions);
        var recordedMode = _decisions.Recorded(mode);
        foreach (var e in evaluation.Events)
        {
            decisions.Add(_decisions.FromGuard(e, recordedMode, now));
        }

        foreach (var e in dispatch?.Events ?? [])
        {
            decisions.Add(_decisions.FromDispatch(e, recordedMode, now));
        }

        if (_port is not null && _port.LinkGeneration != _linkGeneration)
        {
            // A new link to Home Assistant delivered its states: a protective target the device does
            // not show is re-sent once (commands lost while the link was down), and nothing else.
            if (_linkGeneration is not null)
            {
                foreach (var device in _targets.Devices)
                {
                    _targets.TryGet(device, out var state);
                    state.Commands.ReassertPending = true;
                }
            }

            _linkGeneration = _port.LinkGeneration;
        }

        var context = new StepContext(evaluation, dispatch, overrides, mode, inputs, now, decisions);
        if (evaluation.Setup is { } setup)
        {
            Sync(context, setup);
            _settling.MarkForeignSteps(context, setup);
            foreach (var device in setup.Devices.OrderBy(d => d.Id))
            {
                _settling.Settle(context, device, Dispatching(context, device, _targets.Get(device.Id)));
                RecordOverride(context, device);
                Resolve(context, device);
            }
        }
        else
        {
            foreach (var intent in evaluation.Intents)
            {
                if (Gate(intent, evaluation, recordedMode, now, null) is { } decision)
                {
                    decisions.Add(decision);
                }
            }
        }

        if (_port is not null)
        {
            // Protective commands leave first; a port that answers at once (the development sink, a
            // test double) has its outcomes recorded in this very step.
            _port.Flush();
            TakeResults(inputs, now);
            _tickets.MoveReady(decisions);
        }

        return decisions.ToImmutable();
    }

    /// <summary>
    /// The shutdown path, after the command path had its bounded wait: every outcome that arrived is
    /// taken, and a decision still waiting is recorded as not answered (device_unresponsive).
    /// </summary>
    public ImmutableArray<Decision> Drain(DateTimeOffset now)
    {
        TakeResults(null, now);
        _tickets.TimeOutUnanswered(now);
        return _tickets.TakeReady();
    }

    /// <summary>The next moment the gate must look again without new input: a settling timeout, a lockout expiry with a deferred change, a command's confirmation.</summary>
    public DateTimeOffset? NextDeadline(DateTimeOffset now)
    {
        DateTimeOffset? next = null;
        void Consider(DateTimeOffset? candidate)
        {
            if (candidate is { } at && at > now && (next is null || at < next))
            {
                next = at;
            }
        }

        foreach (var device in _targets.Devices)
        {
            _targets.TryGet(device, out var state);
            Consider(state.Settling.Window?.Times.Required);
            Consider(state.Settling.Window?.Times.Timeout);
            Consider(state.Deferrals.Deferred?.DueAt);
            Consider(state.Commands.ConfirmBy);
        }

        return next;
    }

    /// <summary>A device whose declaration changed starts unset; its restore baseline survives only when its levels still mean the same.</summary>
    private static void Redeclared(DeviceTargets.State state, DeviceSpec previous, DeviceSpec next)
    {
        state.Unset();
        state.LastReported = null;
        if (!SpecIdentity.SameLevelSpace(previous, next))
        {
            state.Baselines.ClearBaseline();
            state.Baselines.SelfStarted = null;
        }

        state.Spec = next;
    }

    /// <summary>
    /// The declared set follows the last valid setup: a device no longer declared leaves the ledger
    /// (it is never commanded again, SAFE-05, with one exception: a device dispatch had switched on
    /// under start: headroom gets one final off through its previous declaration). A device whose
    /// declaration changed starts unset, one that is unchanged keeps its target and deferral.
    /// </summary>
    private void Sync(StepContext context, ProtectionSetup setup)
    {
        foreach (var device in _targets.Devices.ToList())
        {
            if (setup.Device(device) is not null)
            {
                continue;
            }

            _targets.TryGet(device, out var gone);
            Removed(context, device, gone);
            _targets.Remove(device);
        }

        foreach (var device in setup.Devices)
        {
            var state = _targets.Get(device.Id);
            if (state.Spec is { } previous && !SpecIdentity.Same(previous, device))
            {
                Redeclared(state, previous, device);
            }

            state.Spec = device;
        }
    }

    private void Removed(StepContext context, DeviceName device, DeviceTargets.State gone)
    {
        var mode = _decisions.Recorded(context.Mode);
        if (gone.Spec is { } spec && gone.Baselines.SelfStarted is not null && gone.LastReported is { } last && !DeviceLevels.IsOff(spec, last)
            && spec.CanSwitchOff && _port is not null && context.Mode.Commands)
        {
            // Switchboard switched it on (start: headroom), so Switchboard switches it off once, through
            // the declaration it had (maintainer survey 2026-10-25); a restore to the user's own state
            // never gets this.
            var off = DeviceLevels.Off(spec);
            var tickets = Submit(spec, last, off, CommandPriority.Ordinary, retiring: true);
            var decision = _decisions.RemovedOff(spec, device, mode, context.Now, off);
            if (tickets.Count > 0)
            {
                _tickets.Pend(decision, tickets, null);
            }
            else
            {
                context.Decisions.Add(decision);
            }

            _logger.LogWarning("Device {Device} is no longer declared; Switchboard had switched it on, so it is switched off once", device);
            return;
        }

        var leftOn = gone.Resolved.Target is { } target && gone.Spec is { } declared && !DeviceLevels.IsOff(declared, target);
        if (leftOn || gone.Baselines.Baseline is not null || gone.Baselines.SelfStarted is not null)
        {
            context.Decisions.Add(_decisions.RemovedRelease(device, mode, context.Now, gone.Resolved.Target));
            _logger.LogWarning(
                "Device {Device} is no longer declared while {State}; it is not commanded again",
                device,
                gone.Baselines.Baseline is not null ? "left reduced by Switchboard" : leftOn ? $"targeted at {gone.Resolved.Target}" : "started by Switchboard");
        }
    }

    /// <summary>A manual override set or cleared is recorded once (manual_override); the target change that follows carries origin override.</summary>
    private void RecordOverride(StepContext context, DeviceSpec device)
    {
        var state = _targets.Get(device.Id);
        var current = context.Overrides.For(device.Id);
        if (current.Mode == state.Override)
        {
            return;
        }

        state.Override = current.Mode;
        var inputs = _decisions.DispatchInputs(context, device, state) with { Override = current.Mode };
        Record(context, device, DecisionAction.OverrideSet, ReasonCode.ManualOverride, DecisionOrigin.Override, state.Resolved.Target, [], null, inputs);
    }

    /// <summary>
    /// Resolution for one device: transitions first (a reported change that does not confirm the
    /// target means someone else switched it: the target follows, the target lockouts restart, a
    /// restore baseline ends), the command's confirmation and failures, then protection's holds,
    /// then the self-test, the override or dispatch wish, then a deferred change whose lockout
    /// expired. The guard's own intents keep their Stage 2 records.
    /// </summary>
    private void Resolve(StepContext context, DeviceSpec device)
    {
        var state = _targets.Get(device.Id);
        var load = context.Evaluation.Envelope.Device(device.Id);
        var reported = load?.Reported;
        _observer.Observe(context, device, state, reported);
        _baselines.Resolve(context, device, state, reported, CommandsFor(context, device));
        _settling.Confirm(context, device, state, reported, Dispatching(context, device, state));
        if (state.Settling.PendingMismatch)
        {
            state.Settling.PendingMismatch = false;
            _settling.CountMismatch(context, device, state, Dispatching(context, device, state));
        }

        var limits = context.Evaluation.Limits.GetValueOrDefault(device.Id) ?? ImmutableDictionary<int, StageLimit>.Empty;
        if (limits.Count == 0 && state.Resolved.Origin == DecisionOrigin.Guard)
        {
            // Protection let go of every hold. The guard never starts anything: the held level stays
            // the target and is handed to dispatch, which evaluated this very step against it. A
            // restore is then an ordinary recorded start or increase through start order,
            // confirmation, DISP-08 and min_off (DISP-03), in shadow mode exactly as in active mode,
            // where the reported state is the held level. Dropping the target instead would compare
            // dispatch's wish with a reported state it was not computed against. Only protection
            // running alone (no dispatch in the step: dispatch in its fault, the shutdown step, the
            // Stage 2 view, tests) drops it, so the target follows the reported state again.
            if (context.Dispatch?.Wish(device.Id) is not null)
            {
                state.Resolved.Origin = DecisionOrigin.Dispatch;
            }
            else
            {
                state.Resolved.Target = null;
                state.Resolved.TargetCommanded = false;
                state.Resolved.Origin = null;
                state.Resolved.Reason = null;
            }
        }

        var current = state.Resolved.Target ?? reported;
        var held = limits.Count > 0 ? DeviceLevels.Cap(device, current ?? DeviceLevels.Max(device), limits) : null;
        var protectiveChange = held is not null && (current is null || !held.Equals(current));

        // The guard's own records first, against the lockouts as they stood before its change (a
        // min_on it outranks is still running then), carrying the target its change leaves. Their
        // command outcome is known once the change below has been sent.
        var holdRecords = new List<Decision>();
        foreach (var intent in context.Evaluation.Intents.Where(i => i.Device == device.Id))
        {
            var forced = context.Overrides.For(device.Id).Mode == OverrideMode.ForceOn && intent.Target != GuardTarget.Released;
            var target = protectiveChange ? held : state.Resolved.Target;
            var willCommand = protectiveChange && intent.Target != GuardTarget.Released && CommandsFor(context, device);
            if (Gate(intent with { Overridden = forced ? ReasonCode.ManualOverride : intent.Overridden }, context.Evaluation, _decisions.Recorded(context.Mode), context.Now, target, willCommand) is { } decision)
            {
                holdRecords.Add(decision);
            }
        }

        var holdRecorded = holdRecords.Any(d => d.Action != DecisionAction.Release);
        if (protectiveChange)
        {
            var guardIntent = context.Evaluation.Intents.LastOrDefault(i => i.Device == device.Id && i.Target != GuardTarget.Released);
            var reason = guardIntent?.Reason ?? HoldReason(context, device);
            var from = current;
            var overridden = Outranked(context, device, from, held!);
            var tickets = Accept(context, device, state, held!, DecisionOrigin.Guard, reason, current, reported);
            foreach (var record in holdRecords)
            {
                if (record.Action != DecisionAction.Release && tickets.Count > 0)
                {
                    _tickets.Pend(record, tickets, null);
                }
                else
                {
                    context.Decisions.Add(record);
                }
            }

            if (!holdRecorded)
            {
                // A hold made earlier is re-asserted without a new guard intent (another controller
                // re-enabled the device, or its declaration changed and the target was reset): the
                // change, and in active mode its command, still carries its own record and reason.
                RecordHold(context, device, limits, from, held!, reason, overridden, tickets);
            }

            current = held;
        }
        else
        {
            context.Decisions.AddRange(holdRecords);
            Reassert(context, device, state, reported);
            Resend(context, device, state, reported);
        }

        if (limits.Count == 0 && SelfTestFor(device) is { } test)
        {
            // The active phase self-test outranks the override and dispatch, never protection: while
            // protection holds the device the test does nothing (the test service aborts).
            TestWant(context, device, state, test, current);
            return;
        }

        var wish = context.Dispatch?.Wish(device.Id);
        if (wish is { Fresh: true })
        {
            Want(context, device, state, wish, limits, current);
        }
        else if (state.Deferrals.Deferred is { DueAt: { } due } deferred && context.Now >= due)
        {
            // DISP-09 reconciliation: the lockout expired between boundaries; the last boundary's
            // wish stays valid until the next one. A deferred decrease is applied now. A deferred
            // increase waits for the next boundary, where dispatch wants it afresh with every gate
            // re-checked (the guard may have stopped increases, a window may have opened, the
            // increase budget may be spent since the wish was made); the lockout has expired, so it
            // is applied there at once, still carrying its deferral.
            if (deferred.Origin == DecisionOrigin.SelfTest)
            {
                state.Deferrals.Reset();
            }
            else if (wish is null || !wish.Level.Equals(deferred.Target))
            {
                state.Deferrals.Reset();
            }
            else if (DeviceLevels.Total(device, DeviceLevels.Cap(device, wish.Level, limits)) <= DeviceLevels.Total(device, current ?? DeviceLevels.Off(device)))
            {
                Want(context, device, state, wish with { Fresh = true }, limits, current);
            }
            else
            {
                state.Deferrals.Deferred = deferred with { DueAt = null };
            }
        }
    }

    private SelfTestTarget? SelfTestFor(DeviceSpec device) =>
        _selfTest?.Current is { } test && test.Device == device.Id && device is RelaySpec && test.Stage >= 1 && test.Stage <= device.Stages.Length ? test : null;

    /// <summary>
    /// The self-test's wish as an ordinary change (CONN-05): on needs the guard's permission on the
    /// device's constraints and respects min_off; off respects min_on. Origin self_test, reason
    /// manual_override (the user asked for it).
    /// </summary>
    private void TestWant(StepContext context, DeviceSpec device, DeviceTargets.State state, SelfTestTarget test, DeviceLevel? current)
    {
        if (current is null)
        {
            return;
        }

        var level = WithStage(device, current, test.Stage, test.On);
        if (level.Equals(current))
        {
            if (state.Deferrals.Deferred is { Origin: DecisionOrigin.SelfTest })
            {
                state.Deferrals.Reset();
            }

            return;
        }

        // Devices resolve only under a setup; without one the constraints are unknown and on has no permission.
        if (test.On && (context.Evaluation.Setup is not { } setup || !setup.ConstraintsOf(device).All(k => context.Evaluation.Envelope.For(k) is { IncreasesAllowed: true })))
        {
            return;
        }

        var inputs = _decisions.DispatchInputs(context, device, state);
        var change = test.On ? DispatchChange.Increase : DispatchChange.Decrease;
        Want(context, device, state, new DispatchWish(device.Id, level, ReasonCode.ManualOverride, DecisionOrigin.SelfTest, change, null, true, inputs), ImmutableDictionary<int, StageLimit>.Empty, current);
    }

    /// <summary>The reason of the guard's current hold on a device (its status), for a re-asserted hold.</summary>
    private static ReasonCode HoldReason(StepContext context, DeviceSpec device) =>
        context.Evaluation.Status.Devices.FirstOrDefault(d => d.Id == device.Id)?.Reason ?? ReasonCode.SafeState;

    /// <summary>What a re-asserted hold outranks, as a guard intent's record would name it: a forced-on override, else a running min_on on either timeline.</summary>
    private ReasonCode? Outranked(StepContext context, DeviceSpec device, DeviceLevel? from, DeviceLevel held)
    {
        if (context.Overrides.For(device.Id).Mode == OverrideMode.ForceOn)
        {
            return ReasonCode.ManualOverride;
        }

        var stopped = device.Stages.Where(s => from is null || (DeviceLevels.StageOn(device, from, s.Index) && !DeviceLevels.StageOn(device, held, s.Index)));
        return stopped.Any(s => _lockouts.MinOnRemaining(device.Id, s.Index, context.Now) > TimeSpan.Zero || _lockouts.MinOnRemaining(device.Id, s.Index, context.Now, LockoutTimeline.Target) > TimeSpan.Zero)
            ? ReasonCode.LockoutMinOn
            : null;
    }

    private void RecordHold(StepContext context, DeviceSpec device, ImmutableDictionary<int, StageLimit> limits, DeviceLevel? from, DeviceLevel held, ReasonCode reason, ReasonCode? overridden, IReadOnlyList<CommandTicket> tickets, bool reasserted = false)
    {
        var decision = _decisions.Hold(context, device, _targets.Get(device.Id), limits, from, held, reason, overridden, tickets.Count > 0, reasserted);
        if (tickets.Count > 0)
        {
            _tickets.Pend(decision, tickets, null);
        }
        else
        {
            context.Decisions.Add(decision);
        }
    }

    /// <summary>
    /// After a new Home Assistant link delivered its states: a protective or lowering target the
    /// device does not show yet is sent once more (its command was lost while the link was down).
    /// Never an increase: an increase is planned again through every gate.
    /// </summary>
    private void Reassert(StepContext context, DeviceSpec device, DeviceTargets.State state, DeviceLevel? reported)
    {
        if (!state.Commands.ReassertPending || reported is null)
        {
            return;
        }

        state.Commands.ReassertPending = false;
        if (state.Resolved.Target is not { } target || state.Resolved.Origin is not { } origin || reported.Equals(target)
            || DeviceLevels.Total(device, target) >= DeviceLevels.Total(device, reported) - DeviceLevel.SetpointTolerance)
        {
            return;
        }

        var priority = origin == DecisionOrigin.Guard ? CommandPriority.Protective : CommandPriority.Ordinary;
        var tickets = CommandChange(context, device, state, reported, target, priority);
        if (tickets.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Re-asserting {Device} at {Target} after Home Assistant came back", device.Id, target);
        RecordReasserted(context, device, state, reported, target, origin, tickets);
    }

    /// <summary>A protective or lowering target whose last command failed is sent again at every cycle boundary while the device still differs; recorded once per episode.</summary>
    private void Resend(StepContext context, DeviceSpec device, DeviceTargets.State state, DeviceLevel? reported)
    {
        if (!state.Commands.ProtectiveFailed || state.Resolved.Target is not { } target || reported is null)
        {
            return;
        }

        if (reported.Equals(target))
        {
            state.Commands.ProtectiveFailed = false;
            state.Commands.ResendRecorded = false;
            return;
        }

        if (!context.Evaluation.AtBoundary || state.Commands.OpenTickets.Count > 0)
        {
            return;
        }

        var tickets = CommandChange(context, device, state, reported, target, CommandPriority.Protective);
        if (tickets.Count == 0)
        {
            return;
        }

        if (!state.Commands.ResendRecorded)
        {
            state.Commands.ResendRecorded = true;
            RecordReasserted(context, device, state, reported, target, state.Resolved.Origin ?? DecisionOrigin.Guard, tickets);
        }
    }

    private void RecordReasserted(StepContext context, DeviceSpec device, DeviceTargets.State state, DeviceLevel from, DeviceLevel target, DecisionOrigin origin, IReadOnlyList<CommandTicket> tickets)
    {
        var inputs = _decisions.DispatchInputs(context, device, state) with { Reasserted = true };
        Record(context, device, DecisionFactory.ActionOf(device, from, target), state.Resolved.Reason ?? ReasonCode.SafeState, origin, target, tickets, null, inputs, DecisionFactory.ChangedStage(device, from, target));
    }

    /// <summary>Sends a change of the current target again without a new target: the ledger only learns of the new command's tickets.</summary>
    private List<CommandTicket> CommandChange(StepContext context, DeviceSpec device, DeviceTargets.State state, DeviceLevel from, DeviceLevel to, CommandPriority priority)
    {
        var tickets = Command(context, device, from, to, priority);
        foreach (var ticket in tickets)
        {
            _tickets.Track(ticket, device.Id, priority, DeviceLevels.Total(device, to) > DeviceLevels.Total(device, from), from, state.Resolved.Origin, state.Resolved.Reason);
            state.Commands.OpenTickets.Add(ticket.Sequence);
        }

        if (tickets.Count > 0)
        {
            state.Settling.CommandMismatchCounted = false;
        }

        return tickets;
    }

    /// <summary>An ordinary change (an override, dispatch or the self-test): lockouts on the target timeline, else applied.</summary>
    private void Want(StepContext context, DeviceSpec device, DeviceTargets.State state, DispatchWish wish, ImmutableDictionary<int, StageLimit> limits, DeviceLevel? current)
    {
        var level = DeviceLevels.Cap(device, wish.Level, limits);
        if (wish.BlockedBy is { } blocked)
        {
            // The deferral waits for the forced level, not for the level the device is already at.
            Defer(context, device, state, wish, DeviceLevels.Cap(device, wish.PendingLevel ?? wish.Level, limits), blocked, null);
            return;
        }

        var from = current ?? DeviceLevels.Off(device);
        if (level.Equals(from))
        {
            // Dispatch no longer wants the deferred change: dropped silently (the API shows it).
            state.Deferrals.Reset();
            return;
        }

        foreach (var stage in device.Stages)
        {
            var was = DeviceLevels.StageOn(device, from, stage.Index);
            var will = DeviceLevels.StageOn(device, level, stage.Index);
            if (was && !will && _lockouts.MinOnRemaining(device.Id, stage.Index, context.Now, LockoutTimeline.Target) is { } on && on > TimeSpan.Zero)
            {
                Defer(context, device, state, wish, level, ReasonCode.LockoutMinOn, context.Now + on);
                return;
            }

            if (!was && will && _lockouts.MinOffRemaining(device.Id, stage.Index, context.Now, LockoutTimeline.Target) is { } off && off > TimeSpan.Zero)
            {
                Defer(context, device, state, wish, level, ReasonCode.LockoutMinOff, context.Now + off);
                return;
            }
        }

        var deferredSince = state.Deferrals.Deferred is { } d && d.Target.Equals(level) ? d.Since : (DateTimeOffset?)null;
        state.Deferrals.Reset();
        var probe = state.Settling.UnresponsiveSince is not null && DeviceLevels.Total(device, level) > DeviceLevels.Total(device, from);

        // A start by Switchboard, not a restore: with a restore baseline pending the device was the
        // user's before Switchboard reduced it, and switching it back on gives the user's state back.
        var selfStart = wish.Origin == DecisionOrigin.Dispatch && device.Start == StartPolicy.Headroom && DeviceLevels.IsOff(device, from) && !DeviceLevels.IsOff(device, level)
            && state.Baselines.Baseline is null && state.Baselines.BaselinePending is null;
        var tickets = Accept(context, device, state, level, wish.Origin, wish.Reason, current, context.Evaluation.Envelope.Device(device.Id)?.Reported, probe);
        var inputs = wish.Inputs with
        {
            DeferredSince = deferredSince,
            Settling = SettlingState.Open,
            MinOnRemaining = _decisions.MaxRemaining(device, context.Now, on: true),
            MinOffRemaining = _decisions.MaxRemaining(device, context.Now, on: false),
        };
        Action<bool>? acknowledged = selfStart
            ? ok =>
            {
                if (ok && _targets.TryGet(device.Id, out var started) && started.Resolved.Target is { } now && !DeviceLevels.IsOff(device, now))
                {
                    started.Baselines.SelfStarted ??= context.Now;
                }
            }
            : null;
        Record(context, device, DecisionFactory.ActionOf(device, from, level), wish.Reason, wish.Origin, level, tickets, null, inputs, DecisionFactory.ChangedStage(device, from, level), acknowledged);
    }

    /// <summary>Stores a refused change and records it once per deferral.</summary>
    private void Defer(StepContext context, DeviceSpec device, DeviceTargets.State state, DispatchWish wish, DeviceLevel level, ReasonCode refusedBy, DateTimeOffset? dueAt)
    {
        if (state.Deferrals.Deferred is { } existing && existing.Target.Equals(level) && existing.RefusedBy == refusedBy && state.Deferrals.DeferredRecorded)
        {
            state.Deferrals.Deferred = existing with { DueAt = dueAt };
            return;
        }

        var since = state.Deferrals.Deferred is { } previous && previous.Target.Equals(level) ? previous.Since : context.Now;
        state.Deferrals.Deferred = new Deferral(level, wish.Reason, wish.Origin, refusedBy, since, dueAt);
        state.Deferrals.DeferredRecorded = true;
        if (dueAt is not null && state.Deferrals.LastDeferralRecord is { } last && last.Target.Equals(level) && last.RefusedBy == refusedBy && last.DueAt == dueAt)
        {
            // The same change refused by the same lockout episode was recorded already (dispatch
            // stopped wanting it for a while in between): once per deferral, not once per wish.
            return;
        }

        state.Deferrals.LastDeferralRecord = state.Deferrals.Deferred;
        var inputs = wish.Inputs with
        {
            DeferredSince = since,
            MinOnRemaining = _decisions.MaxRemaining(device, context.Now, on: true),
            MinOffRemaining = _decisions.MaxRemaining(device, context.Now, on: false),
        };
        Record(context, device, DecisionAction.Deferred, wish.Reason, wish.Origin, state.Resolved.Target, [], refusedBy, inputs);
    }

    /// <summary>A target change is accepted: the ledger, a restore baseline, a settling window, the target lockouts, and, while commands may leave, the commands.</summary>
    /// <param name="current">The level the change starts from; unknown (null) counts as off for an ordinary change and as possibly on for a protective one, which then commands every stage (D8).</param>
    /// <param name="reported">The device's fresh reported level: what a restore baseline records.</param>
    private List<CommandTicket> Accept(StepContext context, DeviceSpec device, DeviceTargets.State state, DeviceLevel level, DecisionOrigin origin, ReasonCode reason, DeviceLevel? current, DeviceLevel? reported, bool probe = false)
    {
        var from = current ?? (origin == DecisionOrigin.Guard ? DeviceLevels.Max(device) : DeviceLevels.Off(device));
        var increase = DeviceLevels.Total(device, level) > DeviceLevels.Total(device, from) + DeviceLevel.SetpointTolerance;
        var decrease = DeviceLevels.Total(device, level) < DeviceLevels.Total(device, from) - DeviceLevel.SetpointTolerance;
        var commanding = CommandsFor(context, device);
        _baselines.OnChange(context, device, state, origin, from, level, reported, increase, decrease, commanding);

        var priority = origin == DecisionOrigin.Guard ? CommandPriority.Protective : CommandPriority.Ordinary;
        var tickets = Command(context, device, from, level, priority);
        foreach (var ticket in tickets)
        {
            _tickets.Track(ticket, device.Id, priority, increase, from, state.Resolved.Origin, state.Resolved.Reason);
        }

        state.Commands.OpenTickets.Clear();
        state.Commands.OpenTickets.UnionWith(tickets.Select(t => t.Sequence));
        state.Resolved.Target = level;
        state.Resolved.TargetCommanded = commanding;
        state.Resolved.CommandRefuted = false;
        state.Settling.CommandMismatchCounted = false;
        state.Commands.ConfirmBy = null;
        state.Commands.UnconfirmedSince = null;
        state.Commands.ProtectiveFailed = false;
        state.Commands.ResendRecorded = false;
        state.Resolved.Origin = origin;
        state.Resolved.Reason = reason;
        state.Settling.TimedOut = false;
        state.Settling.Window = SettlingVerifier.OpenWindow(context, device, WindowKind.Command, from, level, commanding, probe);
        foreach (var stage in device.Stages)
        {
            if (DeviceLevels.StageOn(device, from, stage.Index) != DeviceLevels.StageOn(device, level, stage.Index) || current is null)
            {
                _lockouts.Commanded(device.Id, stage.Index, DeviceLevels.StageOn(device, level, stage.Index), context.Now);
            }
        }

        return tickets;
    }

    /// <summary>
    /// Submits the commands of a change, if commands may leave for this device: in active mode, for a
    /// device of the last valid setup (SAFE-05); during an invalid configuration only a change that
    /// switches off or lowers. Logical names and levels only: the port never gets an entity ID.
    /// </summary>
    private List<CommandTicket> Command(StepContext context, DeviceSpec device, DeviceLevel from, DeviceLevel to, CommandPriority priority)
    {
        if (!CommandsFor(context, device))
        {
            return [];
        }

        if ((!context.Evaluation.ConfigurationValid || context.Mode.DecreasesOnly) && DeviceLevels.Total(device, to) > DeviceLevels.Total(device, from) + DeviceLevel.SetpointTolerance)
        {
            return [];
        }

        return Submit(device, from, to, priority);
    }

    private List<CommandTicket> Submit(DeviceSpec device, DeviceLevel from, DeviceLevel to, CommandPriority priority, bool retiring = false)
    {
        var tickets = new List<CommandTicket>();
        void Send(DeviceCommandKind kind, int stage, bool on, Amperes? setpoint, bool decrease) =>
            tickets.Add(_port!.Submit(new DeviceCommand(device.Id, kind, stage, on, setpoint, priority)
            {
                Revision = device.ActuatorRevision,
                Decrease = decrease,
                Retiring = retiring,
            }));

        if (device is SetpointSpec setpointDevice)
        {
            if (to is SetpointLevel target && !(from is SetpointLevel unchanged && unchanged.Setpoint.Equals(target.Setpoint)) && !(retiring && setpointDevice.HasStopActuator))
            {
                Send(DeviceCommandKind.Setpoint, device.Stages[0].Index, true, target.Setpoint, from is not SetpointLevel before || target.Setpoint.Value < before.Setpoint.Value);
            }

            if (setpointDevice.HasStopActuator && from.Enabled != to.Enabled)
            {
                Send(DeviceCommandKind.Enable, device.Stages[0].Index, to.Enabled, null, !to.Enabled);
            }

            return tickets;
        }

        foreach (var stage in device.Stages)
        {
            var on = DeviceLevels.StageOn(device, to, stage.Index);
            if (DeviceLevels.StageOn(device, from, stage.Index) != on || from is not RelayLevel { Relays.IsDefault: false })
            {
                Send(DeviceCommandKind.Relay, stage.Index, on, null, !on);
            }
        }

        return tickets;
    }

    private bool CommandsFor(StepContext context, DeviceSpec device) =>
        _port is not null && context.Mode.Commands && context.Evaluation.Setup?.Device(device.Id) is not null;

    /// <summary>
    /// The outcomes that arrived: decisions waiting for them are completed (in order), and the
    /// device each command belongs to learns what happened to its current target's command.
    /// </summary>
    private void TakeResults(ControlInputs? inputs, DateTimeOffset now)
    {
        if (_port is null)
        {
            return;
        }

        while (_port.TryTakeResult(out var result))
        {
            _tickets.Take(result, inputs, now);
        }

        _tickets.CompleteAnswered();
    }

    /// <summary>A relay device's level with one stage switched on or off.</summary>
    private static DeviceLevel WithStage(DeviceSpec device, DeviceLevel level, int stage, bool on)
    {
        var relays = (level is RelayLevel { Relays.IsDefault: false } current && current.Relays.Length == device.Stages.Length ? current.Relays : [.. device.Stages.Select(_ => false)]).ToArray();
        relays[stage - 1] = on;
        return new RelayLevel([.. relays]);
    }

    /// <summary>Records a decision of dispatch's own kind about a device at the target the ledger holds, for the settling verifier.</summary>
    private RecordDispatchEvent Dispatching(StepContext context, DeviceSpec device, DeviceTargets.State state) =>
        (action, reason, inputs) => Record(context, device, action, reason, DecisionOrigin.Dispatch, state.Resolved.Target, [], null, inputs);

    /// <param name="tickets">The commands the decision carries: it is recorded once each has an outcome; none records it at once.</param>
    private void Record(StepContext context, DeviceSpec device, DecisionAction action, ReasonCode reason, DecisionOrigin origin, DeviceLevel? target, IReadOnlyList<CommandTicket> tickets, ReasonCode? refusedBy, DispatchDecisionInputs inputs, int? stage = null, Action<bool>? acknowledged = null)
    {
        var decision = _decisions.Device(context, device, action, reason, origin, target, tickets.Count > 0, refusedBy, inputs, stage);
        if (tickets.Count > 0 && refusedBy is null)
        {
            _tickets.Pend(decision, tickets, acknowledged);
        }
        else
        {
            context.Decisions.Add(decision);
        }
    }

    /// <summary>A guard intent's record (Stage 2): protective intents pass lockouts and say which they outrank; ordinary ones (tests) do not.</summary>
    /// <param name="willCommand">The protective change it makes will be sent: no mode_shadow refusal (the command's outcome completes the record).</param>
    private Decision? Gate(GuardIntent intent, GuardEvaluation evaluation, OperatingMode mode, DateTimeOffset now, DeviceLevel? target, bool willCommand = false)
    {
        var stage = intent.Stage ?? 1;
        var overridden = intent.Overridden;
        ReasonCode? refusedBy = null;
        if (intent.Urgency == IntentUrgency.Protective)
        {
            // Lockouts never outrank protection; the record says which one was outranked, on either
            // timeline (in shadow mode a would-started device is under its target min_on).
            if (intent.Target == GuardTarget.Off && overridden is null
                && (_lockouts.MinOnRemaining(intent.Device, stage, now) > TimeSpan.Zero || _lockouts.MinOnRemaining(intent.Device, stage, now, LockoutTimeline.Target) > TimeSpan.Zero))
            {
                overridden = ReasonCode.LockoutMinOn;
            }
        }
        else if (intent.Target == GuardTarget.Off && _lockouts.MinOnRemaining(intent.Device, stage, now) > TimeSpan.Zero)
        {
            refusedBy = ReasonCode.LockoutMinOn;
        }
        else if (intent.Target != GuardTarget.Released && !TakeChange(intent.Device, evaluation))
        {
            // Beyond max_step_change_stages this cycle: the producer proposes it again next cycle.
            // settling_window_open is the nearest code ("let the previous change settle first");
            // change_limit (v3) is defined for increases in flight only.
            refusedBy = ReasonCode.SettlingWindowOpen;
        }

        if (refusedBy is null && !willCommand && mode != OperatingMode.Active)
        {
            refusedBy = ReasonCode.ModeShadow;
        }

        var recorded = new RecordedIntent(intent.Target, intent.Reason, refusedBy);
        if (_recorded.TryGetValue((intent.Device, intent.Stage), out var last) && last == recorded)
        {
            return null;
        }

        _recorded[(intent.Device, intent.Stage)] = recorded;
        return _decisions.Intent(intent, overridden, refusedBy, mode, now, target);
    }

    /// <summary>Counts an ordinary stage change against the device's limit for this cycle; false when the limit is reached.</summary>
    private bool TakeChange(DeviceName device, GuardEvaluation evaluation)
    {
        var limit = evaluation.Timing?.MaxStepChangeStages ?? int.MaxValue;
        var (cycle, changes) = _changes.GetValueOrDefault(device, (evaluation.CycleIndex, 0));
        if (cycle != evaluation.CycleIndex)
        {
            changes = 0;
        }

        if (changes >= limit)
        {
            return false;
        }

        _changes[device] = (evaluation.CycleIndex, changes + 1);
        return true;
    }

    private readonly record struct RecordedIntent(GuardTarget Target, ReasonCode Reason, ReasonCode? RefusedBy);
}
