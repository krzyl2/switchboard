using Switchboard.Domain.Units;

namespace Switchboard.Domain.Devices;

/// <summary>The state a device reports it is in (its applied state), read through the signal layer.</summary>
public abstract record AppliedState;

/// <summary>A relay stage reported on or off.</summary>
public sealed record RelayApplied(bool On) : AppliedState
{
    /// <summary>A select stop actuator's reported option, as written (on is any option but the stop option); null for a switch.</summary>
    public OptionText? Option { get; init; }
}

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
/// state is <see cref="Fresh"/>: a stale "off" must never exclude a device from protection, so a
/// sample that is not fresh cannot be built with an applied state.
/// </summary>
public sealed record DeviceStateSample
{
    /// <summary>The stage number under which a stop actuator's on/off state is reported (its enabled flag).</summary>
    public const int StopStage = 0;

    /// <param name="stage">The stage number, starting at 1; <see cref="StopStage"/> for a modulating device's stop actuator.</param>
    /// <exception cref="ArgumentException">An applied state of a sample that is not fresh.</exception>
    public DeviceStateSample(
        DeviceName device,
        int stage,
        bool fresh,
        DeviceStateInvalidReason? invalidReason,
        AppliedState? applied,
        DateTimeOffset? reportedAt)
    {
        if (applied is not null && !fresh)
        {
            throw new ArgumentException("A state that is not fresh carries no applied state.", nameof(applied));
        }

        Device = device;
        Stage = stage;
        Fresh = fresh;
        InvalidReason = invalidReason;
        Applied = applied;
        ReportedAt = reportedAt;
    }

    public DeviceName Device { get; }

    /// <summary>The stage number, starting at 1; <see cref="StopStage"/> for a modulating device's stop actuator.</summary>
    public int Stage { get; }

    public bool Fresh { get; }

    public DeviceStateInvalidReason? InvalidReason { get; }

    public AppliedState? Applied { get; }

    public DateTimeOffset? ReportedAt { get; }

    /// <summary>A fresh state with its applied value.</summary>
    public static DeviceStateSample Known(DeviceName device, int stage, AppliedState applied, DateTimeOffset? reportedAt) =>
        new(device, stage, true, null, applied, reportedAt);

    /// <summary>A state that is not known, and why: it carries no applied value.</summary>
    public static DeviceStateSample Unknown(DeviceName device, int stage, DeviceStateInvalidReason reason, DateTimeOffset? reportedAt) =>
        new(device, stage, false, reason, null, reportedAt);
}
