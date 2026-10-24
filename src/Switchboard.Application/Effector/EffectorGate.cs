using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Decisions;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;

namespace Switchboard.Application.Effector;

/// <summary>
/// The single place where an intent would become a command (03 EffectorGate). In this version it
/// has no way to command anything: it holds no command sender, and its only output is the decision
/// record. It enforces the mode (shadow: never commanded, MODE-01), lockouts and the per-cycle
/// change limit for ordinary intents (dispatch, from Stage 3), and lets protective intents (every
/// guard intent) through both, recording the lockout they outrank. A decision is recorded only
/// when what it says changes, so an unchanged cycle writes nothing (MODE-03).
/// </summary>
public sealed class EffectorGate
{
    private readonly LockoutClocks _lockouts;
    private readonly DecisionIdGenerator _ids;
    private readonly ILogger<EffectorGate> _logger;
    private readonly Dictionary<(DeviceName Device, int? Stage), Recorded> _recorded = [];
    private readonly Dictionary<DeviceName, (long Cycle, int Changes)> _changes = [];
    private bool _activeLogged;

    public EffectorGate(LockoutClocks lockouts, DecisionIdGenerator ids, ILogger<EffectorGate> logger)
    {
        _lockouts = lockouts;
        _ids = ids;
        _logger = logger;
    }

    /// <summary>The mode decision recorded at every start: what was asked for, and what runs.</summary>
    public Decision Start(ModeStatus mode, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(mode);
        if (mode.Note == ModeNote.ActiveUnavailableBeforeStage3)
        {
            _logger.LogWarning("Active mode is allowed by the add-on option, but this version has no command path: it runs in shadow mode.");
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
            OperatingMode.Shadow,
            false,
            null,
            DecisionInputs.None with { Mode = new ModeDecisionInputs(mode.Ceiling, mode.UiChoice, mode.Note) });
    }

    public ImmutableArray<Decision> Apply(GuardEvaluation evaluation, ModeStatus mode, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(mode);
        if (mode.Effective == OperatingMode.Active && !_activeLogged)
        {
            // Unreachable through ModeState in this version; refused structurally all the same.
            _logger.LogError("Active mode is not available before Stage 3; nothing is commanded.");
            _activeLogged = true;
        }

        var decisions = ImmutableArray.CreateBuilder<Decision>();
        foreach (var e in evaluation.Events)
        {
            decisions.Add(new Decision(
                _ids.Next(),
                now,
                e.Scope,
                null,
                null,
                e.Phase,
                e.Action,
                e.Reason,
                null,
                OperatingMode.Shadow,
                false,
                null,
                e.Inputs));
        }

        foreach (var intent in evaluation.Intents)
        {
            if (Gate(intent, evaluation, now) is { } decision)
            {
                decisions.Add(decision);
            }
        }

        return decisions.ToImmutable();
    }

    private Decision? Gate(GuardIntent intent, GuardEvaluation evaluation, DateTimeOffset now)
    {
        var stage = intent.Stage ?? 1;
        var overridden = intent.Overridden;
        ReasonCode? refusedBy = null;
        if (intent.Urgency == IntentUrgency.Protective)
        {
            // Lockouts never outrank protection; the record says which one was outranked.
            if (intent.Target == GuardTarget.Off && overridden is null && _lockouts.MinOnRemaining(intent.Device, stage, now) > TimeSpan.Zero)
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

        // Shadow is the only mode this version has: every decision is recorded, none is commanded.
        refusedBy ??= ReasonCode.ModeShadow;
        var recorded = new Recorded(intent.Target, intent.Reason, refusedBy.Value);
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
            OperatingMode.Shadow,
            false,
            refusedBy,
            intent.Inputs);
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

    private readonly record struct Recorded(GuardTarget Target, ReasonCode Reason, ReasonCode RefusedBy);
}
