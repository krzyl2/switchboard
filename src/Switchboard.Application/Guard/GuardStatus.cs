using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Modes;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Guard;

public enum PhaseGuardState
{
    Unknown,
    Normal,

    /// <summary>In the hold band, or the budget is below stop_increases_below: nothing may be added.</summary>
    Hold,

    /// <summary>A reduce_* band is active, or dispatch is asked to reduce (reserve threshold).</summary>
    Reducing,

    /// <summary>A shed_* band, an expired band timer or the budget is shedding.</summary>
    Shedding,

    /// <summary>No valid protection data: everything controlled on the phase is held off.</summary>
    SafeState,
}

public enum DeviceGuardState
{
    Allowed,

    /// <summary>Held at its minimum setpoint.</summary>
    Reduced,

    /// <summary>Held off.</summary>
    Shed,

    /// <summary>Its state or a phase it is on has no valid data (SIG-05); held off, still a shed candidate, never counted as relief.</summary>
    Unsafe,
}

public enum BudgetTrend
{
    Full,
    Consuming,
    Recovering,

    /// <summary>No valid data: the budget holds its value.</summary>
    Held,
}

public enum SafeStateScope
{
    None,
    Phases,
    All,
}

/// <summary>What put the whole installation in safe state; recorded with the system-wide decisions (MODE-03).</summary>
public enum SafeStateCause
{
    /// <summary>The add-on is stopping (graceful stop path).</summary>
    Shutdown,

    /// <summary>No configuration document has been read yet in this process.</summary>
    Starting,

    ConfigurationInvalid,
    ConfigurationMissing,

    /// <summary>The connection to Home Assistant is down: no phase has data.</summary>
    SourceDisconnected,

    /// <summary>No phase has a valid current (stale, unavailable, missing, rejected or still awaited).</summary>
    MeterInvalid,
}

public sealed record SafeStateStatus(bool Active, SafeStateScope Scope, ReasonCode? Reason, ImmutableArray<Phase> Phases, DateTimeOffset? Since)
{
    public static SafeStateStatus None { get; } = new(false, SafeStateScope.None, null, [], null);
}

public enum KillSwitchSource
{
    Api,
    Mqtt,

    /// <summary>Restored from state.json at start.</summary>
    Restored,

    /// <summary>Engaged at start because state.json was corrupt or unreadable: held until the user confirms.</summary>
    CorruptState,

    /// <summary>Released by the user's confirmation after a corrupt state.json.</summary>
    Confirmation,
}

/// <summary>The global kill switch (SAFE-03); while engaged every controlled device is held off.</summary>
public sealed record KillSwitchStatus(bool Engaged, DateTimeOffset? ChangedAt, KillSwitchSource? Source)
{
    public static KillSwitchStatus Off { get; } = new(false, null, null);

    /// <summary>Engaged by a corrupt state.json at start and held until the user confirms: no release is accepted before.</summary>
    public bool Locked { get; init; }
}

public sealed record BreakerStatus(
    Amperes Rating,
    BreakerCharacteristic Characteristic,
    double ContinuousMultiple,
    Amperes Continuous,
    ImmutableArray<TierBand> Tiers);

/// <summary>
/// One phase as the guard sees it. Published headroom (<see cref="Headroom"/>,
/// <see cref="BreakerHeadroom"/>) is computed from the measured current, never from the effective
/// one: in shadow mode nothing is applied, and a user automation must not act on a hypothetical
/// number. The envelope dispatch obeys (from Stage 3) uses the effective current.
/// </summary>
/// <param name="Current">Measured, import positive (cautious mode: the magnitude).</param>
/// <param name="EffectiveCurrent">The estimate I_est: measured plus every counted in-flight change (would-starts added, relief never above a measured draw).</param>
/// <param name="Multiple">|I| / In of the measured current.</param>
/// <param name="Band">Tier band of the measured multiple.</param>
/// <param name="Headroom">To the reserve threshold (In − reserve), from the measured current.</param>
/// <param name="BreakerHeadroom">To the continuous current (continuous multiple × In), from the measured current.</param>
/// <param name="EnvelopeToReserve">The envelope for dispatch: to the reserve threshold from the effective current.</param>
/// <param name="EnvelopeToBreaker">The envelope for dispatch: to the continuous current from the effective current.</param>
/// <param name="RecoveryRemaining">(1 − budget) · recovery_time (BRK-04).</param>
/// <param name="ReductionRequested">Dispatch is asked to reduce on this phase (reserve threshold, persisted).</param>
public sealed record PhaseGuardStatus(
    Phase Phase,
    bool DataValid,
    ProtectionInvalidReason? InvalidReason,
    Amperes? Current,
    Amperes? EffectiveCurrent,
    double? Multiple,
    int? Band,
    TierReaction? Reaction,
    Amperes Continuous,
    Amperes ReserveThreshold,
    Amperes? Headroom,
    Amperes? BreakerHeadroom,
    Amperes? EnvelopeToReserve,
    Amperes? EnvelopeToBreaker,
    double Budget,
    BudgetTrend BudgetTrend,
    TimeSpan RecoveryRemaining,
    bool IncreasesAllowed,
    bool ReductionRequested,
    PhaseGuardState State,
    FlowDirection? Direction,
    DateTimeOffset? ReportedAt);

