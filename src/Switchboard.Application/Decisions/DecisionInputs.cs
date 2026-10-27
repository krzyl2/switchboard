using System.Collections.Immutable;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Decisions;

/// <summary>The inputs that produced a decision (MODE-03); each part is present where it applies.</summary>
/// <param name="SafeStateCause">What caused a system-wide safe state, on its events and on the device holds it made.</param>
/// <param name="Dispatch">Headroom, hysteresis, settling and verification behind a dispatch decision.</param>
public sealed record DecisionInputs(
    PhaseDecisionInputs? Phase,
    DeviceDecisionInputs? Device,
    ModeDecisionInputs? Mode,
    KillSwitchSource? KillSwitch,
    SafeStateCause? SafeStateCause,
    DispatchDecisionInputs? Dispatch = null)
{
    public static DecisionInputs None { get; } = new(null, null, null, null, null);
}

/// <summary>
/// The phase picture at the decision. <see cref="Current"/> is the measured current (import
/// positive), <see cref="EffectiveCurrent"/> the same minus the relief of limits already decided;
/// <see cref="Multiple"/>, <see cref="Band"/> and <see cref="Reaction"/> belong to the measured magnitude.
/// </summary>
/// <param name="Headroom">To the reserve threshold, from the measured current.</param>
public sealed record PhaseDecisionInputs(
    Phase Phase,
    bool DataValid,
    ProtectionInvalidReason? InvalidReason,
    Amperes? Current,
    Amperes? EffectiveCurrent,
    double? Multiple,
    int? Band,
    TierReaction? Reaction,
    double Budget,
    Amperes Continuous,
    Amperes ReserveThreshold,
    Amperes? Headroom,
    FlowDirection? Direction,
    DateTimeOffset? ReportedAt);

/// <summary>The device stage at the decision.</summary>
/// <param name="ShedRank">Position in the shed order (1 sheds first); null for a no_shed device.</param>
/// <param name="CanSwitchOff">False for a modulating device: before Stage 3 it has no stop path, so Off cannot happen.</param>
public sealed record DeviceDecisionInputs(
    AppliedState? Applied,
    bool StateFresh,
    DeviceStateInvalidReason? StateInvalidReason,
    int? ShedRank,
    double ValuePerKwh,
    double? ShedPriority,
    TimeSpan MinOnRemaining,
    TimeSpan MinOffRemaining,
    bool CanSwitchOff);

/// <summary>
/// The mode at a mode decision (MODE-02): the add-on option (the ceiling) and its note; the control
/// switch's parts arrived with 0.7.0 and are null on lines written before it. The Stage 5 UI choice
/// of older lines is read and ignored (0.7.1).
/// </summary>
public sealed record ModeDecisionInputs(OperatingMode Ceiling, ModeNote Note)
{
    /// <summary>The global "control allowed" switch.</summary>
    public bool? ControlAllowed { get; init; }

    public bool? ConfigurationValid { get; init; }

    /// <summary>False while a corrupt state.json waits for the user's confirmation.</summary>
    public bool? StateConfirmed { get; init; }

    /// <summary>Every condition that keeps the effective mode from being active; empty when it is active.</summary>
    public ImmutableArray<ModeCondition> Missing { get; init; } = [];

    /// <summary>Who changed the control switch last.</summary>
    public ControlSwitchSource? Source { get; init; }

    public bool Equals(ModeDecisionInputs? other) =>
        other is not null
        && Ceiling == other.Ceiling
        && Note == other.Note
        && ControlAllowed == other.ControlAllowed
        && ConfigurationValid == other.ConfigurationValid
        && StateConfirmed == other.StateConfirmed
        && Source == other.Source
        && (Missing.IsDefault ? [] : Missing).SequenceEqual(other.Missing.IsDefault ? [] : other.Missing);

    public override int GetHashCode() => HashCode.Combine(Ceiling, Note, ControlAllowed, ConfigurationValid, StateConfirmed, Source);
}

/// <summary>The headroom on one constraint at a dispatch decision.</summary>
/// <param name="Envelope">E from the guard (null without valid data).</param>
/// <param name="Remaining">What was left there when the device's turn came in the allocation (null when it was not allocated).</param>
public sealed record ConstraintHeadroom(ConstraintKey Key, Amperes? Envelope, Amperes? Remaining);

/// <summary>The unit a verification compares in.</summary>
public enum MeasureUnit
{
    Amperes,
    Watts,
}

/// <summary>A verification at the close of a settling window (DISP-07).</summary>
/// <param name="Expected">The expected change (a step), or the new ceiling (a setpoint).</param>
/// <param name="Measured">The measured change, or the draw after the window.</param>
public sealed record VerificationInputs(double Expected, double Measured, MeasureUnit Unit, Verdict Verdict);

