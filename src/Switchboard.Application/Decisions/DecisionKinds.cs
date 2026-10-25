namespace Switchboard.Application.Decisions;

/// <summary>What a decision is about, for filtering the decision log (closed list).</summary>
public enum DecisionKind
{
    /// <summary>The guard: holds, sheds, releases, reserve requests, phase and group safe states.</summary>
    Protection,

    /// <summary>Dispatch's starts, increases, reductions and deferrals.</summary>
    Dispatch,

    /// <summary>A manual override set or cleared, and the changes it caused.</summary>
    Override,

    /// <summary>Effector verification: unresponsive marks and clears, settling timeouts.</summary>
    Verification,

    /// <summary>Power that appeared on a phase without a change of ours.</summary>
    ForeignLoad,

    /// <summary>The whole installation: mode, kill switch, system-wide safe state.</summary>
    System,
}

public static class DecisionKinds
{
    /// <summary>
    /// The kind of a decision. Action and scope decide first, origin last: a foreign load event
    /// carries origin dispatch and a verification record may carry one too, but neither is a
    /// dispatch change. Order: system events, verification, foreign load, overrides, then origin
    /// dispatch (a deferral without an origin is dispatch's too), and everything else is protection.
    /// </summary>
    public static DecisionKind Of(Decision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return decision.Action switch
        {
            DecisionAction.ModeResolved or DecisionAction.KillSwitchEngaged or DecisionAction.KillSwitchDisengaged => DecisionKind.System,
            DecisionAction.SafeStateEntered or DecisionAction.SafeStateExited when decision.Scope == DecisionScope.System => DecisionKind.System,
            DecisionAction.MarkedUnresponsive or DecisionAction.UnresponsiveCleared or DecisionAction.SettlingTimedOut => DecisionKind.Verification,
            DecisionAction.ForeignLoadDetected => DecisionKind.ForeignLoad,
            DecisionAction.OverrideSet => DecisionKind.Override,
            _ when decision.Origin == DecisionOrigin.Override => DecisionKind.Override,
            _ when decision.Origin == DecisionOrigin.Dispatch => DecisionKind.Dispatch,
            DecisionAction.Deferred when decision.Origin is null => DecisionKind.Dispatch,
            _ => DecisionKind.Protection,
        };
    }
}
