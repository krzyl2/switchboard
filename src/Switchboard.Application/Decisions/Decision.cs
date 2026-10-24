using Switchboard.Application.Modes;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Decisions;

/// <summary>What a decision is about.</summary>
public enum DecisionScope
{
    /// <summary>The whole installation: mode, kill switch, safe state everywhere.</summary>
    System,

    Phase,

    Device,

    /// <summary>A circuit group (CONN-03): its own safe state, reserve request or budget.</summary>
    Group,
}

/// <summary>Who decided: protection, dispatch, or a manual override (SAFE-04).</summary>
public enum DecisionOrigin
{
    Guard,
    Dispatch,
    Override,
}

/// <summary>What was decided. Device actions are protective or releases; the rest are phase or system events.</summary>
public enum DecisionAction
{
    SwitchOff,
    ReduceToMinimum,

    /// <summary>Protection no longer holds the device; it never starts anything (dispatch may, from Stage 3).</summary>
    Release,

    /// <summary>The phase is above In − reserve; dispatch is asked to reduce (04 §2 step 4).</summary>
    RequestReduction,
    ReductionRequestCleared,
    StopIncreases,
    IncreasesAllowed,
    SafeStateEntered,
    SafeStateExited,
    KillSwitchEngaged,
    KillSwitchDisengaged,
    ModeResolved,

    /// <summary>A reaction was due on a phase that exports: shedding load would only raise the current.</summary>
    CannotReduceExporting,

    /// <summary>Dispatch (or an override) starts a device or raises it a stage.</summary>
    SwitchOn,

    /// <summary>Dispatch (or an override) changes a setpoint.</summary>
    SetSetpoint,

    /// <summary>A wanted change waits for a lockout (min_on, min_off) or, for an override, for headroom; recorded once per deferral.</summary>
    Deferred,

    /// <summary>A manual override was set or cleared for a device (SAFE-04).</summary>
    OverrideSet,

    /// <summary>Repeated mismatches between expected and measured change: the device is excluded from dispatch (DISP-07).</summary>
    MarkedUnresponsive,

    /// <summary>A probe after unresponsive_retry_after matched: the device takes part in dispatch again.</summary>
    UnresponsiveCleared,

    /// <summary>A settling window reached its hard timeout with an input still silent (DISP-05).</summary>
    SettlingTimedOut,

    /// <summary>Power appeared on a phase without a command of ours (04 §4).</summary>
    ForeignLoadDetected,
}

/// <summary>
/// One recorded decision (MODE-03): what, about what, why (a reason code from the closed list of
/// 04 §6), in which mode, whether it was commanded, and the inputs that produced it. A decision
/// cannot exist without a defined reason: the constructor refuses one, so a decision without a
/// reason is a construction error, never a log line.
/// </summary>
public sealed record Decision
{
    /// <param name="overridden">A rule protection outranked to take this decision (a min_on lockout).</param>
    /// <param name="commanded">Whether a command was sent. Always false before Stage 3: nothing can be commanded.</param>
    /// <param name="refusedBy">Why it was not commanded (mode_shadow, a lockout); null for events that are not commands.</param>
    /// <param name="origin">Who decided a device change: protection, dispatch or a manual override.</param>
    /// <param name="group">The circuit group a decision is about or was caused by.</param>
    /// <param name="target">The level the device is targeted at after this decision.</param>
    public Decision(
        string id,
        DateTimeOffset at,
        DecisionScope scope,
        DeviceName? device,
        int? stage,
        Phase? phase,
        DecisionAction action,
        ReasonCode reason,
        ReasonCode? overridden,
        OperatingMode mode,
        bool commanded,
        ReasonCode? refusedBy,
        DecisionInputs inputs,
        DecisionOrigin? origin = null,
        GroupName? group = null,
        DeviceLevel? target = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(inputs);
        Require(ReasonCodes.IsDefined(reason), "The reason is not a code of list version 2.");
        Require(overridden is null || ReasonCodes.IsDefined(overridden.Value), "The overridden rule is not a code of list version 2.");
        Require(refusedBy is null || ReasonCodes.IsDefined(refusedBy.Value), "The refusal is not a code of list version 2.");
        Require(Enum.IsDefined(scope) && Enum.IsDefined(action) && Enum.IsDefined(mode), "Scope, action and mode must be defined values.");
        Require(scope != DecisionScope.Device || device is not null, "A device decision names its device.");
        Require(scope != DecisionScope.Phase || phase is not null, "A phase decision names its phase.");
        Require(scope != DecisionScope.Group || group is not null, "A group decision names its group.");
        Require(origin is null || Enum.IsDefined(origin.Value), "The origin must be a defined value.");
        Require(stage is null or > 0, "Stage numbers start at 1.");

        Id = id;
        At = at;
        Scope = scope;
        Device = device;
        Stage = stage;
        Phase = phase;
        Action = action;
        Reason = reason;
        Overridden = overridden;
        Mode = mode;
        Commanded = commanded;
        RefusedBy = refusedBy;
        Inputs = inputs;
        Origin = origin;
        Group = group;
        Target = target;
    }

    /// <summary>Unique within the log: the process's boot id plus a sequence number.</summary>
    public string Id { get; }

    public DateTimeOffset At { get; }

    public DecisionScope Scope { get; }

    public DeviceName? Device { get; }

    /// <summary>The stage number of a stepped device; null for a device with one stage.</summary>
    public int? Stage { get; }

    public Phase? Phase { get; }

    public DecisionAction Action { get; }

    public ReasonCode Reason { get; }

    public ReasonCode? Overridden { get; }

    public OperatingMode Mode { get; }

    public bool Commanded { get; }

    public ReasonCode? RefusedBy { get; }

    public DecisionInputs Inputs { get; }

    public DecisionOrigin? Origin { get; }

    public GroupName? Group { get; }

    /// <summary>The device's target level after the decision; null for events and for decisions written before Stage 3.</summary>
    public DeviceLevel? Target { get; }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new ArgumentException(message);
        }
    }
}
