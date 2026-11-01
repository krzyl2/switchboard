using System.Collections.Immutable;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Effector;

/// <summary>
/// Where the gate's decisions are built (03 EffectorGate): the mode decision, the dispatch fault,
/// the records of guard and dispatch events, of a device's change, of a hold, of a guard intent and
/// of the one-off offs of a removed device or an ended self-test. Every decision is made with the
/// <see cref="Decision"/> constructor, which enforces the closed reason list and its invariants; the
/// factory only fills it (the id comes from the generator at the moment of construction, so the order
/// of ids follows the order of calls). It decides nothing and keeps no state: whether to record, when
/// and with which tickets is the gate's.
/// </summary>
internal sealed class DecisionFactory
{
    private readonly DecisionIdGenerator _ids;
    private readonly LockoutClocks _lockouts;
    private readonly bool _commandPath;

    /// <param name="commandPath">The gate has a command port; without one every decision records shadow.</param>
    public DecisionFactory(DecisionIdGenerator ids, LockoutClocks lockouts, bool commandPath)
    {
        _ids = ids;
        _lockouts = lockouts;
        _commandPath = commandPath;
    }

    /// <summary>The mode a decision records: active only while commands may leave and the gate has a way to send them.</summary>
    public OperatingMode Recorded(ModeStatus mode) => mode.Commands && _commandPath ? OperatingMode.Active : OperatingMode.Shadow;

