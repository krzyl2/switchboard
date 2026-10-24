using System.Collections.Immutable;
using Switchboard.Domain.Connection;
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

/// <summary>The summed current of the three_phase_summed topology.</summary>
public sealed record TotalProtection(bool DataValid, ProtectionInvalidReason? InvalidReason, CurrentReading? Current);

/// <summary>
/// Everything protection may read, and nothing else (SIG-04): currents in amperes, never power.
/// A phase that is not listed, or not <see cref="PhaseProtection.DataValid"/>, has no valid
/// protection data and is unsafe.
/// </summary>
public sealed record ProtectionInputs(
    long Sequence,
    DateTimeOffset TakenAt,
    Topology? Topology,
    ImmutableArray<PhaseProtection> Phases,
    TotalProtection? Total)
{
    public PhaseProtection? For(Phase phase) => Phases.FirstOrDefault(p => p.Phase == phase);

    public bool IsDataValid(Phase phase) => For(phase) is { DataValid: true };
}
