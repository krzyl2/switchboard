using System.Collections.Immutable;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Units;

namespace Switchboard.Domain.Signals;

/// <summary>Why a phase has no valid protection data; every one of them means "unsafe" to the guard.</summary>
public enum ProtectionInvalidReason
{
    ConfigurationInvalid,
    SourceDisconnected,
    CurrentAwaiting,
    CurrentMissing,
    CurrentUnavailable,
    CurrentStale,
    CurrentRejected,
}

/// <summary>
/// One fresh current measurement. <see cref="Signed"/> follows the internal convention (import
/// positive); in cautious mode (direction unknown or pending) it is the magnitude, counted as import.
/// </summary>
public sealed record CurrentReading(
    Amperes Signed,
    Amperes Magnitude,
    FlowDirection Direction,
    DirectionSource DirectionSource,
    DateTimeOffset ReportedAt);

public sealed record PhaseProtection(Phase Phase, bool DataValid, ProtectionInvalidReason? InvalidReason, CurrentReading? Current);

/// <summary>
/// One phase of a circuit group (CONN-03). A sub-metered group carries its own current reading; an
/// estimated one (<see cref="Estimated"/>) carries none, and the guard derives its worst-case
/// current from the members' states and draws in the same snapshot.
/// </summary>
public sealed record GroupProtection(GroupName Group, Phase Phase, bool DataValid, ProtectionInvalidReason? InvalidReason, CurrentReading? Current, bool Estimated);

/// <summary>The summed current of the three_phase_summed topology.</summary>
public sealed record TotalProtection(bool DataValid, ProtectionInvalidReason? InvalidReason, CurrentReading? Current);

/// <summary>Why a protection snapshot carries no setup.</summary>
public enum SetupAbsence
{
    /// <summary>No configuration document has been read yet in this process.</summary>
    NotRead,
    Invalid,
    Missing,
}

/// <summary>
/// Everything protection may read, and nothing else (SIG-04): currents in amperes, never power.
/// A phase that is not listed, or not <see cref="PhaseProtection.DataValid"/>, has no valid
/// protection data and is unsafe. <see cref="Setup"/> and <see cref="DeviceStates"/> come from the
/// same store cycle, so the guard never pairs a device list with currents of another moment.
/// </summary>
/// <param name="Setup">The protection-relevant configuration; null when the document is invalid or missing.</param>
/// <param name="DeviceStates">The applied state of every declared device stage (SIG-05: stale means unknown).</param>
/// <param name="SetupAbsence">Why <paramref name="Setup"/> is null; null when it is present.</param>
public sealed record ProtectionInputs(
    long Sequence,
    DateTimeOffset TakenAt,
    Topology? Topology,
    ImmutableArray<PhaseProtection> Phases,
    TotalProtection? Total,
    ProtectionSetup? Setup,
    ImmutableArray<DeviceStateSample> DeviceStates,
    SetupAbsence? SetupAbsence)
{
    /// <summary>The declared draw currents of devices, per phase (a stale draw carries no value and is never relief).</summary>
    public ImmutableArray<DeviceDrawSample> DeviceDraws { get; init; } = [];

    /// <summary>The phases of every declared circuit group.</summary>
    public ImmutableArray<GroupProtection> Groups { get; init; } = [];

    public PhaseProtection? For(Phase phase) => Phases.FirstOrDefault(p => p.Phase == phase);

    public bool IsDataValid(Phase phase) => For(phase) is { DataValid: true };
}
