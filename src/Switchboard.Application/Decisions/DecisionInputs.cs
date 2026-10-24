using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Decisions;

/// <summary>The inputs that produced a decision (MODE-03); each part is present where it applies.</summary>
/// <param name="SafeStateCause">What caused a system-wide safe state, on its events and on the device holds it made.</param>
public sealed record DecisionInputs(
    PhaseDecisionInputs? Phase,
    DeviceDecisionInputs? Device,
    ModeDecisionInputs? Mode,
    KillSwitchSource? KillSwitch,
    SafeStateCause? SafeStateCause)
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

public sealed record ModeDecisionInputs(OperatingMode Ceiling, OperatingMode UiChoice, ModeNote Note);
