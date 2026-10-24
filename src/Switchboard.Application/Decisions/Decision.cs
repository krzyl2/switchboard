using Switchboard.Application.Modes;
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
        DecisionInputs inputs)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(inputs);
        Require(ReasonCodes.IsDefined(reason), "The reason is not a code of list version 2.");
        Require(overridden is null || ReasonCodes.IsDefined(overridden.Value), "The overridden rule is not a code of list version 2.");
        Require(refusedBy is null || ReasonCodes.IsDefined(refusedBy.Value), "The refusal is not a code of list version 2.");
        Require(Enum.IsDefined(scope) && Enum.IsDefined(action) && Enum.IsDefined(mode), "Scope, action and mode must be defined values.");
        Require(scope != DecisionScope.Device || device is not null, "A device decision names its device.");
        Require(scope != DecisionScope.Phase || phase is not null, "A phase decision names its phase.");
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

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new ArgumentException(message);
        }
    }
}
