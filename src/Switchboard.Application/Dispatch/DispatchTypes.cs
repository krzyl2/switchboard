using System.Collections.Immutable;
using Switchboard.Application.Decisions;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Dispatch;

/// <summary>
/// One dispatch evaluation's input: the dispatch port (watts and volts), the dispatch setup, the
/// guard's evaluation of the same step (its envelope is the only source of amperes, SIG-04), the
/// effector's ledger, the overrides and the lockouts.
/// </summary>
public sealed record DispatchInput(
    DispatchInputs Inputs,
    DispatchSetup? Setup,
    GuardEvaluation Guard,
    IDispatchLedgerView Ledger,
    OverrideSnapshot Overrides,
    IReadOnlyLockouts Lockouts,
    ModeStatus Mode);

public enum DispatchChange
{
    Keep,
    Increase,
    Decrease,
}

/// <summary>
/// What dispatch wants for one device after a cycle. <see cref="Fresh"/> wishes were computed at
/// this evaluation (a cycle boundary, or an override change); a stale one is the last boundary's
/// wish, still valid until the next boundary (a deferred change applied at its lockout's expiry
/// must still be wanted).
/// </summary>
/// <param name="BlockedBy">An override that cannot be applied now (headroom_insufficient, guard_budget_low): recorded once as deferred.</param>
public sealed record DispatchWish(
    DeviceName Device,
    DeviceLevel Level,
    ReasonCode Reason,
    DecisionOrigin Origin,
    DispatchChange Change,
    ReasonCode? BlockedBy,
    bool Fresh,
    DispatchDecisionInputs Inputs)
{
    /// <summary>For a blocked override: the level it would apply once allowed (what its deferral waits for); <see cref="Level"/> stays at the basis.</summary>
    public DeviceLevel? PendingLevel { get; init; }
}

/// <summary>A phase-level dispatch event worth a decision (a foreign load step).</summary>
public sealed record DispatchEvent(DecisionScope Scope, Phase? Phase, DecisionAction Action, ReasonCode Reason, DecisionInputs Inputs);

/// <summary>Why a device is where it is, as dispatch sees it (UI-10: a stopped device keeps its reason).</summary>
/// <param name="Pending">An increase dispatch wants but has not made yet: awaiting_confirmation, settling_window_open, a guard reason.</param>
public sealed record DeviceDispatchState(DeviceName Device, ReasonCode? Reason, ReasonCode? Pending, int? StartRank, DeviceLevel? Wish);

/// <summary>Foreign load and phase priority of one phase.</summary>
public sealed record PhaseDispatchState(Phase Phase, Watts? Foreign, Watts? Baseline, int? Rank);

public sealed record DispatchEvaluation(
    DateTimeOffset At,
    ImmutableArray<DispatchWish> Wishes,
    ImmutableArray<DispatchEvent> Events,
    ImmutableArray<DeviceDispatchState> Devices,
    ImmutableArray<PhaseDispatchState> Phases,
    ImmutableArray<Phase> Ranking)
{
    public static DispatchEvaluation Empty(DateTimeOffset at) => new(at, [], [], [], [], []);

    public DispatchWish? Wish(DeviceName device) => Wishes.FirstOrDefault(w => w.Device == device);
}

public enum SettlingStatus
{
    None,
    Open,
    TimedOut,
}

public sealed record DeferredStatus(DeviceLevel Target, ReasonCode Reason, ReasonCode RefusedBy, DateTimeOffset Since, DateTimeOffset? DueAt);

public sealed record UnresponsiveStatus(DateTimeOffset Since, DateTimeOffset? RetryAt);

/// <summary>A device's own draw as last read.</summary>
public sealed record DrawStatus(Amperes? Current, Watts? Power, bool Fresh);

/// <summary>One device as dispatch and the effector see it, for the API and the MQTT dispatch entity (Stage 4 builds on it).</summary>
/// <param name="Intended">The resolved target (what Switchboard would have the device at); null while unset (it follows the reported state).</param>
/// <param name="Matches">Intended and reported agree (an unset target always does).</param>
public sealed record DispatchDeviceStatus(
    DeviceName Id,
    DeviceType Type,
    ImmutableArray<Phase> Phases,
    GroupName? RotationGroup,
    GroupName? CircuitGroup,
    DeviceLevel? Intended,
    DeviceLevel? Reported,
    bool ReportedFresh,
    bool Matches,
    ReasonCode? Reason,
    DecisionOrigin? Origin,
    int? StartRank,
    ReasonCode? Pending,
    TimeSpan MinOnRemaining,
    TimeSpan MinOffRemaining,
    DeferredStatus? Deferred,
    SettlingStatus Settling,
    UnresponsiveStatus? Unresponsive,
    int Mismatches,
    DeviceOverride Override,
    DrawStatus? Draw,
    ImmutableArray<VerificationRecord> History);

public sealed record DispatchGroupEnvelope(GroupName Group, Amperes? Envelope, Amperes InFlight, bool IncreasesAllowed);

/// <param name="Envelope">E on the main phase (null without valid data).</param>
public sealed record DispatchPhaseStatus(
    Phase Phase,
    Amperes? Envelope,
    Amperes InFlight,
    bool DataValid,
    DispatchInvalidReason? InvalidReason,
    Watts? Foreign,
    Watts? ForeignBaseline,
    int? Rank,
    ImmutableArray<DispatchGroupEnvelope> Groups);

/// <summary>
/// Dispatch at one moment. <see cref="CommandPath"/> is false in this version: decisions are
/// computed and logged, nothing is switched.
/// </summary>
public sealed record DispatchStatus(
    DateTimeOffset At,
    ModeStatus Mode,
    bool CommandPath,
    ImmutableArray<DispatchPhaseStatus> Phases,
    ImmutableArray<DispatchDeviceStatus> Devices)
{
    public static DispatchStatus Initial(DateTimeOffset at, ModeStatus mode) => new(at, mode, false, [], []);
}

public interface IDispatchStatusSource
{
    DispatchStatus Current { get; }

    event EventHandler<DispatchStatus>? Published;
}