/// <summary>What a dispatch decision was made from (MODE-03).</summary>
/// <param name="StartRank">Position in the start order (1 is given headroom first); null for a no_shed device.</param>
/// <param name="Contribution">The device's share of the measured current per phase.</param>
/// <param name="InFlight">The in-flight change counted for it per constraint.</param>
/// <param name="Settling">The device's settling window at the decision.</param>
/// <param name="MinOnRemaining">On the target timeline (the lockouts dispatch is held to).</param>
/// <param name="DeferredSince">When the change was first refused, for a deferral or a deferred change applied at expiry.</param>
public sealed record DispatchDecisionInputs(
    ImmutableArray<ConstraintHeadroom> Headroom,
    Amperes StartHysteresis,
    Amperes StopHysteresis,
    int? StartRank,
    ImmutableDictionary<Phase, Amperes> Contribution,
    ImmutableDictionary<ConstraintKey, Amperes> InFlight,
    SettlingState? Settling,
    VerificationInputs? Verification,
    TimeSpan MinOnRemaining,
    TimeSpan MinOffRemaining,
    DateTimeOffset? DeferredSince,
    OverrideMode Override)
{
    /// <summary>
    /// The change repeats a protective target already decided: re-sent after Home Assistant came
    /// back, or after its last command failed. It is a command of an earlier decision, not a new one.
    /// </summary>
    public bool Reasserted { get; init; }

    /// <summary>The hourly balance as the allowance rule read it in the step (BAL-07); null while no energy counters are mapped, and on lines written before 0.9.</summary>
    public BalanceDecisionInputs? Balance { get; init; }

    /// <summary>The prices, effective value, order key and energy class the step used (ECO-09); null without an <c>economics:</c> section, and on lines written before it.</summary>
    public EconomicsDecisionInputs? Economics { get; init; }

    /// <summary>The plan entry dispatch used for the device (PLAN-15); null when <c>planner.dispatch</c> is off, when no valid entry applied, and on lines written before list version 7.</summary>
    public PlanDecisionInputs? Plan { get; init; }
}

/// <summary>
/// The hourly balance a dispatch decision was made beside (BAL-07). Wh and W with positive = export; a
/// value is null while it is not known. <paramref name="RemainingS"/> is the time to the :00 the rule used
/// (zero when the balance was not of the running hour).
/// </summary>
public sealed record BalanceDecisionInputs(double? BalanceWh, double? FreeW, double? LoadW, double? ProjectionWh, double? LoWh, double? HiWh, double RemainingS, bool Burning);

/// <summary>
/// What the economics of a dispatch decision were (ECO-09, 14-ECONOMICS): the tariff zone and the prices in force, the device's
/// effective value and the key it was ordered by, the energy class that allowed it and its sources. A price is currency per kWh
/// in the user's label, never a symbol of ours; a value is null where no such price exists.
/// </summary>
/// <param name="Zone">The tariff zone now; null for a flat or absent tariff.</param>
/// <param name="ImportPrice">What a kWh bought costs now.</param>
/// <param name="ExportPrice">What a kWh not exported earns now; null when none is configured (the surplus price test is then off).</param>
/// <param name="PriceBasis">How the import price (else the export price) came about; fallback when either price is at its fallback, else live when either is live; null when there is none.</param>
/// <param name="VEff">The device's effective value per kWh now.</param>
/// <param name="ValueBasis">static, followed or fallback.</param>
/// <param name="OrderKey">The key the device was ordered by: its shed priority where one is set, else the effective value.</param>
/// <param name="Class">The energy class that allows the device now: surplus or the active zone; null (written <c>none</c>) when neither does.</param>
/// <param name="Sources">The sources as dispatch used them (narrowed); null when the device declares none.</param>
/// <param name="Settlement">The settlement model that judged the surplus.</param>
public sealed record EconomicsDecisionInputs(
    string? Zone,
    double? ImportPrice,
    double? ExportPrice,
    PriceBasis? PriceBasis,
    double VEff,
    ValueBasis ValueBasis,
    double OrderKey,
    EnergyClass? Class,
    string? Sources,
    SettlementModelId Settlement);

/// <summary>
/// The plan entry a dispatch decision was made beside (15-PLANNER, "Reason codes (list version 7)"): which plan, which slot, what it wanted of the device
/// and how much of the slot's budget was already delivered. Only a valid, fresh entry is recorded (status is always <c>fresh</c>); strings, not planner types,
/// so nothing the guard builds a decision from reaches the planner.
/// </summary>
/// <param name="PlanId">The plan's content fingerprint.</param>
/// <param name="Want">run, defer, done or urgent.</param>
/// <param name="Class">pv or zone:&lt;id&gt;; null for a want that carries none.</param>
/// <param name="EnergyKwh">The slot's budget for the device.</param>
/// <param name="DeliveredKwh">What dispatch counted the device drawing in the slot since it began watching it (an estimate that can only fall short).</param>
/// <param name="MaxPowerW">The slot's ceiling.</param>
/// <param name="Mode">narrow or full.</param>
public sealed record PlanDecisionInputs(
    string PlanId,
    DateTimeOffset ComputedAt,
    DateTimeOffset SlotStart,
    DateTimeOffset SlotEnd,
    string Want,
    string? Class,
    double EnergyKwh,
    double DeliveredKwh,
    double MaxPowerW,
    string Mode)
{
    public const string FreshStatus = "fresh";

    public static readonly System.Collections.Frozen.FrozenSet<string> Wants = System.Collections.Frozen.FrozenSet.ToFrozenSet(["run", "defer", "done", "urgent"]);

    public static readonly System.Collections.Frozen.FrozenSet<string> Modes = System.Collections.Frozen.FrozenSet.ToFrozenSet(["narrow", "full"]);
}
