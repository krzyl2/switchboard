using System.Collections.Immutable;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Units;

namespace Switchboard.Domain.Signals;

/// <summary>
/// One signal as diagnostics shows it. <see cref="Name"/> is null for an unmapped role slot.
/// <see cref="LastReported"/> is the freshness basis: the latest report time confirmed for the
/// value in use. <see cref="Dependents"/> are the devices protection treats as unsafe while this
/// signal is not fresh (SIG-05).
/// </summary>
public sealed record SignalReading(
    SignalName? Name,
    SignalKind Kind,
    SignalRole Role,
    Phase? Phase,
    SignalState State,
    SignalDetail Detail,
    SignalValue? Value,
    string? RawState,
    string? RawUnit,
    DateTimeOffset? LastReported,
    TimeSpan? MaxAge,
    TimeSpan? DetectedRefresh,
    int RefreshIntervals,
    DirectionInfo? Direction,
    long RejectedCount,
    ImmutableArray<DeviceName> Dependents);

/// <summary>Balance residual check of one phase (SIG-09).</summary>
/// <param name="Residual">The residual of the latest evaluation (generation + import − measured − controlled loads).</param>
/// <param name="SkippedMisaligned">Evaluations skipped because the inputs were not from the same moment.</param>
public sealed record PhaseBalance(
    Phase Phase,
    Watts? Residual,
    DateTimeOffset? LastEvaluated,
    DateTimeOffset? LastRejected,
    long Evaluations,
    long Rejections,
    long SkippedMisaligned);

/// <summary>Samples refused since start: failed the balance check, or invalid (not numeric, wrong unit, wrong sign).</summary>
public sealed record RejectionCounters(long BalanceResidual, long InvalidSample)
{
    public long Total => BalanceResidual + InvalidSample;
}

/// <summary>
/// The complete, immutable result of one store cycle; consumers never see a half-updated set.
/// Only the diagnostics view reads all of it: protection receives <see cref="Protection"/> and
/// dispatch <see cref="Dispatch"/>, through separate ports (SIG-04).
/// </summary>
public sealed record SignalSnapshot(
    long Sequence,
    DateTimeOffset TakenAt,
    bool ConfigurationValid,
    SignalSourceStatus Source,
    ConnectionProfile? Connection,
    ImmutableArray<SignalReading> Signals,
    ProtectionInputs Protection,
    DispatchInputs Dispatch,
    ImmutableArray<PhaseBalance> Balance,
    ImmutableArray<ReserveDerivation> Reserve,
    RejectionCounters Rejections)
{
    /// <summary>
    /// The configuration dispatch reads beside its port, built from the same document as the
    /// protection setup of this cycle; null without a valid configuration. It travels next to
    /// <see cref="Dispatch"/>, never inside it (dispatch inputs carry watts only, SIG-04).
    /// </summary>
    public DispatchSetup? DispatchSetup { get; init; }
}
