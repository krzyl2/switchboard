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
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Effector;

/// <param name="HistoryLength">Verifications and observations kept per device for the API (operational, appsettings).</param>
public sealed record EffectorSettings(int HistoryLength);

/// <summary>
/// The single place where an intent becomes a target, and, in active mode, a command (03
/// EffectorGate). Per device and step it resolves, in this order: protection (kill switch, safe
/// state, guard holds: applied at once, outranking lockouts and change limits), a manual override,
/// then dispatch. It keeps the ledger (<see cref="DeviceTargets"/>): the resolved target separate
/// from the reported state (in shadow mode the intended state), deferred intents (DISP-09: a change
/// refused by a lockout is recorded once; a decrease is applied when the lockout expires, an
/// increase at the next cycle boundary with every gate re-checked, if dispatch still wants it),
/// settling windows (DISP-05) and effector verification (DISP-07). A decision is
/// recorded only when the resolved target changes, or once per deferral, so an identical intent is
/// never repeated (MODE-03).
/// <para>
/// The public constructor holds no command port: production can command nothing (the maintainer's
/// rule). Only the internal constructor, reachable from the tests alone, accepts one; even then the
/// port is called only in active mode and only for a device of the last valid configuration, and
/// during an invalid configuration only to switch off (SAFE-05).
/// </para>
/// </summary>
public sealed class EffectorGate
{
    private readonly LockoutClocks _lockouts;
    private readonly DeviceTargets _targets;
    private readonly DecisionIdGenerator _ids;
    private readonly EffectorSettings _settings;
    private readonly ILogger<EffectorGate> _logger;
    private readonly IDeviceCommandPort? _port;
    private readonly Dictionary<(DeviceName Device, int? Stage), RecordedIntent> _recorded = [];
    private readonly Dictionary<DeviceName, (long Cycle, int Changes)> _changes = [];
    private bool _activeLogged;

