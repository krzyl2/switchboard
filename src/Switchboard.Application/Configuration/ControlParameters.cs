using System.Collections.Immutable;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Configuration;

/// <summary>
/// Every control parameter, bound from the embedded defaults file merged with <c>control:</c> in
/// the user's document. There is no default in code: a key missing from both is an error.
/// Stage 1 consumes the signal and reserve parameters; the rest are bound and validated so later
/// stages start from the same source.
/// </summary>
public sealed record ControlParameters(
    ConnectionDefaults Connection,
    BreakerParameters Breaker,
    BudgetParameters Budget,
    DispatchParameters Dispatch,
    SettlingParameters Settling,
    SignalParameters Signals,
    ReserveDerivationParameters ReserveDerivation);

public sealed record ConnectionDefaults(Watts DefaultReserve);

/// <summary>Continuous-safety multiple and tier table per breaker characteristic (04 §1).</summary>
public sealed record BreakerParameters(
    ImmutableDictionary<BreakerCharacteristic, double> ContinuousMultiple,
    ImmutableDictionary<BreakerCharacteristic, TierTable> Tiers);

/// <summary>Overload budget with thermal memory (04 §1).</summary>
public sealed record BudgetParameters(
    TimeSpan RecoveryTime,
    double StopIncreasesBelow,
    double ShedOneBelow,
    double TopTierAtOrBelow);

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
public sealed record SignalParameters(
    ImmutableDictionary<SignalKind, double> DefaultSigma,
    DirectionParameters Direction,
    RefreshDetectionParameters Refresh,
    FreshnessProbeParameters FreshnessProbe,
    BalanceParameters Balance,
    TimeSpan MaxAgeLimit);

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