    public Decision Mode(ModeStatus mode, ControlSwitchSource? source, DateTimeOffset now)
    {
        var reason = mode.Effective == OperatingMode.Active ? ReasonCode.ModeActive
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
                Mode = new ModeDecisionInputs(mode.Ceiling, mode.Note)
                {
                    ControlAllowed = mode.ControlAllowed,
                    ConfigurationValid = mode.ConfigurationValid,
                    StateConfirmed = mode.StateConfirmed,
                    Missing = mode.Missing,
                    Source = source,
                },
            });
    }

    public Decision DispatchFault(ModeStatus mode, DateTimeOffset now) =>
        new(_ids.Next(), now, DecisionScope.System, null, null, null, DecisionAction.StopIncreases, ReasonCode.DispatchFault, null, Recorded(mode), false, null, DecisionInputs.None, DecisionOrigin.Dispatch);

    /// <summary>The record of an event the guard produced.</summary>
    public Decision FromGuard(GuardEvent e, OperatingMode recordedMode, DateTimeOffset now) =>
        new(_ids.Next(), now, e.Scope, null, null, e.Phase, e.Action, e.Reason, null, recordedMode, false, null, e.Inputs, group: e.Group);

    /// <summary>The record of an event dispatch produced.</summary>
    public Decision FromDispatch(DispatchEvent e, OperatingMode recordedMode, DateTimeOffset now) =>
        new(_ids.Next(), now, e.Scope, null, null, e.Phase, e.Action, e.Reason, null, recordedMode, false, null, e.Inputs, DecisionOrigin.Dispatch);

    /// <summary>The active self-test's own off, sent when control is switched off while the test holds its device on.</summary>
    public Decision SelfTestOff(DeviceSpec spec, int stage, DeviceLevel off, DateTimeOffset now) =>
        new(
            _ids.Next(), now, DecisionScope.Device, spec.Id, spec.Stages.Length > 1 ? stage : null, spec.Phases[0], DecisionAction.SwitchOff,
            ReasonCode.ManualOverride, null, OperatingMode.Active, false, null, DecisionInputs.None, DecisionOrigin.SelfTest, spec.CircuitGroup, off);

    /// <summary>The final off of a removed device Switchboard had started.</summary>
    public Decision RemovedOff(DeviceSpec spec, DeviceName device, OperatingMode mode, DateTimeOffset now, DeviceLevel off) =>
        new(
            _ids.Next(), now, DecisionScope.Device, device, null, spec.Phases[0], DecisionAction.SwitchOff, ReasonCode.SafeState, null,
            mode, false, null, DecisionInputs.None, DecisionOrigin.Dispatch, spec.CircuitGroup, off);

    /// <summary>A removed device left as it was: Switchboard lets go of it.</summary>
    public Decision RemovedRelease(DeviceName device, OperatingMode mode, DateTimeOffset now, DeviceLevel? target) =>
        new(
            _ids.Next(), now, DecisionScope.Device, device, null, null, DecisionAction.Release, ReasonCode.SafeState, null,
            mode, false, null, DecisionInputs.None, DecisionOrigin.Guard, null, target);

    /// <summary>
    /// The record of a protective change made without a guard intent of its own (a hold re-asserted).
    /// </summary>
    /// <param name="sent">The change's command was sent: no mode_shadow refusal (the command's outcome completes the record).</param>
    public Decision Hold(StepContext context, DeviceSpec device, DeviceTargets.State state, ImmutableDictionary<int, StageLimit> limits, DeviceLevel? from, DeviceLevel held, ReasonCode reason, ReasonCode? overridden, bool sent, bool reasserted)
    {
        var action = limits.Values.Any(l => l == StageLimit.Off) || limits.Count == 0 && DeviceLevels.IsOff(device, held) ? DecisionAction.SwitchOff : DecisionAction.ReduceToMinimum;
        var mode = Recorded(context.Mode);
        return new Decision(
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
            sent || mode == OperatingMode.Active ? null : ReasonCode.ModeShadow,
            DecisionInputs.None with { Dispatch = DispatchInputs(context, device, state) with { Reasserted = reasserted } },
            DecisionOrigin.Guard,
            device.CircuitGroup,
            held);
    }

    /// <summary>The record of a device's change (or deferral, or verdict); <paramref name="hasTickets"/>: the change carries commands.</summary>
    public Decision Device(StepContext context, DeviceSpec device, DecisionAction action, ReasonCode reason, DecisionOrigin origin, DeviceLevel? target, bool hasTickets, ReasonCode? refusedBy, DispatchDecisionInputs inputs, int? stage)
    {
        var mode = Recorded(context.Mode);

        // mode_shadow names shadow mode as what stopped a change; with active semantics a change
        // that needed no command is not refused by the mode.
        var refused = refusedBy ?? (action is DecisionAction.SwitchOn or DecisionAction.SwitchOff or DecisionAction.SetSetpoint && !hasTickets && mode != OperatingMode.Active ? ReasonCode.ModeShadow : null);
        return new Decision(
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
    }

    /// <summary>A guard intent's record (Stage 2).</summary>
    public Decision Intent(GuardIntent intent, ReasonCode? overridden, ReasonCode? refusedBy, OperatingMode mode, DateTimeOffset now, DeviceLevel? target) =>
        new(
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

    internal static DecisionAction ActionOf(DeviceSpec device, DeviceLevel from, DeviceLevel to)
    {
        if (device is SetpointSpec)
        {
            return from.Enabled != to.Enabled ? (to.Enabled ? DecisionAction.SwitchOn : DecisionAction.SwitchOff) : DecisionAction.SetSetpoint;
        }

        return device.Stages.Any(s => !DeviceLevels.StageOn(device, from, s.Index) && DeviceLevels.StageOn(device, to, s.Index)) ? DecisionAction.SwitchOn : DecisionAction.SwitchOff;
    }

    internal static int? ChangedStage(DeviceSpec device, DeviceLevel from, DeviceLevel to) =>
        device.Stages.Length > 1 ? device.Stages.FirstOrDefault(s => DeviceLevels.StageOn(device, from, s.Index) != DeviceLevels.StageOn(device, to, s.Index))?.Index : null;

    public TimeSpan MaxRemaining(DeviceSpec device, DateTimeOffset now, bool on) =>
        device.Stages.Select(s => on ? _lockouts.MinOnRemaining(device.Id, s.Index, now, LockoutTimeline.Target) : _lockouts.MinOffRemaining(device.Id, s.Index, now, LockoutTimeline.Target)).Max();

    public DispatchDecisionInputs DispatchInputs(StepContext context, DeviceSpec device, DeviceTargets.State state)
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
            state.Deferrals.Deferred?.Since,
            context.Overrides.For(device.Id).Mode)
        {
            Balance = context.Dispatch?.Balance?.ToDecisionInputs(),
        };
    }
}
