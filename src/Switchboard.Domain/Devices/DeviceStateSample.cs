using Switchboard.Domain.Units;

namespace Switchboard.Domain.Devices;

/// <summary>The state a device reports it is in (its applied state), read through the signal layer.</summary>
public abstract record AppliedState;

/// <summary>A relay stage reported on or off.</summary>
public sealed record RelayApplied(bool On) : AppliedState;

/// <summary>A modulating device's reported setpoint, in amperes.</summary>
public sealed record SetpointApplied(Amperes Setpoint) : AppliedState;

/// <summary>
/// Why a device's applied state is not known. Protection then treats the stage as possibly on: it
/// holds it off and keeps it a shed candidate at full rating, but never counts holding it as relief.
/// </summary>
public enum DeviceStateInvalidReason
{
    ConfigurationInvalid,
    SourceDisconnected,
    Awaiting,
    Missing,
    Unavailable,
    Stale,

    /// <summary>A state that is neither on nor off, or a setpoint that is not a non-negative current.</summary>
    Unrecognized,
}

/// <summary>
/// One stage's applied state as protection sees it. <see cref="Applied"/> is null unless the
/// state is <see cref="Fresh"/>: a stale "off" must never exclude a device from protection.
/// </summary>
/// <param name="Stage">The stage number, starting at 1; <see cref="StopStage"/> for a modulating device's stop actuator.</param>
public sealed record DeviceStateSample(
    DeviceName Device,
    int Stage,
    bool Fresh,
    DeviceStateInvalidReason? InvalidReason,
    AppliedState? Applied,
    DateTimeOffset? ReportedAt)
{
    /// <summary>The stage number under which a stop actuator's on/off state is reported (its enabled flag).</summary>
    public const int StopStage = 0;
}
