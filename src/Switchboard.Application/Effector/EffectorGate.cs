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
    private readonly DecisionIdGenerator _ids;
    private readonly EffectorSettings _settings;
    private readonly ILogger<EffectorGate> _logger;
    private readonly IDeviceCommandPort? _port;
    private readonly SelfTestSlot? _selfTest;
    private readonly Dictionary<(DeviceName Device, int? Stage), RecordedIntent> _recorded = [];
    private readonly Dictionary<DeviceName, (long Cycle, int Changes)> _changes = [];
    private readonly Dictionary<long, TicketInfo> _tickets = [];
    private readonly List<PendingDecision> _pending = [];
    private readonly List<Decision> _ready = [];
    private long? _linkGeneration;
    private bool _activeLogged;

    /// <param name="port">The command port; without one (tests of shadow semantics) nothing can be commanded and every decision records shadow.</param>
    /// <param name="selfTest">The active phase self-test's slot; null where no test can run.</param>
    public EffectorGate(LockoutClocks lockouts, DeviceTargets targets, DecisionIdGenerator ids, EffectorSettings settings, ILogger<EffectorGate> logger, IDeviceCommandPort? port = null, SelfTestSlot? selfTest = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _lockouts = lockouts;
        _targets = targets;
        _ids = ids;
        _settings = settings;
        _logger = logger;
        _port = port;
        _selfTest = selfTest;
    }

    /// <summary>The ledger, for the dispatch status.</summary>
    public DeviceTargets Ledger => _targets;

    /// <summary>Decisions waiting for the outcome of their commands.</summary>
    public int PendingDecisions => _pending.Count;

    /// <summary>
    /// The mode decision (MODE-02), at every start and every change: what the add-on option, the
    /// control switch, the configuration and the state allow, and which condition is missing.
    /// </summary>
    public Decision ModeDecision(ModeStatus mode, ControlSwitchSource? source, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(mode);
        var reason = mode.Effective == OperatingMode.Active ? ReasonCode.ManualOverride
            : mode.DecreasesOnly ? ReasonCode.SafeState
            : mode.Missing.Contains(ModeCondition.StateUnconfirmed) ? ReasonCode.KillSwitch
            : ReasonCode.ModeShadow;
        return new Decision(
            _ids.Next(),
            now,
            DecisionScope.System,
            null,
            null,
            null,
            DecisionAction.ModeResolved,
            reason,
            null,
            mode.Effective,
            false,
            null,
            DecisionInputs.None with
            {
                Mode = new ModeDecisionInputs(mode.Ceiling, mode.UiChoice, mode.Note)
                {
                    ControlAllowed = mode.ControlAllowed,
                    ConfigurationValid = mode.ConfigurationValid,
                    StateConfirmed = mode.StateConfirmed,
                    Missing = mode.Missing,
                    Source = source,
                },
            });
    }

    /// <summary>
    /// The restore baselines and the devices dispatch started under start: headroom, as state.json
    /// held them at the last stop; applied before the first step. A baseline stands only while the
    /// device's first fresh report still shows the level it was shed to (otherwise the user acted
    /// while the add-on was down).
    /// </summary>
    public void Restore(ImmutableDictionary<DeviceName, StartBaseline> baselines, ImmutableDictionary<DeviceName, DateTimeOffset> selfStarted)
    {
        ArgumentNullException.ThrowIfNull(baselines);
        ArgumentNullException.ThrowIfNull(selfStarted);
        foreach (var (device, baseline) in baselines)
        {
            var state = _targets.Get(device);
            state.Baseline = baseline with { Commanded = true };
            state.BaselineRestored = true;
        }

        foreach (var (device, at) in selfStarted)
        {
            _targets.Get(device).SelfStarted = at;
        }
    }

    /// <summary>The commanded baselines and the devices started under start: headroom, for state.json (guard thread).</summary>
    public (ImmutableDictionary<DeviceName, StartBaseline> Baselines, ImmutableDictionary<DeviceName, DateTimeOffset> SelfStarted) Persisted()
    {
        var baselines = ImmutableDictionary.CreateBuilder<DeviceName, StartBaseline>();
        var started = ImmutableDictionary.CreateBuilder<DeviceName, DateTimeOffset>();
        foreach (var device in _targets.Devices)
        {
            _targets.TryGet(device, out var state);
            if (state.Baseline is { Commanded: true } baseline)
            {
                baselines[device] = baseline;
            }

            if (state.SelfStarted is { } at)
            {
                started[device] = at;
            }
        }

        return (baselines.ToImmutable(), started.ToImmutable());
    }

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
                state.DeferredRecorded = false;
                state.LastDeferralRecord = null;
                state.ReassertPending = false;
                if (state.Baseline is { Commanded: false } || state.BaselinePending is { Commanded: false })
                {
                    state.ClearBaseline();
                }
            }

            _recorded.Clear();
            _lockouts.RestartTargets(now);
            return;
        }

        if (wasCommanding && !commanding && _selfTest?.Current is { } test && _targets.TryGet(test.Device, out var tested)
            && tested is { Origin: DecisionOrigin.SelfTest, Spec: { } spec } && tested.Target is { } target && StageOn(spec, target, test.Stage))
        {
            // Control was switched off while the test's own switch-on still holds its device on
            // (running, or aborted with its off waiting for min_on): the test's off is the last
            // command, so no load Switchboard added for the test is left behind. This is the one
            // exception to "switching control off sends nothing", on the model of the final off of a
            // removed device Switchboard started (maintainer survey 2026-10-25).
            var off = WithStage(spec, target, test.Stage, on: false);
            if (tested.Deferred is { Origin: DecisionOrigin.SelfTest })
            {
                tested.Deferred = null;
                tested.DeferredRecorded = false;
            }

            var ticket = _port!.Submit(new DeviceCommand(spec.Id, DeviceCommandKind.Relay, test.Stage, false, null, CommandPriority.Protective)
            {
                Revision = spec.ActuatorRevision,
                Decrease = true,
                Final = true,
            });
            _port.Flush();
            tested.Target = off;
            Track(ticket, spec.Id, CommandPriority.Protective, increase: false, target, null, null);
            tested.OpenTickets.Add(ticket.Sequence);
            Pend(
                new Decision(
                    _ids.Next(), now, DecisionScope.Device, spec.Id, spec.Stages.Length > 1 ? test.Stage : null, spec.Phases[0], DecisionAction.SwitchOff,
                    ReasonCode.ManualOverride, null, OperatingMode.Active, false, null, DecisionInputs.None, DecisionOrigin.SelfTest, spec.CircuitGroup, off),
                [ticket],
                null);
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
        if (mode.Commands && _port is null && !_activeLogged)
        {
            _logger.LogError("Active mode without a command path: nothing is commanded.");
            _activeLogged = true;
        }

        var decisions = ImmutableArray.CreateBuilder<Decision>();
        decisions.AddRange(_ready);
        _ready.Clear();
        var recordedMode = Recorded(mode);
        foreach (var e in evaluation.Events)
        {
            decisions.Add(new Decision(_ids.Next(), now, e.Scope, null, null, e.Phase, e.Action, e.Reason, null, recordedMode, false, null, e.Inputs, group: e.Group));
        }

        foreach (var e in dispatch?.Events ?? [])
        {
            decisions.Add(new Decision(_ids.Next(), now, e.Scope, null, null, e.Phase, e.Action, e.Reason, null, recordedMode, false, null, e.Inputs, DecisionOrigin.Dispatch));
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
                    state.ReassertPending = true;
                }
            }

            _linkGeneration = _port.LinkGeneration;
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

        if (_port is not null)
        {
            // Protective commands leave first; a port that answers at once (the development sink, a
            // test double) has its outcomes recorded in this very step.
            _port.Flush();
            TakeResults(inputs, now);
            decisions.AddRange(_ready);
            _ready.Clear();
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
        foreach (var pending in _pending.ToList())
        {
            foreach (var ticket in pending.Tickets.Where(t => !pending.Results.ContainsKey(t)).ToList())
            {
                pending.Results[ticket] = new CommandResult(new CommandTicket(ticket), pending.Draft.Device!.Value, CommandOutcome.TimedOut, null, now, "no answer before the add-on stopped");
            }

            Resolve(pending);
        }

        var drained = _ready.ToImmutableArray();
        _ready.Clear();
        return drained;
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
            Consider(state.Window?.Times.Required);
            Consider(state.Window?.Times.Timeout);
            Consider(state.Deferred?.DueAt);
            Consider(state.ConfirmBy);
        }

        return next;
    }

    private OperatingMode Recorded(ModeStatus mode) => mode.Commands && _port is not null ? OperatingMode.Active : OperatingMode.Shadow;

    /// <summary>A device whose declaration changed starts unset; its restore baseline survives only when its levels still mean the same.</summary>
    private static void Redeclared(DeviceTargets.State state, DeviceSpec previous, DeviceSpec next)
    {
        state.Unset();
        state.LastReported = null;
        if (!SpecIdentity.SameLevelSpace(previous, next))
        {
            state.ClearBaseline();
            state.SelfStarted = null;
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
        var mode = Recorded(context.Mode);
        if (gone.Spec is { } spec && gone.SelfStarted is not null && gone.LastReported is { } last && !DeviceLevels.IsOff(spec, last)
            && spec.CanSwitchOff && _port is not null && context.Mode.Commands)
        {
            // Switchboard switched it on (start: headroom), so Switchboard switches it off once, through
            // the declaration it had (maintainer survey 2026-10-25); a restore to the user's own state
            // never gets this.
            var off = DeviceLevels.Off(spec);
            var tickets = Submit(spec, last, off, CommandPriority.Ordinary, retiring: true);
            var decision = new Decision(
                _ids.Next(), context.Now, DecisionScope.Device, device, null, spec.Phases[0], DecisionAction.SwitchOff, ReasonCode.SafeState, null,
                mode, false, null, DecisionInputs.None, DecisionOrigin.Dispatch, spec.CircuitGroup, off);
            if (tickets.Count > 0)
            {
                Pend(decision, tickets, null);
            }
            else
            {
                context.Decisions.Add(decision);
            }

            _logger.LogWarning("Device {Device} is no longer declared; Switchboard had switched it on, so it is switched off once", device);
            return;
        }

        var leftOn = gone.Target is { } target && gone.Spec is { } declared && !DeviceLevels.IsOff(declared, target);
        if (leftOn || gone.Baseline is not null || gone.SelfStarted is not null)
        {
            context.Decisions.Add(new Decision(
                _ids.Next(), context.Now, DecisionScope.Device, device, null, null, DecisionAction.Release, ReasonCode.SafeState, null,
                mode, false, null, DecisionInputs.None, DecisionOrigin.Guard, null, gone.Target));
            _logger.LogWarning(
                "Device {Device} is no longer declared while {State}; it is not commanded again",
                device,
                gone.Baseline is not null ? "left reduced by Switchboard" : leftOn ? $"targeted at {gone.Target}" : "started by Switchboard");
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
        Record(context, device, DecisionAction.OverrideSet, ReasonCode.ManualOverride, DecisionOrigin.Override, state.Target, [], null, inputs);
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
            Record(context, device, DecisionAction.SettlingTimedOut, ReasonCode.SettlingWindowOpen, DecisionOrigin.Dispatch, state.Target, [], null, inputs);
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
                    Record(context, device, DecisionAction.UnresponsiveCleared, ReasonCode.DeviceUnresponsive, DecisionOrigin.Dispatch, state.Target, [], null, inputs);
                }

                break;
            case Verdict.Mismatch:
                state.CommandRefuted = window.To.Equals(state.Target);
                if (state.CommandMismatchCounted && window.To.Equals(state.Target))
                {
                    // At most one mismatch per command: its failure or its confirmation counted it already.
                    break;
                }

                state.CommandMismatchCounted = window.To.Equals(state.Target);
                state.Mismatches++;
                if (window.Probe || (state.UnresponsiveSince is null && state.Mismatches >= settling.MismatchLimit))
                {
                    // A failed probe re-marks at once, without counting again.
                    state.UnresponsiveSince = window.Probe ? state.UnresponsiveSince ?? context.Now : context.Now;
                    state.RetryAt = context.Now + settling.UnresponsiveRetryAfter;
                    Record(context, device, DecisionAction.MarkedUnresponsive, ReasonCode.DeviceUnresponsive, DecisionOrigin.Dispatch, state.Target, [], null, inputs);
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
        ObserveTransition(context, device, state, reported);
        ResolveBaseline(context, device, state, reported);
        Confirm(context, device, state, reported);
        if (state.PendingMismatch)
        {
            state.PendingMismatch = false;
            CountMismatch(context, device, state);
        }

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
        // min_on it outranks is still running then), carrying the target its change leaves. Their
        // command outcome is known once the change below has been sent.
        var holdRecords = new List<Decision>();
        foreach (var intent in context.Evaluation.Intents.Where(i => i.Device == device.Id))
        {
            var forced = context.Overrides.For(device.Id).Mode == OverrideMode.ForceOn && intent.Target != GuardTarget.Released;
            var target = protectiveChange ? held : state.Target;
            var willCommand = protectiveChange && intent.Target != GuardTarget.Released && CommandsFor(context, device);
            if (Gate(intent with { Overridden = forced ? ReasonCode.ManualOverride : intent.Overridden }, context.Evaluation, Recorded(context.Mode), context.Now, target, willCommand) is { } decision)
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
                    Pend(record, tickets, null);
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
        else if (state.Deferred is { DueAt: { } due } deferred && context.Now >= due)
        {
            // DISP-09 reconciliation: the lockout expired between boundaries; the last boundary's
            // wish stays valid until the next one. A deferred decrease is applied now. A deferred
            // increase waits for the next boundary, where dispatch wants it afresh with every gate
            // re-checked (the guard may have stopped increases, a window may have opened, the
            // increase budget may be spent since the wish was made); the lockout has expired, so it
            // is applied there at once, still carrying its deferral.
            if (deferred.Origin == DecisionOrigin.SelfTest)
            {
                state.Deferred = null;
                state.DeferredRecorded = false;
            }
            else if (wish is null || !wish.Level.Equals(deferred.Target))
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

    private SelfTestTarget? SelfTestFor(DeviceSpec device) =>
        _selfTest?.Current is { } test && test.Device == device.Id && !device.IsSetpoint && test.Stage >= 1 && test.Stage <= device.Stages.Length ? test : null;

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
            if (state.Deferred is { Origin: DecisionOrigin.SelfTest })
            {
                state.Deferred = null;
                state.DeferredRecorded = false;
            }

            return;
        }

        if (test.On && !Constraints(context, device).All(k => context.Evaluation.Envelope.For(k) is { IncreasesAllowed: true }))
        {
            return;
        }

        var inputs = DispatchInputs(context, device, state);
        var change = test.On ? DispatchChange.Increase : DispatchChange.Decrease;
        Want(context, device, state, new DispatchWish(device.Id, level, ReasonCode.ManualOverride, DecisionOrigin.SelfTest, change, null, true, inputs), ImmutableDictionary<int, StageLimit>.Empty, current);
    }

    private static IEnumerable<ConstraintKey> Constraints(StepContext context, DeviceSpec device)
    {
        foreach (var phase in device.Phases)
        {
            yield return ConstraintKey.Main(phase);
        }

        if (device.CircuitGroup is { } group && context.Evaluation.Setup?.Group(group) is { } spec)
        {
            foreach (var phase in device.Phases.Where(spec.Phases.Contains))
            {
                yield return new ConstraintKey(group, phase);
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

    private void RecordHold(StepContext context, DeviceSpec device, ImmutableDictionary<int, StageLimit> limits, DeviceLevel? from, DeviceLevel held, ReasonCode reason, ReasonCode? overridden, IReadOnlyList<CommandTicket> tickets, bool reasserted = false)
    {
        var action = limits.Values.Any(l => l == StageLimit.Off) || limits.Count == 0 && DeviceLevels.IsOff(device, held) ? DecisionAction.SwitchOff : DecisionAction.ReduceToMinimum;
        var mode = Recorded(context.Mode);
        var decision = new Decision(
            _ids.Next(),
            context.Now,
            DecisionScope.Device,
            device.Id,
            from is null ? null : ChangedStage(device, from, held),
            device.Phases[0],
            action,
            reason,
            overridden,
            mode,
            false,
            tickets.Count > 0 || mode == OperatingMode.Active ? null : ReasonCode.ModeShadow,
            DecisionInputs.None with { Dispatch = DispatchInputs(context, device, _targets.Get(device.Id)) with { Reasserted = reasserted } },
            DecisionOrigin.Guard,
            device.CircuitGroup,
            held);
        if (tickets.Count > 0)
        {
            Pend(decision, tickets, null);
        }
        else
        {
            context.Decisions.Add(decision);
        }
    }

    private void ObserveTransition(StepContext context, DeviceSpec device, DeviceTargets.State state, DeviceLevel? reported)
    {
        if (reported is null)
        {
            return;
        }

        var previous = state.LastReported;
        state.LastReported = reported;
        if (state.SelfStarted is not null && DeviceLevels.IsOff(device, reported))
        {
            state.SelfStarted = null;
        }

        if (previous is null || previous.Equals(reported) || reported.Equals(state.Target))
        {
            return;
        }

        if (state.TargetCommanded && state.Target is { } commanded && MovesToward(device, previous, reported, commanded))
        {
            // Part of our own command shows first (one relay of several, the setpoint before the
            // stop actuator): progress toward the target, not someone else's act.
            return;
        }

        // Someone else switched the device, and the step is observed for the history either way.
        // Where the intended state follows the real one (no target set, or active mode, which does
        // not fight another controller) its contacts moved: the target lockouts restart and a set
        // target follows. In shadow mode a set target is the would-be world and stays as it is: the
        // same intent is never recorded again because another controller undid it in reality. The
        // level the user chose is the new prior level: a restore baseline ends, and a device
        // dispatch had started is no longer Switchboard's to switch off.
        state.ClearBaseline();
        state.SelfStarted = null;
        var followsReality = state.Target is null || context.Mode.Commands;
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
                state.ConfirmBy = null;
                state.UnconfirmedSince = null;
                state.ProtectiveFailed = false;
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

    /// <summary>Every component that changed between two reports now equals the target's: the device is on its way to the commanded level.</summary>
    private static bool MovesToward(DeviceSpec device, DeviceLevel previous, DeviceLevel reported, DeviceLevel target)
    {
        if (device.IsSetpoint)
        {
            var setpointMoved = !Nullable.Equals(previous.Setpoint, reported.Setpoint);
            var enabledMoved = previous.Enabled != reported.Enabled;
            return (!setpointMoved || SameSetpoint(reported.Setpoint, target.Setpoint, device))
                && (!enabledMoved || reported.Enabled == target.Enabled)
                && (setpointMoved || enabledMoved);
        }

        var changed = device.Stages.Where(s => StageOn(device, previous, s.Index) != StageOn(device, reported, s.Index)).ToList();
        return changed.Count > 0 && changed.All(s => StageOn(device, reported, s.Index) == StageOn(device, target, s.Index));
    }

    private static bool SameSetpoint(Amperes? a, Amperes? b, DeviceSpec device) =>
        a is { } x && b is { } y && Math.Abs(x.Value - y.Value) <= (device.Range?.Step.Value ?? 0) / 2;

    /// <summary>
    /// A restore baseline taken while the level was unknown is decided by the first fresh report
    /// (set when it is above the shed level and no acknowledged command reached the device first);
    /// one restored from state.json stands only while the device still shows the level it was shed to.
    /// When it does not (the user acted while the add-on was down, or the shutdown's off never
    /// landed) and a reduction of Switchboard holds the device below what it reports now (the
    /// startup safe state, taken while its level was unknown), the reported level is the one to give
    /// back, exactly as for a device without a persisted baseline.
    /// </summary>
    private void ResolveBaseline(StepContext context, DeviceSpec device, DeviceTargets.State state, DeviceLevel? reported)
    {
        if (reported is null)
        {
            return;
        }

        if (state.BaselinePending is { } pending)
        {
            state.BaselinePending = null;
            state.Baseline = !state.AckedSincePending && DeviceLevels.Total(device, reported) > DeviceLevels.Total(device, pending.Shed) + DeviceLevel.SetpointTolerance
                ? pending with { Level = reported }
                : null;
            state.AckedSincePending = false;
        }

        if (state.BaselineRestored)
        {
            state.BaselineRestored = false;
            if (state.Baseline is { } restored && !reported.Equals(restored.Shed))
            {
                state.Baseline = state.Target is { } target && state.Origin is DecisionOrigin.Guard or DecisionOrigin.Dispatch
                    && DeviceLevels.Total(device, target) < DeviceLevels.Total(device, reported) - DeviceLevel.SetpointTolerance
                    ? new StartBaseline(reported, target, context.Now, CommandsFor(context, device))
                    : null;
            }
        }
    }

    /// <summary>
    /// An acknowledged command to a device whose state reports on change must show in the reported
    /// state by ack + reaction time + effector.confirm_timeout. If it does not (a fresh report still differs), the device is
    /// flagged unconfirmed (a warning; whether Home Assistant marks a Modbus or go-e entity
    /// unavailable after a link loss is not known), the window's relief ends, and one mismatch counts
    /// unless the command's verification counted one already. The flag clears once it shows.
    /// </summary>
    private void Confirm(StepContext context, DeviceSpec device, DeviceTargets.State state, DeviceLevel? reported)
    {
        if (state.UnconfirmedSince is not null && reported is not null && reported.Equals(state.Target))
        {
            state.UnconfirmedSince = null;
            _logger.LogInformation("Device {Device} now reports the commanded level {Target}", device.Id, state.Target);
        }

        if (state.ConfirmBy is not { } by || context.Now < by)
        {
            return;
        }

        state.ConfirmBy = null;
        if (reported is null || state.Target is not { } target || reported.Equals(target) || (device.IsSetpoint && SameSetpoint(reported.Setpoint, target.Setpoint, device) && reported.Enabled == target.Enabled))
        {
            return;
        }

        state.UnconfirmedSince ??= context.Now;
        _logger.LogWarning(
            "Device {Device} acknowledged the command to {Target} but still reports {Reported} after its reaction time and confirm timeout; check that its entity follows commands",
            device.Id,
            target,
            reported);
        if (state.Window is { Kind: WindowKind.Command, Open: true, Applied: true } window)
        {
            state.Window = window with { Outcome = SettlingState.Closed };
        }

        if (!state.CommandMismatchCounted)
        {
            CountMismatch(context, device, state);
        }
    }

    /// <summary>One mismatch of a command (its failure, or a confirmation that never came): at most one per command, toward settling.mismatch_limit.</summary>
    private void CountMismatch(StepContext context, DeviceSpec device, DeviceTargets.State state)
    {
        if (state.CommandMismatchCounted || context.DispatchSetup is not { } setup)
        {
            return;
        }

        state.CommandMismatchCounted = true;
        state.Mismatches++;
        if (state.UnresponsiveSince is null && state.Mismatches >= setup.Settling.MismatchLimit)
        {
            state.UnresponsiveSince = context.Now;
            state.RetryAt = context.Now + setup.Settling.UnresponsiveRetryAfter;
            Record(context, device, DecisionAction.MarkedUnresponsive, ReasonCode.DeviceUnresponsive, DecisionOrigin.Dispatch, state.Target, [], null, DispatchInputs(context, device, state));
        }
    }

    /// <summary>
    /// After a new Home Assistant link delivered its states: a protective or lowering target the
    /// device does not show yet is sent once more (its command was lost while the link was down).
    /// Never an increase: an increase is planned again through every gate.
    /// </summary>
    private void Reassert(StepContext context, DeviceSpec device, DeviceTargets.State state, DeviceLevel? reported)
    {
        if (!state.ReassertPending || reported is null)
        {
            return;
        }

        state.ReassertPending = false;
        if (state.Target is not { } target || state.Origin is not { } origin || reported.Equals(target)
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
        if (!state.ProtectiveFailed || state.Target is not { } target || reported is null)
        {
            return;
        }

        if (reported.Equals(target))
        {
            state.ProtectiveFailed = false;
            state.ResendRecorded = false;
            return;
        }

        if (!context.Evaluation.AtBoundary || state.OpenTickets.Count > 0)
        {
            return;
        }

        var tickets = CommandChange(context, device, state, reported, target, CommandPriority.Protective);
        if (tickets.Count == 0)
        {
            return;
        }

        if (!state.ResendRecorded)
        {
            state.ResendRecorded = true;
            RecordReasserted(context, device, state, reported, target, state.Origin ?? DecisionOrigin.Guard, tickets);
        }
    }

    private void RecordReasserted(StepContext context, DeviceSpec device, DeviceTargets.State state, DeviceLevel from, DeviceLevel target, DecisionOrigin origin, IReadOnlyList<CommandTicket> tickets)
    {
        var inputs = DispatchInputs(context, device, state) with { Reasserted = true };
        Record(context, device, ActionOf(device, from, target), state.Reason ?? ReasonCode.SafeState, origin, target, tickets, null, inputs, ChangedStage(device, from, target));
    }

    /// <summary>Sends a change of the current target again without a new target: the ledger only learns of the new command's tickets.</summary>
    private List<CommandTicket> CommandChange(StepContext context, DeviceSpec device, DeviceTargets.State state, DeviceLevel from, DeviceLevel to, CommandPriority priority)
    {
        var tickets = Command(context, device, from, to, priority);
        foreach (var ticket in tickets)
        {
            Track(ticket, device.Id, priority, DeviceLevels.Total(device, to) > DeviceLevels.Total(device, from), from, state.Origin, state.Reason);
            state.OpenTickets.Add(ticket.Sequence);
        }

        if (tickets.Count > 0)
        {
            state.CommandMismatchCounted = false;
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

        // A start by Switchboard, not a restore: with a restore baseline pending the device was the
        // user's before Switchboard reduced it, and switching it back on gives the user's state back.
        var selfStart = wish.Origin == DecisionOrigin.Dispatch && device.Start == StartPolicy.Headroom && DeviceLevels.IsOff(device, from) && !DeviceLevels.IsOff(device, level)
            && state.Baseline is null && state.BaselinePending is null;
        var tickets = Accept(context, device, state, level, wish.Origin, wish.Reason, current, context.Evaluation.Envelope.Device(device.Id)?.Reported, probe);
        var inputs = wish.Inputs with
        {
            DeferredSince = deferredSince,
            Settling = SettlingState.Open,
            MinOnRemaining = MaxRemaining(device, context.Now, on: true),
            MinOffRemaining = MaxRemaining(device, context.Now, on: false),
        };
        Action<bool>? acknowledged = selfStart
            ? ok =>
            {
                if (ok && _targets.TryGet(device.Id, out var started) && started.Target is { } now && !DeviceLevels.IsOff(device, now))
                {
                    started.SelfStarted ??= context.Now;
                }
            }
            : null;
        Record(context, device, ActionOf(device, from, level), wish.Reason, wish.Origin, level, tickets, null, inputs, ChangedStage(device, from, level), acknowledged);
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
        Record(context, device, DecisionAction.Deferred, wish.Reason, wish.Origin, state.Target, [], refusedBy, inputs);
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
        if (decrease)
        {
            TakeBaseline(context, device, state, origin, level, reported, commanding);
        }
        else if (increase && (commanding || state.Baseline is { Commanded: false }) && state.Baseline is { } baseline
            && DeviceLevels.Total(device, level) >= DeviceLevels.Total(device, baseline.Level) - DeviceLevel.SetpointTolerance)
        {
            // Restored up to the level the device had before Switchboard reduced it. A reduction that
            // was sent ends only with a restore that is sent too: a "would" restore in shadow (control
            // switched off after the shed) leaves the device where it is, still to be given back.
            state.ClearBaseline();
        }

        var priority = origin == DecisionOrigin.Guard ? CommandPriority.Protective : CommandPriority.Ordinary;
        var tickets = Command(context, device, from, level, priority);
        foreach (var ticket in tickets)
        {
            Track(ticket, device.Id, priority, increase, from, state.Origin, state.Reason);
        }

        state.OpenTickets.Clear();
        state.OpenTickets.UnionWith(tickets.Select(t => t.Sequence));
        state.Target = level;
        state.TargetCommanded = commanding;
        state.CommandRefuted = false;
        state.CommandMismatchCounted = false;
        state.ConfirmBy = null;
        state.UnconfirmedSince = null;
        state.ProtectiveFailed = false;
        state.ResendRecorded = false;
        state.Origin = origin;
        state.Reason = reason;
        state.TimedOut = false;
        state.Window = OpenWindow(context, device, WindowKind.Command, from, level, commanding, probe);
        foreach (var stage in device.Stages)
        {
            if (StageOn(device, from, stage.Index) != StageOn(device, level, stage.Index) || current is null)
            {
                _lockouts.Commanded(device.Id, stage.Index, StageOn(device, level, stage.Index), context.Now);
            }
        }

        return tickets;
    }

    /// <summary>
    /// The level before Switchboard's first reduction of a device is what start: restore_only
    /// restores. Only a reduction by protection or dispatch takes one, and only when the device's
    /// level is the user's own: never while the self-test or a start under start: headroom put it
    /// there (undoing that brings it back to the user's level, which is not restored).
    /// </summary>
    private static void TakeBaseline(StepContext context, DeviceSpec device, DeviceTargets.State state, DecisionOrigin origin, DeviceLevel level, DeviceLevel? reported, bool commanded)
    {
        if (origin is not (DecisionOrigin.Guard or DecisionOrigin.Dispatch) || state.Origin == DecisionOrigin.SelfTest || state.SelfStarted is not null
            || state.Baseline is not null || state.BaselinePending is not null)
        {
            return;
        }

        if (reported is null)
        {
            state.BaselinePending = new StartBaseline(level, level, context.Now, commanded);
            state.AckedSincePending = false;
            return;
        }

        if (DeviceLevels.Total(device, reported) > DeviceLevels.Total(device, level) + DeviceLevel.SetpointTolerance)
        {
            state.Baseline = new StartBaseline(reported, level, context.Now, commanded);
        }
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

        if (device.IsSetpoint)
        {
            if (to.Setpoint is { } setpoint && !Nullable.Equals(from.Setpoint, to.Setpoint) && !(retiring && device.HasStopActuator))
            {
                Send(DeviceCommandKind.Setpoint, device.Stages[0].Index, true, setpoint, from.Setpoint is not { } before || setpoint.Value < before.Value);
            }

            if (device.HasStopActuator && from.Enabled != to.Enabled)
            {
                Send(DeviceCommandKind.Enable, device.Stages[0].Index, to.Enabled, null, !to.Enabled);
            }

            return tickets;
        }

        foreach (var stage in device.Stages)
        {
            var on = StageOn(device, to, stage.Index);
            if (StageOn(device, from, stage.Index) != on || from.Relays.IsDefault)
            {
                Send(DeviceCommandKind.Relay, stage.Index, on, null, !on);
            }
        }

        return tickets;
    }

    private bool CommandsFor(StepContext context, DeviceSpec device) =>
        _port is not null && context.Mode.Commands && context.Evaluation.Setup?.Device(device.Id) is not null;

    private void Track(CommandTicket ticket, DeviceName device, CommandPriority priority, bool increase, DeviceLevel? from, DecisionOrigin? previousOrigin, ReasonCode? previousReason) =>
        _tickets[ticket.Sequence] = new TicketInfo(device, priority, increase, from, previousOrigin, previousReason);

    /// <summary>Holds a decision until every command it carries has an outcome; <paramref name="resolved"/> runs then with whether all were acknowledged.</summary>
    private void Pend(Decision decision, IReadOnlyList<CommandTicket> tickets, Action<bool>? resolved)
    {
        var pending = new PendingDecision(decision, [.. tickets.Select(t => t.Sequence)], resolved);
        _pending.Add(pending);
    }

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
            foreach (var pending in _pending.Where(p => p.Tickets.Contains(result.Ticket.Sequence)))
            {
                pending.Results[result.Ticket.Sequence] = result;
            }

            if (_tickets.Remove(result.Ticket.Sequence, out var info) && _targets.TryGet(info.Device, out var state) && state.OpenTickets.Remove(result.Ticket.Sequence))
            {
                Learn(state, info, result, inputs, now);
            }
        }

        foreach (var pending in _pending.Where(p => p.Tickets.All(p.Results.ContainsKey)).ToList())
        {
            Resolve(pending);
        }
    }

    /// <summary>
    /// What a command's outcome means for its device. Acknowledged: the reported state must show the
    /// target by ack + reaction time + effector.confirm_timeout. An error counts one mismatch; a
    /// timeout or a closed link counts nothing by itself (confirmation decides). Anything but an
    /// acknowledgement ends the window's relief at once (the guard then judges the measured current
    /// and takes its next candidate), takes a failed ordinary increase back (dispatch plans it again
    /// through every gate), and marks a failed protective or lowering target for re-sending at each
    /// boundary.
    /// </summary>
    private void Learn(DeviceTargets.State state, TicketInfo info, CommandResult result, ControlInputs? inputs, DateTimeOffset now)
    {
        if (result.Outcome == CommandOutcome.Acknowledged)
        {
            if (state.BaselinePending is not null)
            {
                state.AckedSincePending = true;
            }

            if (state.OpenTickets.Count == 0 && state.Spec is { StateReportsOnChange: true } spec)
            {
                var confirm = inputs?.DispatchSetup?.Settling.ConfirmTimeout ?? TimeSpan.Zero;
                state.ConfirmBy = now + spec.ReactionTime + confirm;
            }

            return;
        }

        if (result is { Outcome: CommandOutcome.NotSent, NotSent: CommandNotSentReason.Superseded or CommandNotSentReason.ControlOff })
        {
            return;
        }

        if (result is { Outcome: CommandOutcome.NotSent, NotSent: CommandNotSentReason.NotDeclared })
        {
            _logger.LogError("A command for {Device} was refused at send time: its entity is not a declared actuator of the configuration loaded now ({Detail})", info.Device, result.Detail);
        }
        else if (result.Outcome != CommandOutcome.NotSent)
        {
            _logger.LogWarning("Command for {Device} {Outcome}: {Detail}", info.Device, result.Outcome, result.Detail);
        }

        state.TargetCommanded = false;
        if (state.Window is { Kind: WindowKind.Command, Open: true } window)
        {
            state.Window = window with { Applied = false, Outcome = SettlingState.Closed };
        }

        if (result.Outcome == CommandOutcome.Failed)
        {
            state.PendingMismatch = true;
        }
        else if (result.Outcome == CommandOutcome.TimedOut && state.Spec is { StateReportsOnChange: true } spec)
        {
            var confirm = inputs?.DispatchSetup?.Settling.ConfirmTimeout ?? TimeSpan.Zero;
            state.ConfirmBy = now + spec.ReactionTime + confirm;
        }

        if (info.Priority == CommandPriority.Protective || !info.Increase)
        {
            // A lowering target the device may not show (an off, a lower setpoint, the self-test's
            // off) keeps being sent: the target stays where it is, so a later hold at the same level
            // would otherwise change nothing and never leave.
            state.ProtectiveFailed = result.Outcome != CommandOutcome.NotSent;
            return;
        }

        if (info.Increase && state.Spec is { } device)
        {
            // A phantom increase would make dispatch believe the device is already there.
            var back = info.From ?? DeviceLevels.Off(device);
            foreach (var stage in device.Stages)
            {
                if (state.Target is { } target && StageOn(device, target, stage.Index) != StageOn(device, back, stage.Index))
                {
                    _lockouts.Commanded(device.Id, stage.Index, StageOn(device, back, stage.Index), now);
                }
            }

            state.Target = back;
            state.Origin = info.PreviousOrigin;
            state.Reason = info.PreviousReason;
            state.ConfirmBy = null;
        }
    }

    private void Resolve(PendingDecision pending)
    {
        _pending.Remove(pending);
        var results = pending.Tickets.Select(t => pending.Results[t]).ToList();
        var acknowledged = results.All(r => r.Outcome == CommandOutcome.Acknowledged);
        ReasonCode? refusedBy = acknowledged
            ? null
            : results.First(r => r.Outcome != CommandOutcome.Acknowledged) switch
            {
                { Outcome: CommandOutcome.Failed or CommandOutcome.TimedOut } => ReasonCode.DeviceUnresponsive,
                { NotSent: CommandNotSentReason.ControlOff } => ReasonCode.ModeShadow,

                // Not declared at send time, a device re-pointed at another entity, or a command a
                // newer one for the same actuator replaced: safe_state is the nearest code of the
                // closed list (change_limit means an increase waiting for another in flight).
                _ => ReasonCode.SafeState,
            };
        _ready.Add(pending.Draft.WithCommand(acknowledged, refusedBy));
        pending.Resolved?.Invoke(acknowledged);
    }

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

    /// <summary>A relay device's level with one stage switched on or off.</summary>
    private static DeviceLevel WithStage(DeviceSpec device, DeviceLevel level, int stage, bool on)
    {
        var relays = (level.Relays.IsDefault || level.Relays.Length != device.Stages.Length ? [.. device.Stages.Select(_ => false)] : level.Relays).ToArray();
        relays[stage - 1] = on;
        return new DeviceLevel([.. relays], null, true);
    }

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

    /// <param name="tickets">The commands the decision carries: it is recorded once each has an outcome; none records it at once.</param>
    private void Record(StepContext context, DeviceSpec device, DecisionAction action, ReasonCode reason, DecisionOrigin origin, DeviceLevel? target, IReadOnlyList<CommandTicket> tickets, ReasonCode? refusedBy, DispatchDecisionInputs inputs, int? stage = null, Action<bool>? acknowledged = null)
    {
        var mode = Recorded(context.Mode);

        // mode_shadow names shadow mode as what stopped a change; with active semantics a change
        // that needed no command is not refused by the mode.
        var refused = refusedBy ?? (action is DecisionAction.SwitchOn or DecisionAction.SwitchOff or DecisionAction.SetSetpoint && tickets.Count == 0 && mode != OperatingMode.Active ? ReasonCode.ModeShadow : null);
        var decision = new Decision(
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
            false,
            refused,
            DecisionInputs.None with { Dispatch = inputs },
            origin,
            device.CircuitGroup,
            target);
        if (tickets.Count > 0 && refusedBy is null)
        {
            Pend(decision, tickets, acknowledged);
        }
        else
        {
            context.Decisions.Add(decision);
        }
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
            false,
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

    /// <summary>A submitted command: its device, priority, and what to go back to if an increase fails.</summary>
    private sealed record TicketInfo(DeviceName Device, CommandPriority Priority, bool Increase, DeviceLevel? From, DecisionOrigin? PreviousOrigin, ReasonCode? PreviousReason);

    /// <summary>A decision waiting for the outcomes of its commands.</summary>
    private sealed class PendingDecision(Decision draft, HashSet<long> tickets, Action<bool>? resolved)
    {
        public Decision Draft { get; } = draft;

        public HashSet<long> Tickets { get; } = tickets;

        public Dictionary<long, CommandResult> Results { get; } = [];

        public Action<bool>? Resolved { get; } = resolved;
    }

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