    public EffectorGate(LockoutClocks lockouts, DeviceTargets targets, DecisionIdGenerator ids, EffectorSettings settings, ILogger<EffectorGate> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _lockouts = lockouts;
        _targets = targets;
        _ids = ids;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>For tests only (InternalsVisibleTo): the one way a command port reaches the gate.</summary>
    internal EffectorGate(LockoutClocks lockouts, DeviceTargets targets, DecisionIdGenerator ids, EffectorSettings settings, ILogger<EffectorGate> logger, IDeviceCommandPort port)
        : this(lockouts, targets, ids, settings, logger)
    {
        ArgumentNullException.ThrowIfNull(port);
        _port = port;
    }

    /// <summary>The ledger, for the dispatch status.</summary>
    public DeviceTargets Ledger => _targets;

    /// <summary>The mode decision recorded at every start: what was asked for, and what runs.</summary>
    public Decision Start(ModeStatus mode, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(mode);
        if (mode.Note == ModeNote.ActiveNotEnabled)
        {
            _logger.LogWarning("Active mode is allowed by the add-on option, but commanding is not enabled in this version: dispatch decisions are computed and logged, nothing is switched.");
        }

        return new Decision(
            _ids.Next(),
            now,
            DecisionScope.System,
            null,
            null,
            null,
            DecisionAction.ModeResolved,
            ReasonCode.ModeShadow,
            null,
            Recorded(mode),
            false,
            null,
            DecisionInputs.None with { Mode = new ModeDecisionInputs(mode.Ceiling, mode.UiChoice, mode.Note) });
    }

    /// <summary>
    /// The start of a step, before the guard: every open settling window whose closing set has
    /// reported after its required time (or whose hard timeout passed) is closed, so the guard's
    /// estimate of this very step already follows the measurement (DISP-06). The window is verified
    /// later in the same step (<see cref="Apply(GuardEvaluation, DispatchEvaluation?, OverrideSnapshot, ModeStatus, ControlInputs?, DateTimeOffset)"/>).
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
                    known.Unset();
                    known.LastReported = null;
                    known.Spec = device;
                }
            }
        }

        foreach (var device in _targets.Devices.ToList())
        {
            _targets.TryGet(device, out var state);
            if (state.Window is not { Open: true } window || state.Spec is not { } spec)
            {
                continue;
            }

            var dispatchDevice = inputs?.DispatchSetup?.Device(device);
            var outcome = SettlingRule.Evaluate(window.Times, ClosingSet(inputs, spec, dispatchDevice), now);
            if (outcome.State != SettlingState.Open)
            {
                state.Window = window with { Outcome = outcome.State };
            }
        }
    }

    /// <summary>The guard's decisions alone (no dispatch in this step).</summary>
    public ImmutableArray<Decision> Apply(GuardEvaluation evaluation, ModeStatus mode, DateTimeOffset now) =>
        Apply(evaluation, null, OverrideSnapshot.None, mode, null, now);

    public ImmutableArray<Decision> Apply(GuardEvaluation evaluation, DispatchEvaluation? dispatch, OverrideSnapshot overrides, ModeStatus mode, ControlInputs? inputs, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentNullException.ThrowIfNull(mode);
        if (mode.Effective == OperatingMode.Active && _port is null && !_activeLogged)
        {
            // Unreachable through ModeState in this version; refused structurally all the same.
            _logger.LogError("Active mode without a command path: nothing is commanded.");
            _activeLogged = true;
        }

        var decisions = ImmutableArray.CreateBuilder<Decision>();
        var recordedMode = Recorded(mode);
        foreach (var e in evaluation.Events)
        {
            decisions.Add(new Decision(_ids.Next(), now, e.Scope, null, null, e.Phase, e.Action, e.Reason, null, recordedMode, false, null, e.Inputs, group: e.Group));
        }

        foreach (var e in dispatch?.Events ?? [])
        {
            decisions.Add(new Decision(_ids.Next(), now, e.Scope, null, null, e.Phase, e.Action, e.Reason, null, recordedMode, false, null, e.Inputs, DecisionOrigin.Dispatch));
        }

        var context = new StepContext(evaluation, dispatch, overrides, mode, inputs, now, decisions);
        if (evaluation.Setup is { } setup)
        {
            Sync(context, setup);
            MarkForeignSteps(context, setup);
            foreach (var device in setup.Devices.OrderBy(d => d.Id))
            {
                Settle(context, device);
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

        return decisions.ToImmutable();
    }

    /// <summary>The next moment the gate must look again without new input: a settling timeout, a lockout expiry with a deferred change.</summary>
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
            Consider(state.Window?.Times.Required);
            Consider(state.Window?.Times.Timeout);
            Consider(state.Deferred?.DueAt);
        }

        return next;
    }

    private OperatingMode Recorded(ModeStatus mode) => mode.Effective == OperatingMode.Active && _port is not null ? OperatingMode.Active : OperatingMode.Shadow;

    /// <summary>
    /// The declared set follows the last valid setup: a device no longer declared leaves the ledger
    /// (it is never commanded again, SAFE-05; if it was targeted on, a decision says so), a device
    /// whose declaration changed starts unset, one that is unchanged keeps its target and deferral.
    /// </summary>
    private void Sync(StepContext context, ProtectionSetup setup)
    {
        foreach (var device in _targets.Devices.ToList())
        {
            if (setup.Device(device) is not null)
            {
                continue;
            }

            if (_targets.TryGet(device, out var gone) && gone.Target is { } target && gone.Spec is { } spec && !DeviceLevels.IsOff(spec, target))
            {
                context.Decisions.Add(new Decision(
                    _ids.Next(), context.Now, DecisionScope.Device, device, null, null, DecisionAction.Release, ReasonCode.SafeState, null,
                    Recorded(context.Mode), false, null, DecisionInputs.None, DecisionOrigin.Guard, null, target));
                _logger.LogWarning("Device {Device} is no longer declared while targeted at {Target}; it is not commanded again", device, target);
            }

            _targets.Remove(device);
        }

        foreach (var device in setup.Devices)
        {
            var state = _targets.Get(device.Id);
            if (state.Spec is { } previous && !SpecIdentity.Same(previous, device))
            {
                state.Unset();
                state.LastReported = null;
            }

            state.Spec = device;
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
        var inputs = DispatchInputs(context, device, state) with { Override = current.Mode };
        Record(context, device, DecisionAction.OverrideSet, ReasonCode.ManualOverride, DecisionOrigin.Override, state.Target, false, null, inputs);
    }

    /// <summary>A foreign load step on a phase makes an excess in the commanded direction of an open window inconclusive.</summary>
    private void MarkForeignSteps(StepContext context, ProtectionSetup setup)
    {
        var phases = context.Dispatch?.Events.Where(e => e.Action == DecisionAction.ForeignLoadDetected && e.Phase is not null).Select(e => e.Phase!.Value).ToHashSet() ?? [];
        if (phases.Count == 0)
        {
            return;
        }

        foreach (var device in setup.Devices.Where(d => d.Phases.Any(phases.Contains)))
        {
            var state = _targets.Get(device.Id);
            if (state.Window is { } window)
            {
                state.Window = window with { ForeignStep = true };
            }
        }
    }

    /// <summary>
    /// Settling (DISP-05, DISP-07): an open window closes once every balance input its change can
    /// move has reported after command time + reaction time (the device's own state is not in that
    /// set), or at the hard timeout. A closed command window that was applied is verified; in
    /// shadow mode nothing was applied (not applied). An observation window only adds history.
    /// </summary>
    private void Settle(StepContext context, DeviceSpec device)
    {
        var state = _targets.Get(device.Id);
        if (state.Window is not { Open: false, Outcome: { } outcome } window || context.DispatchSetup?.Device(device.Id) is not { } dispatchDevice)
        {
            return;
        }

        state.Window = null;
        var timedOut = outcome == SettlingState.TimedOut;
        var (verdict, expected, measured, unit) = timedOut
            ? (Verdict.TimedOut, 0.0, 0.0, MeasureUnit.Watts)
            : window.Kind == WindowKind.Command && !window.Applied ? (Verdict.NotApplied, 0.0, 0.0, MeasureUnit.Watts)
            : Verify(context, device, dispatchDevice, window);
        AddHistory(state, new VerificationRecord(context.Now, window.Kind == WindowKind.Command ? HistoryKind.Command : HistoryKind.Observed, expected, measured, unit, verdict)
        {
            CommandedAt = window.Times.CommandedAt,
            From = window.From,
            To = window.To,
        });
        if (window.Kind != WindowKind.Command)
        {
            return;
        }

        state.TimedOut = timedOut;
        var inputs = DispatchInputs(context, device, state) with
        {
            Verification = new VerificationInputs(expected, measured, unit, verdict),
            Settling = timedOut ? SettlingState.TimedOut : SettlingState.Closed,
        };
        if (timedOut)
        {
            // Lost sample: no mismatch is counted, the in-flight change ends, measurement rules.
            Record(context, device, DecisionAction.SettlingTimedOut, ReasonCode.SettlingWindowOpen, DecisionOrigin.Dispatch, state.Target, false, null, inputs);
        }

        if (!window.Applied)
        {
            return;
        }

        var settling = context.DispatchSetup!.Settling;
        switch (verdict)
        {
            case Verdict.Match:
                state.Mismatches = 0;
                if (state.UnresponsiveSince is not null && window.Probe)
                {
                    state.UnresponsiveSince = null;
                    state.RetryAt = null;
                    Record(context, device, DecisionAction.UnresponsiveCleared, ReasonCode.DeviceUnresponsive, DecisionOrigin.Dispatch, state.Target, false, null, inputs);
                }

                break;
            case Verdict.Mismatch:
                state.Mismatches++;
                state.CommandRefuted = window.To.Equals(state.Target);
                if (window.Probe || (state.UnresponsiveSince is null && state.Mismatches >= settling.MismatchLimit))
                {
                    // A failed probe re-marks at once, without counting again.
                    state.UnresponsiveSince = window.Probe ? state.UnresponsiveSince ?? context.Now : context.Now;
                    state.RetryAt = context.Now + settling.UnresponsiveRetryAfter;
                    Record(context, device, DecisionAction.MarkedUnresponsive, ReasonCode.DeviceUnresponsive, DecisionOrigin.Dispatch, state.Target, false, null, inputs);
                }

                break;
            default:
                if (window.Probe && state.UnresponsiveSince is not null)
                {
                    // An inconclusive probe proves nothing: the next one comes after the retry delay.
                    state.RetryAt = context.Now + settling.UnresponsiveRetryAfter;
                }

                break;
        }
    }

    /// <summary>
    /// The balance inputs a change on this device can move: per phase the main power and current, a
    /// sub-metered group current, and the device's own draws. Never the device's own state (a relay
    /// that reports only on change, or ignores the command, would hold the window to its timeout),
    /// never a generation input (an inverter asleep at night reports nothing).
    /// </summary>
    private static List<ClosingSignal> ClosingSet(ControlInputs? inputs, DeviceSpec device, DispatchDevice? dispatchDevice)
    {
        var set = new List<ClosingSignal>();
        foreach (var phase in device.Phases)
        {
            if (inputs?.Dispatch.For(phase) is { InvalidReason: not DispatchInvalidReason.PowerUnmapped } dispatch)
            {
                set.Add(new ClosingSignal(ClosingInput.PhasePower, phase, dispatch.PowerReportedAt));
            }

            set.Add(new ClosingSignal(ClosingInput.PhaseCurrent, phase, inputs?.Protection.For(phase)?.Current?.ReportedAt));
            if (device.CircuitGroup is { } group && inputs?.Protection.Setup?.Group(group) is { SubMetered: true } spec && spec.Phases.Contains(phase))
            {
                var meter = inputs.Protection.Groups.IsDefault ? null : inputs.Protection.Groups.FirstOrDefault(g => g.Group == group && g.Phase == phase);
                set.Add(new ClosingSignal(ClosingInput.PhaseCurrent, phase, meter?.Current?.ReportedAt));
            }

            if (device.HasCurrentDraw(phase))
            {
                var draw = inputs?.Protection.DeviceDraws is { IsDefault: false } draws ? draws.FirstOrDefault(d => d.Device == device.Id && d.Phase == phase) : null;
                set.Add(new ClosingSignal(ClosingInput.DrawCurrent, phase, draw?.ReportedAt));
            }

            if (dispatchDevice?.HasPowerDraw(phase) == true)
            {
                var power = inputs?.Dispatch.DevicePowers is { IsDefault: false } powers ? powers.FirstOrDefault(d => d.Device == device.Id && d.Phase == phase) : null;
                set.Add(new ClosingSignal(ClosingInput.DrawPower, phase, power?.ReportedAt));
            }
        }

        return set;
    }

    /// <summary>
    /// DISP-07: the device's own draw first (A, else W); a relay without one against the phase's
    /// house load (net import plus generation share, so clouds do not count) and its rated power; a
    /// setpoint device without a draw cannot be verified. A setpoint is a ceiling: a decrease must
    /// bring the draw down to it, an increase the car does not take is inconclusive.
    /// </summary>
    private static (Verdict Verdict, double Expected, double Measured, MeasureUnit Unit) Verify(StepContext context, DeviceSpec device, DispatchDevice dispatchDevice, OpenWindow window)
    {
        var settling = context.DispatchSetup!.Settling;
        var phases = device.Phases;
        if (phases.All(device.HasCurrentDraw))
        {
            var now = phases.Select(p => DrawSample(context, device.Id, p) is { Fresh: true, Current: { } c } ? c.Value : (double?)null).ToList();
            if (now.Any(v => v is null) || (!device.IsSetpoint && phases.Any(p => window.Before.GetValueOrDefault(p)?.DrawCurrent is null)))
            {
                return (Verdict.Inconclusive, 0, 0, MeasureUnit.Amperes);
            }

            var ceilingTo = phases.Sum(p => DeviceLevels.Ceiling(device, window.To, p).Value);
            var ceilingFrom = phases.Sum(p => DeviceLevels.Ceiling(device, window.From, p).Value);
            var tolerance = Verification.Tolerance(ceilingTo - ceilingFrom, settling.VerificationTolerance, settling.VerificationSigmaMultiplier, dispatchDevice.CurrentDrawSigma.Value);
            if (device.IsSetpoint)
            {
                var drawn = now.Sum(v => v!.Value);
                var kind = ceilingTo < ceilingFrom ? VerificationKind.SetpointDecrease : VerificationKind.SetpointIncrease;
                return (Verification.Judge(new VerificationCase(kind, ceilingTo, drawn, tolerance, window.ForeignStep)), ceilingTo, drawn, MeasureUnit.Amperes);
            }

            var measured = phases.Select((p, i) => now[i]!.Value - window.Before[p].DrawCurrent!.Value.Value).Sum();
            var expected = ceilingTo - ceilingFrom;
            return (Verification.Judge(new VerificationCase(VerificationKind.Step, expected, measured, tolerance, window.ForeignStep)), expected, measured, MeasureUnit.Amperes);
        }

        if (device.IsSetpoint)
        {
            return (Verdict.Unverifiable, 0, 0, MeasureUnit.Amperes);
        }

        var expectedW = phases.Sum(p => dispatchDevice.RatedPower(window.To, p).Value - dispatchDevice.RatedPower(window.From, p).Value);
        if (phases.All(dispatchDevice.HasPowerDraw))
        {
            var after = phases.Select(p => PowerSample(context, device.Id, p) is { Fresh: true, Power: { } w } ? w.Value : (double?)null).ToList();
            if (after.Any(v => v is null) || phases.Any(p => window.Before.GetValueOrDefault(p)?.DrawPower is null))
            {
                return (Verdict.Inconclusive, expectedW, 0, MeasureUnit.Watts);
            }

            var measuredW = phases.Select((p, i) => after[i]!.Value - window.Before[p].DrawPower!.Value.Value).Sum();
            var toleranceW = Verification.Tolerance(expectedW, settling.VerificationTolerance, settling.VerificationSigmaMultiplier, dispatchDevice.PowerDrawSigma.Value);
            return (Verification.Judge(new VerificationCase(VerificationKind.Step, expectedW, measuredW, toleranceW, window.ForeignStep)), expectedW, measuredW, MeasureUnit.Watts);
        }

        var loads = phases.Select(p => context.Inputs?.Dispatch.For(p) is { DataValid: true, Load: { } l } ? l.Value : (double?)null).ToList();
        if (loads.Any(v => v is null) || phases.Any(p => window.Before.GetValueOrDefault(p)?.Load is null))
        {
            return (Verdict.Inconclusive, expectedW, 0, MeasureUnit.Watts);
        }

        var measuredLoad = phases.Select((p, i) => loads[i]!.Value - window.Before[p].Load!.Value.Value).Sum();
        var sigma = phases.Sum(p => context.DispatchSetup!.LoadSigma.GetValueOrDefault(p).Value);
        var toleranceLoad = Verification.Tolerance(expectedW, settling.VerificationTolerance, settling.VerificationSigmaMultiplier, sigma);
        return (Verification.Judge(new VerificationCase(VerificationKind.Step, expectedW, measuredLoad, toleranceLoad, window.ForeignStep)), expectedW, measuredLoad, MeasureUnit.Watts);
    }

    /// <summary>
    /// Resolution for one device: transitions first (a reported change that does not confirm the
    /// target means someone else switched it: the target follows, the target lockouts restart), then
    /// protection's holds, then the override or dispatch wish, then a deferred change whose lockout
    /// expired. The guard's own intents keep their Stage 2 records.
    /// </summary>
    private void Resolve(StepContext context, DeviceSpec device)
    {
        var state = _targets.Get(device.Id);
        var load = context.Evaluation.Envelope.Device(device.Id);
        var reported = load?.Reported;
        ObserveTransition(context, device, state, reported);

        var limits = context.Evaluation.Limits.GetValueOrDefault(device.Id) ?? ImmutableDictionary<int, StageLimit>.Empty;
        if (limits.Count == 0 && state.Origin == DecisionOrigin.Guard)
        {
            // Protection let go of every hold. The guard never starts anything: the held level stays
            // the target and is handed to dispatch, which evaluated this very step against it. A
            // restore is then an ordinary recorded start or increase through start order,
            // confirmation, DISP-08 and min_off (DISP-03), in shadow mode exactly as in active mode,
            // where the reported state is the held level. Dropping the target instead would compare
            // dispatch's wish with a reported state it was not computed against. Only protection
            // running alone (no dispatch in the step: the Stage 2 view, tests) drops it, so the
            // target follows the reported state again.
            if (context.Dispatch?.Wish(device.Id) is not null)
            {
                state.Origin = DecisionOrigin.Dispatch;
            }
            else
            {
                state.Target = null;
                state.TargetCommanded = false;
                state.Origin = null;
                state.Reason = null;
            }
        }

        var current = state.Target ?? reported;
        var held = limits.Count > 0 ? DeviceLevels.Cap(device, current ?? DeviceLevels.Max(device), limits) : null;
        var protectiveChange = held is not null && (current is null || !held.Equals(current));

        // The guard's own records first, against the lockouts as they stood before its change (a
        // min_on it outranks is still running then), carrying the target its change leaves.
        var holdRecorded = false;
        foreach (var intent in context.Evaluation.Intents.Where(i => i.Device == device.Id))
        {
            var forced = context.Overrides.For(device.Id).Mode == OverrideMode.ForceOn && intent.Target != GuardTarget.Released;
            var target = protectiveChange ? held : state.Target;
            if (Gate(intent with { Overridden = forced ? ReasonCode.ManualOverride : intent.Overridden }, context.Evaluation, Recorded(context.Mode), context.Now, target, protectiveChange && intent.Target != GuardTarget.Released && CommandedFor(context, device)) is { } decision)
            {
                context.Decisions.Add(decision);
                holdRecorded |= intent.Target != GuardTarget.Released;
            }
        }

        if (protectiveChange)
        {
            var guardIntent = context.Evaluation.Intents.LastOrDefault(i => i.Device == device.Id && i.Target != GuardTarget.Released);
            var reason = guardIntent?.Reason ?? HoldReason(context, device);
            var from = current;
            var overridden = Outranked(context, device, from, held!);
            Accept(context, device, state, held!, DecisionOrigin.Guard, reason, current, out var commanded);
            if (!holdRecorded)
            {
                // A hold made earlier is re-asserted without a new guard intent (another controller
                // re-enabled the device, or its declaration changed and the target was reset): the
                // change, and in active mode its command, still carries its own record and reason.
                RecordHold(context, device, limits, from, held!, reason, overridden, commanded);
            }

            current = held;
        }

        var wish = context.Dispatch?.Wish(device.Id);
        if (wish is { Fresh: true })
        {
            Want(context, device, state, wish, limits, current);
        }
        else if (state.Deferred is { DueAt: { } due } deferred && context.Now >= due)
        {
            // DISP-09 reconciliation: the lockout expired between boundaries; the last boundary's
            // wish stays valid until the next one. A deferred decrease is applied now. A deferred
            // increase waits for the next boundary, where dispatch wants it afresh with every gate
            // re-checked (the guard may have stopped increases, a window may have opened, the
            // increase budget may be spent since the wish was made); the lockout has expired, so it
            // is applied there at once, still carrying its deferral.
            if (wish is null || !wish.Level.Equals(deferred.Target))
            {
                state.Deferred = null;
                state.DeferredRecorded = false;
            }
            else if (DeviceLevels.Total(device, DeviceLevels.Cap(device, wish.Level, limits)) <= DeviceLevels.Total(device, current ?? DeviceLevels.Off(device)))
            {
                Want(context, device, state, wish with { Fresh = true }, limits, current);
            }
            else
            {
                state.Deferred = deferred with { DueAt = null };
            }
        }
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

        var stopped = device.Stages.Where(s => from is null || (StageOn(device, from, s.Index) && !StageOn(device, held, s.Index)));
        return stopped.Any(s => _lockouts.MinOnRemaining(device.Id, s.Index, context.Now) > TimeSpan.Zero || _lockouts.MinOnRemaining(device.Id, s.Index, context.Now, LockoutTimeline.Target) > TimeSpan.Zero)
            ? ReasonCode.LockoutMinOn
            : null;
    }

    private void RecordHold(StepContext context, DeviceSpec device, ImmutableDictionary<int, StageLimit> limits, DeviceLevel? from, DeviceLevel held, ReasonCode reason, ReasonCode? overridden, bool commanded)
    {
        var action = limits.Values.Any(l => l == StageLimit.Off) ? DecisionAction.SwitchOff : DecisionAction.ReduceToMinimum;
        context.Decisions.Add(new Decision(
            _ids.Next(),
            context.Now,
            DecisionScope.Device,
            device.Id,
            from is null ? null : ChangedStage(device, from, held),
            device.Phases[0],
            action,
            reason,
            overridden,
            Recorded(context.Mode),
            commanded,
            commanded || Recorded(context.Mode) == OperatingMode.Active ? null : ReasonCode.ModeShadow,
            DecisionInputs.None with { Dispatch = DispatchInputs(context, device, _targets.Get(device.Id)) },
            DecisionOrigin.Guard,
            device.CircuitGroup,
            held));
    }

    private void ObserveTransition(StepContext context, DeviceSpec device, DeviceTargets.State state, DeviceLevel? reported)
    {
        if (reported is null)
        {
            return;
        }

        var previous = state.LastReported;
        state.LastReported = reported;
        if (previous is null || previous.Equals(reported) || reported.Equals(state.Target))
        {
            return;
        }

        // Someone else switched the device, and the step is observed for the history either way.
        // Where the intended state follows the real one (no target set, or active mode, which does
        // not fight another controller) its contacts moved: the target lockouts restart and a set
        // target follows. In shadow mode a set target is the would-be world and stays as it is: the
        // same intent is never recorded again because another controller undid it in reality.
        var followsReality = state.Target is null || context.Mode.Effective == OperatingMode.Active;
        if (followsReality)
        {
            foreach (var stage in device.Stages)
            {
                var was = StageOn(device, previous, stage.Index);
                var now = StageOn(device, reported, stage.Index);
                if (was != now)
                {
                    _lockouts.Switched(device.Id, stage.Index, now, context.Now);
                }
            }

            if (state.Target is not null)
            {
                state.Target = reported;
                state.Origin = null;
                state.Reason = null;
                state.TargetCommanded = false;
            }
        }

        if (state.Window is null or { Kind: WindowKind.Observation })
        {
            state.Window = OpenWindow(context, device, WindowKind.Observation, previous, reported, applied: false, probe: false);
        }
        else if (followsReality)
        {
            state.Window = null;
        }
    }

    /// <summary>An ordinary change (an override or dispatch): lockouts on the target timeline, else applied.</summary>
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
            state.Deferred = null;
            state.DeferredRecorded = false;
            return;
        }

        foreach (var stage in device.Stages)
        {
            var was = StageOn(device, from, stage.Index);
            var will = StageOn(device, level, stage.Index);
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

        var deferredSince = state.Deferred is { } d && d.Target.Equals(level) ? d.Since : (DateTimeOffset?)null;
        state.Deferred = null;
        state.DeferredRecorded = false;
        var probe = state.UnresponsiveSince is not null && DeviceLevels.Total(device, level) > DeviceLevels.Total(device, from);
        Accept(context, device, state, level, wish.Origin, wish.Reason, current, out var commanded, probe);
        var inputs = wish.Inputs with
        {
            DeferredSince = deferredSince,
            Settling = SettlingState.Open,
            MinOnRemaining = MaxRemaining(device, context.Now, on: true),
            MinOffRemaining = MaxRemaining(device, context.Now, on: false),
        };
        Record(context, device, ActionOf(device, from, level), wish.Reason, wish.Origin, level, commanded, null, inputs, ChangedStage(device, from, level));
    }

    /// <summary>Stores a refused change and records it once per deferral.</summary>
    private void Defer(StepContext context, DeviceSpec device, DeviceTargets.State state, DispatchWish wish, DeviceLevel level, ReasonCode refusedBy, DateTimeOffset? dueAt)
    {
        if (state.Deferred is { } existing && existing.Target.Equals(level) && existing.RefusedBy == refusedBy && state.DeferredRecorded)
        {
            state.Deferred = existing with { DueAt = dueAt };
            return;
        }

        var since = state.Deferred is { } previous && previous.Target.Equals(level) ? previous.Since : context.Now;
        state.Deferred = new Deferral(level, wish.Reason, wish.Origin, refusedBy, since, dueAt);
        state.DeferredRecorded = true;
        if (dueAt is not null && state.LastDeferralRecord is { } last && last.Target.Equals(level) && last.RefusedBy == refusedBy && last.DueAt == dueAt)
        {
            // The same change refused by the same lockout episode was recorded already (dispatch
            // stopped wanting it for a while in between): once per deferral, not once per wish.
            return;
        }

        state.LastDeferralRecord = state.Deferred;
        var inputs = wish.Inputs with
        {
            DeferredSince = since,
            MinOnRemaining = MaxRemaining(device, context.Now, on: true),
            MinOffRemaining = MaxRemaining(device, context.Now, on: false),
        };
        Record(context, device, DecisionAction.Deferred, wish.Reason, wish.Origin, state.Target, false, refusedBy, inputs);
    }

    /// <summary>A target change is accepted: the ledger, a settling window, the target lockouts, and in active mode the command.</summary>
    /// <param name="current">The level the change starts from; unknown (null) counts as off for an ordinary change and as possibly on for a protective one, which then commands every stage (D8).</param>
    private void Accept(StepContext context, DeviceSpec device, DeviceTargets.State state, DeviceLevel level, DecisionOrigin origin, ReasonCode reason, DeviceLevel? current, out bool commanded, bool probe = false)
    {
        var from = current ?? (origin == DecisionOrigin.Guard ? DeviceLevels.Max(device) : DeviceLevels.Off(device));
        commanded = Command(context, device, from, level);
        state.Target = level;
        state.TargetCommanded = commanded;
        state.CommandRefuted = false;
        state.Origin = origin;
        state.Reason = reason;
        state.TimedOut = false;
        state.Window = OpenWindow(context, device, WindowKind.Command, from, level, commanded, probe);
        foreach (var stage in device.Stages)
        {
            if (StageOn(device, from, stage.Index) != StageOn(device, level, stage.Index) || current is null)
            {
                _lockouts.Commanded(device.Id, stage.Index, StageOn(device, level, stage.Index), context.Now);
            }
        }
    }

    /// <summary>
    /// Calls the command port, if there is one, in active mode, for a device of the last valid
    /// configuration only (SAFE-05); during an invalid configuration only a change that switches
    /// off or lowers. Logical names and levels only: the port never sees an entity ID.
    /// </summary>
    private bool Command(StepContext context, DeviceSpec device, DeviceLevel from, DeviceLevel to)
    {
        if (_port is null || context.Mode.Effective != OperatingMode.Active || context.Evaluation.Setup?.Device(device.Id) is null)
        {
            return false;
        }

        if (!context.Evaluation.ConfigurationValid && DeviceLevels.Total(device, to) > DeviceLevels.Total(device, from))
        {
            return false;
        }

        if (device.IsSetpoint)
        {
            if (to.Setpoint is { } setpoint && !Nullable.Equals(from.Setpoint, to.Setpoint))
            {
                _port.SetSetpoint(device.Id, setpoint);
            }

            if (device.HasStopActuator && from.Enabled != to.Enabled)
            {
                _port.SetEnabled(device.Id, to.Enabled);
            }

            return true;
        }

        foreach (var stage in device.Stages)
        {
            var on = StageOn(device, to, stage.Index);
            if (StageOn(device, from, stage.Index) != on || from.Relays.IsDefault)
            {
                _port.SetRelay(device.Id, stage.Index, on);
            }
        }

        return true;
    }

    private bool CommandedFor(StepContext context, DeviceSpec device) =>
        _port is not null && context.Mode.Effective == OperatingMode.Active && context.Evaluation.Setup?.Device(device.Id) is not null;

    private static OpenWindow OpenWindow(StepContext context, DeviceSpec device, WindowKind kind, DeviceLevel from, DeviceLevel to, bool applied, bool probe)
    {
        var reaction = device.ReactionTime;
        var timeout = context.DispatchSetup?.Settling.HardTimeout ?? reaction;
        var load = context.Evaluation.Envelope.Device(device.Id);
        var before = device.Phases.ToImmutableDictionary(p => p, p => new WindowBaseline(
            DrawSample(context, device.Id, p) is { Fresh: true, Current: { } c } ? c : null,
            PowerSample(context, device.Id, p) is { Fresh: true, Power: { } w } ? w : null,
            context.Inputs?.Dispatch.For(p) is { DataValid: true, Load: { } l } ? l : null));
        return new OpenWindow(
            kind,
            new SettlingWindow(context.Now, context.Now + reaction, context.Now + Max(timeout, reaction)),
            from,
            to,
            DeviceLevels.Total(device, to) > DeviceLevels.Total(device, from),
            applied,
            load?.Contribution ?? ImmutableDictionary<Phase, Amperes>.Empty,
            device.Phases.ToImmutableDictionary(p => p, p => LoadModel.GroupTerm(device, p, load?.Reported, DrawSample(context, device.Id, p))),
            before,
            false,
            probe);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;

    private static DeviceDrawSample? DrawSample(StepContext context, DeviceName device, Phase phase) =>
        context.Inputs?.Protection.DeviceDraws is { IsDefault: false } draws ? draws.FirstOrDefault(d => d.Device == device && d.Phase == phase) : null;

    private static DevicePowerSample? PowerSample(StepContext context, DeviceName device, Phase phase) =>
        context.Inputs?.Dispatch.DevicePowers is { IsDefault: false } powers ? powers.FirstOrDefault(d => d.Device == device && d.Phase == phase) : null;

    private static bool StageOn(DeviceSpec device, DeviceLevel level, int stage) =>
        device.IsSetpoint
            ? level.Enabled && level.Setpoint is { Value: > 0 }
            : !level.Relays.IsDefault && stage - 1 < level.Relays.Length && level.Relays[stage - 1];

    private static DecisionAction ActionOf(DeviceSpec device, DeviceLevel from, DeviceLevel to)
    {
        if (device.IsSetpoint)
        {
            return from.Enabled != to.Enabled ? (to.Enabled ? DecisionAction.SwitchOn : DecisionAction.SwitchOff) : DecisionAction.SetSetpoint;
        }

        return device.Stages.Any(s => !StageOn(device, from, s.Index) && StageOn(device, to, s.Index)) ? DecisionAction.SwitchOn : DecisionAction.SwitchOff;
    }

    private static int? ChangedStage(DeviceSpec device, DeviceLevel from, DeviceLevel to) =>
        device.Stages.Length > 1 ? device.Stages.FirstOrDefault(s => StageOn(device, from, s.Index) != StageOn(device, to, s.Index))?.Index : null;

    private TimeSpan MaxRemaining(DeviceSpec device, DateTimeOffset now, bool on) =>
        device.Stages.Select(s => on ? _lockouts.MinOnRemaining(device.Id, s.Index, now, LockoutTimeline.Target) : _lockouts.MinOffRemaining(device.Id, s.Index, now, LockoutTimeline.Target)).Max();

    private DispatchDecisionInputs DispatchInputs(StepContext context, DeviceSpec device, DeviceTargets.State state)
    {
        var load = context.Evaluation.Envelope.Device(device.Id);
        var setup = context.DispatchSetup;
        return context.Dispatch?.Wish(device.Id)?.Inputs ?? new DispatchDecisionInputs(
            [],
            setup?.StartHysteresis ?? Amperes.Zero,
            setup?.StopHysteresis ?? Amperes.Zero,
            null,
            load?.Contribution ?? ImmutableDictionary<Phase, Amperes>.Empty,
            load?.InFlight ?? ImmutableDictionary<Domain.Dispatch.ConstraintKey, Amperes>.Empty,
            null,
            null,
            MaxRemaining(device, context.Now, on: true),
            MaxRemaining(device, context.Now, on: false),
            state.Deferred?.Since,
            context.Overrides.For(device.Id).Mode);
    }

    private void Record(StepContext context, DeviceSpec device, DecisionAction action, ReasonCode reason, DecisionOrigin origin, DeviceLevel? target, bool commanded, ReasonCode? refusedBy, DispatchDecisionInputs inputs, int? stage = null)
    {
        var mode = Recorded(context.Mode);
        // mode_shadow names shadow mode as what stopped a change; with active semantics a change
        // that needed no command (or could not be sent) is not refused by the mode.
        var refused = refusedBy ?? (action is DecisionAction.SwitchOn or DecisionAction.SwitchOff or DecisionAction.SetSetpoint && !commanded && mode != OperatingMode.Active ? ReasonCode.ModeShadow : null);
        context.Decisions.Add(new Decision(
            _ids.Next(),
            context.Now,
            DecisionScope.Device,
            device.Id,
            stage,
            device.Phases[0],
            action,
            reason,
            null,
            mode,
            commanded,
            refused,
            DecisionInputs.None with { Dispatch = inputs },
            origin,
            device.CircuitGroup,
            target));
    }

    private void AddHistory(DeviceTargets.State state, VerificationRecord record)
    {
        state.History.Enqueue(record);
        while (state.History.Count > _settings.HistoryLength)
        {
            state.History.Dequeue();
        }
    }

    /// <summary>A guard intent's record (Stage 2): protective intents pass lockouts and say which they outrank; ordinary ones (tests) do not.</summary>
    private Decision? Gate(GuardIntent intent, GuardEvaluation evaluation, OperatingMode mode, DateTimeOffset now, DeviceLevel? target, bool commanded = false)
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
            // v2 has no code for the change limit; settling_window_open is the nearest ("let the
            // previous change settle first").
            refusedBy = ReasonCode.SettlingWindowOpen;
        }

        if (refusedBy is null && !commanded && mode != OperatingMode.Active)
        {
            refusedBy = ReasonCode.ModeShadow;
        }

        var recorded = new RecordedIntent(intent.Target, intent.Reason, refusedBy);
        if (_recorded.TryGetValue((intent.Device, intent.Stage), out var last) && last == recorded)
        {
            return null;
        }

        _recorded[(intent.Device, intent.Stage)] = recorded;
        return new Decision(
            _ids.Next(),
            now,
            DecisionScope.Device,
            intent.Device,
            intent.Stage,
            intent.Phase,
            intent.Target switch
            {
                GuardTarget.Off => DecisionAction.SwitchOff,
                GuardTarget.AtMinimum => DecisionAction.ReduceToMinimum,
                _ => DecisionAction.Release,
            },
            intent.Reason,
            overridden,
            mode,
            commanded && refusedBy is null,
            refusedBy,
            intent.Inputs,
            DecisionOrigin.Guard,
            intent.Group,
            target);
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

    /// <summary>One step's inputs and the decisions it collects.</summary>
    private sealed record StepContext(
        GuardEvaluation Evaluation,
        DispatchEvaluation? Dispatch,
        OverrideSnapshot Overrides,
        ModeStatus Mode,
        ControlInputs? Inputs,
        DateTimeOffset Now,
        ImmutableArray<Decision>.Builder Decisions)
    {
        public DispatchSetup? DispatchSetup => Inputs?.DispatchSetup;
    }
}