/// <param name="Stage">The stage number, starting at 1.</param>
/// <param name="Limit">What protection holds this stage to; null when it holds nothing.</param>
public sealed record DeviceStageStatus(int Stage, AppliedState? Applied, bool Fresh, DeviceStateInvalidReason? InvalidReason, GuardTarget? Limit);

/// <summary>One device as the guard sees it.</summary>
/// <param name="Reason">Why it is not allowed (the limit's reason, or the unsafe cause); null when allowed.</param>
/// <param name="ShedRank">Position in the shed order (1 sheds first); null for a no_shed device.</param>
/// <param name="UnsafePhases">Phases it is on whose current is not valid (their current signals are the unsafe signals).</param>
/// <param name="UnsafeStages">Stages whose state signal is not fresh.</param>
public sealed record DeviceGuardStatus(
    DeviceName Id,
    DeviceType Type,
    ImmutableArray<Phase> Phases,
    DeviceGuardState GuardState,
    ReasonCode? Reason,
    ImmutableArray<DeviceStageStatus> Stages,
    int? ShedRank,
    double ValuePerKwh,
    double? ShedPriority,
    bool NoShed,
    bool CanSwitchOff,
    TimeSpan MinOnRemaining,
    TimeSpan MinOffRemaining,
    ImmutableArray<Phase> UnsafePhases,
    ImmutableArray<int> UnsafeStages);

/// <summary>One phase of a circuit group as the guard sees it (CONN-03): the same picture as a main phase, against the group's breaker.</summary>
public sealed record GroupGuardStatus(GroupName Group, PhaseGuardStatus Phase);

/// <summary>Everything the guard knows at one moment: for the API, the MQTT entities and dispatch.</summary>
public sealed record GuardStatus(
    DateTimeOffset At,
    ModeStatus Mode,
    KillSwitchStatus KillSwitch,
    SafeStateStatus SafeState,
    BreakerStatus? Breaker,
    ImmutableArray<PhaseGuardStatus> Phases,
    ImmutableArray<DeviceGuardStatus> Devices)
{
    /// <summary>The phases of every circuit group; empty when none is declared.</summary>
    public ImmutableArray<GroupGuardStatus> Groups { get; init; } = [];

    /// <summary>Before the first evaluation: no data, so safe state everywhere.</summary>
    public static GuardStatus Initial(DateTimeOffset at, ModeStatus mode) =>
        new(at, mode, KillSwitchStatus.Off, new SafeStateStatus(true, SafeStateScope.All, ReasonCode.SafeState, [], at), null, [], []);
}

/// <summary>The headroom envelope for one phase (03: dispatch never exceeds the envelope the guard publishes).</summary>
public sealed record PhaseEnvelope(Phase Phase, bool DataValid, Amperes? EffectiveCurrent, Amperes? ToReserve, Amperes? ToBreaker, bool IncreasesAllowed, bool ReductionRequested);

public interface IGuardStatusSource
{
    GuardStatus Current { get; }

    event EventHandler<GuardStatus>? Published;
}

/// <summary>The dispatch port of the guard (Stage 3): the envelope only.</summary>
public interface IGuardEnvelopeSource
{
    ImmutableArray<PhaseEnvelope> Envelope { get; }
}

/// <summary>Holds the latest guard status; written by the guard runner, read by anyone without locking.</summary>
public sealed class GuardStatusHolder : IGuardStatusSource, IGuardEnvelopeSource
{
    private readonly ILogger<GuardStatusHolder> _logger;
    private GuardStatus _current;

    public GuardStatusHolder(GuardStatus initial, ILogger<GuardStatusHolder> logger)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
        _logger = logger;
    }

    public event EventHandler<GuardStatus>? Published;

    public GuardStatus Current => Volatile.Read(ref _current);

    public ImmutableArray<PhaseEnvelope> Envelope =>
        [.. Current.Phases.Select(p => new PhaseEnvelope(p.Phase, p.DataValid, p.EffectiveCurrent, p.EnvelopeToReserve, p.EnvelopeToBreaker, p.IncreasesAllowed, p.ReductionRequested))];

    /// <summary>
    /// Publishes on the guard's thread. A failing listener (an MQTT entity, an API waiter) is logged
    /// and skipped: it must neither stop the guard loop nor keep the other listeners from the status.
    /// </summary>
    public void Publish(GuardStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        Volatile.Write(ref _current, status);
        foreach (var handler in Published?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler<GuardStatus>)handler)(this, status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A guard status listener failed; the guard keeps running");
            }
        }
    }
}
