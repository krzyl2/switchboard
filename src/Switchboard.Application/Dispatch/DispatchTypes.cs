using System.Collections.Immutable;
using Switchboard.Application.Balance;
using Switchboard.Application.Decisions;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Domain.Balance;
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
    ModeStatus Mode)
{
    /// <summary>The active phase self-test running now, if any: its device is left alone and every other increase waits (change_limit).</summary>
    public SelfTestTarget? SelfTest { get; init; }

    /// <summary>The balance of the running hour as the monitor last saw it; null while no energy counters are mapped.</summary>
    public HourlyBalanceStatus? Balance { get; init; }
}

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
public sealed record PhaseDispatchState(Phase Phase, Watts? Foreign, Watts? Baseline, int? Rank)
{
    /// <summary>
    /// Read-only exposure: Σ of every declared device's power on the phase (storage included) as this
    /// step counted it for foreign load (house load − ours); null while one of them is unknown.
    /// </summary>
    public Watts? Ours { get; init; }
}

/// <summary>Where a device's power figure comes from.</summary>
public enum PowerSource
{
    /// <summary>Its own fresh power draw.</summary>
    Measured,

    /// <summary>A relay's rated power while it reports on (0 W while off): the model, never a measurement.</summary>
    RatedModel,

    /// <summary>Not known: a stale draw, an unknown state, or a setpoint device without a power draw.</summary>
    Unknown,
}

/// <summary>A device's power on one phase as foreign load detection counted it (read-only exposure).</summary>
public sealed record DevicePhasePower(DeviceName Device, Phase Phase, Watts? Power, PowerSource Source);

public sealed record DispatchEvaluation(
    DateTimeOffset At,
    ImmutableArray<DispatchWish> Wishes,
    ImmutableArray<DispatchEvent> Events,
    ImmutableArray<DeviceDispatchState> Devices,
    ImmutableArray<PhaseDispatchState> Phases,
    ImmutableArray<Phase> Ranking)
{
    /// <summary>Read-only exposure: every declared device's power per phase, as this step counted it for foreign load.</summary>
    public ImmutableArray<DevicePhasePower> DevicePowers { get; init; } = [];

    /// <summary>Read-only exposure: the hourly balance as the allowance rule saw it this step; null while no energy counters are mapped.</summary>
    public BalanceView? Balance { get; init; }

    public static DispatchEvaluation Empty(DateTimeOffset at) => new(at, [], [], [], [], []);

    public DispatchWish? Wish(DeviceName device) => Wishes.FirstOrDefault(w => w.Device == device);
}

/// <summary>
/// The hourly balance as the allowance rule read it in one step (BAL-02 … BAL-04): what it was given and
/// what it concluded. Every Wh and W value is null while it is not known; with any of the three inputs
/// unknown the allowance is zero.
/// </summary>
/// <param name="BalanceWh">The balance of the running hour, positive = net export.</param>
/// <param name="Free">Free power: grid net export plus every own load added back, smoothed.</param>
/// <param name="Load">What Switchboard's own devices draw now.</param>
/// <param name="UntilNext">Time to the next local :00; zero when the balance is not of the running hour.</param>
/// <param name="Band">The target band; null while the hour is not known.</param>
/// <param name="ProjectionWh">The balance at the next :00 if the load stays as it is.</param>
public sealed record BalanceView(double? BalanceWh, Watts? Free, Watts? Load, TimeSpan UntilNext, BalanceBand? Band, double? ProjectionWh)
{
    public bool Known => BalanceWh is not null && Free is not null && Load is not null && Band is not null;

    /// <summary>What a decision records of this view (BAL-07).</summary>
    public BalanceDecisionInputs ToDecisionInputs() => new(BalanceWh, Free?.Value, Load?.Value, ProjectionWh, Band?.LoWh, Band?.HiWh, UntilNext.TotalSeconds, Band?.Burning ?? false);
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
    ImmutableArray<VerificationRecord> History)
{
    /// <summary>What dispatch may do beyond restoring (<c>start:</c>).</summary>
    public required StartPolicy Start { get; init; }

    /// <summary>What Switchboard reduced the device from (restore_only restores up to it); null when nothing is to be restored.</summary>
    public StartBaseline? Baseline { get; init; }

    /// <summary>Since when an acknowledged command was not confirmed by the device's reported state (a visible warning).</summary>
    public DateTimeOffset? UnconfirmedSince { get; init; }

    /// <summary>When dispatch switched the device on under start: headroom.</summary>
    public DateTimeOffset? SelfStarted { get; init; }
}

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
/// Dispatch at one moment. <see cref="CommandPath"/> is true while commands leave (effective mode
/// active, or the safe state's offs during an invalid configuration); otherwise decisions are
/// computed and logged and nothing is switched.
/// </summary>
public sealed record DispatchStatus(
    DateTimeOffset At,
    ModeStatus Mode,
    bool CommandPath,
    ImmutableArray<DispatchPhaseStatus> Phases,
    ImmutableArray<DispatchDeviceStatus> Devices)
{
    /// <summary>Read-only exposure: the hourly balance as the allowance rule read it in this step (BAL-07); null while no energy counters are mapped.</summary>
    public BalanceView? Balance { get; init; }

    public static DispatchStatus Initial(DateTimeOffset at, ModeStatus mode) => new(at, mode, false, [], []);
}

public interface IDispatchStatusSource
{
    DispatchStatus Current { get; }

    event EventHandler<DispatchStatus>? Published;
}
