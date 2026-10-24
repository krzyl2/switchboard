using System.Collections.Immutable;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Configuration;

/// <summary>
/// Every control parameter, bound from the embedded defaults file merged with <c>control:</c> in
/// the user's document. There is no default in code: a key missing from both is an error.
/// <see cref="Limits"/> is the exception to the merge: the safety envelope is read from the defaults
/// file only, and <c>control.limits</c> is refused.
/// </summary>
public sealed record ControlParameters(
    ConnectionDefaults Connection,
    BreakerParameters Breaker,
    BudgetParameters Budget,
    DispatchParameters Dispatch,
    SettlingParameters Settling,
    SignalParameters Signals,
    ReserveDerivationParameters ReserveDerivation,
    DeviceDefaults Devices,
    DecisionLogParameters DecisionLog,
    SafetyEnvelope Limits);

public sealed record ConnectionDefaults(Watts DefaultReserve);

/// <summary>Continuous-safety multiple and tier table per breaker characteristic (04 §1).</summary>
public sealed record BreakerParameters(
    ImmutableDictionary<BreakerCharacteristic, double> ContinuousMultiple,
    ImmutableDictionary<BreakerCharacteristic, TierTable> Tiers);

/// <summary>Dispatch loop parameters (04 §3 and §7).</summary>
public sealed record DispatchParameters(
    TimeSpan Cycle,
    Watts StartHysteresis,
    Watts StopHysteresis,
    TimeSpan RelayMinOn,
    TimeSpan RelayMinOff,
    int MaxStepChangeStages,
    int PhaseRankingStabilityCycles,
    KilowattHours BalanceCredit,
    int PersistenceUpCycles,
    int PersistenceDownCycles,
    int MaxSimultaneousIncreases);

public sealed record SettlingParameters(TimeSpan HardTimeout);

/// <summary>Signal-layer parameters (SIG-03, SIG-05 … SIG-09).</summary>
/// <param name="MaxAgeLimit">The longest max_age a signal may declare (a frozen sensor counts as fresh for its max_age).</param>
/// <param name="CurrentLagPolls">Polls by which a meter's current may lag its own power; counted as blind time in the reserve derivation.</param>
public sealed record SignalParameters(
    ImmutableDictionary<SignalKind, double> DefaultSigma,
    DirectionParameters Direction,
    RefreshDetectionParameters Refresh,
    FreshnessProbeParameters FreshnessProbe,
    BalanceParameters Balance,
    TimeSpan MaxAgeLimit,
    int CurrentLagPolls);

/// <summary>Defaults for what a device declaration leaves out.</summary>
/// <param name="StateMaxAge">Freshness of a device's applied-state signal when the device sets none (SIG-05).</param>
/// <param name="ReactionTime">From a command to the load changing, per device type; the shed order prefers the faster.</param>
/// <param name="ModulatingMinOn">Lockouts of a modulating device; relays use the dispatch relay lockouts.</param>
public sealed record DeviceDefaults(
    TimeSpan StateMaxAge,
    ImmutableDictionary<DeviceType, TimeSpan> ReactionTime,
    TimeSpan ModulatingMinOn,
    TimeSpan ModulatingMinOff);

/// <param name="RetentionDays">Daily decision files older than this are deleted (MODE-03, MODE-04).</param>
public sealed record DecisionLogParameters(int RetentionDays);

/// <summary>
/// The safety envelope of the tier table, built from the IEC 60898 thermal points: no
/// configuration may move a band outside it (CLAUDE.md: protection outranks everything).
/// </summary>
/// <param name="ContinuousMultipleMax">Characteristics B and C may claim at most this continuous multiple (1.13, conventional non-tripping current).</param>
/// <param name="UnknownContinuousMultipleMax">An unknown characteristic gets no credit for the curve (BRK-05).</param>
/// <param name="MayAddUpToMax">A may_add band ends at or below this multiple (rated current).</param>
/// <param name="OpenBandFromMax">The last finite band ends at or below this multiple (1.45, conventional tripping current): above it everything is shed at once.</param>
/// <param name="BudgetRecoveryTimeMin">The overload budget recovers no faster than this (BRK-02): a bimetal cools slowly.</param>
/// <param name="BudgetStopIncreasesBelowMin">budget.stop_increases_below may be raised, never lowered below this.</param>
/// <param name="BudgetShedOneBelowMin">budget.shed_one_below may be raised, never lowered below this.</param>
public sealed record SafetyEnvelope(
    double ContinuousMultipleMax,
    double UnknownContinuousMultipleMax,
    double MayAddUpToMax,
    double OpenBandFromMax,
    TimeSpan BudgetRecoveryTimeMin,
    double BudgetStopIncreasesBelowMin,
    double BudgetShedOneBelowMin);

/// <param name="Deadband">Direction power within ±deadband counts as import.</param>
/// <param name="MaxSkew">How much older than the current the direction power may be reported and still pair with it.</param>
/// <param name="PairingWait">How long a changed current waits for its direction power from the same meter poll.</param>
public sealed record DirectionParameters(Watts Deadband, TimeSpan MaxSkew, TimeSpan PairingWait);

/// <param name="WindowIntervals">How many recent report intervals the estimate looks at.</param>
/// <param name="MinIntervals">Below this many intervals the refresh is reported as unknown.</param>
/// <param name="Quantile">Which quantile of the intervals is the estimate.</param>
public sealed record RefreshDetectionParameters(int WindowIntervals, int MinIntervals, double Quantile);

/// <param name="AfterFraction">A signal silent for this fraction of its max_age gets its last_reported read.</param>
public sealed record FreshnessProbeParameters(double AfterFraction);

/// <param name="ResidualThreshold">A residual below −threshold (an impossible negative load) rejects the sample.</param>
/// <param name="MaxSkew">Inputs reported further apart than this are not compared.</param>
public sealed record BalanceParameters(Watts ResidualThreshold, TimeSpan MaxSkew);
