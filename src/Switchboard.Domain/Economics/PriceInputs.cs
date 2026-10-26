using System.Collections.Immutable;
using Switchboard.Domain.Signals;

namespace Switchboard.Domain.Economics;

/// <summary>
/// One price signal as dispatch may read it (kind price): its state and, only while it is usable, its value in currency per kWh.
/// The unit conversion (per MWh to per kWh) and the currency label check happened in the signal layer; a sample whose unit or
/// label was wrong arrives as <see cref="SignalState.Rejected"/> without a value.
/// </summary>
/// <param name="ReportedAt">When the value in use was last reported (the freshness basis, from <c>last_reported</c>); null when none arrived.</param>
public readonly record struct PriceSample(SignalState State, SignalDetail Detail, double? PerKwh, DateTimeOffset? ReportedAt)
{
    /// <summary>Only a fresh, finite value is usable: unavailable, unknown, stale and refused samples are not (the fallback stands in).</summary>
    public bool Usable => State == SignalState.Fresh && PerKwh is { } value && double.IsFinite(value);
}

/// <summary>
/// The prices dispatch reads from the signal layer (14-ECONOMICS, slice 4), beside <c>DispatchInputs</c> and never inside
/// <c>ProtectionInputs</c>: a price must not be able to reach the guard (SIG-04 by type). Keyed by the logical signal name; the
/// entity is not known here.
/// </summary>
public sealed record PriceInputs(ImmutableDictionary<SignalName, PriceSample> Samples)
{
    public static PriceInputs Empty { get; } = new(ImmutableDictionary<SignalName, PriceSample>.Empty);

    /// <summary>The value of a price signal in currency per kWh, only while it is usable; null otherwise (never a last known value).</summary>
    public double? Usable(SignalName signal) => Samples.TryGetValue(signal, out var sample) && sample.Usable ? sample.PerKwh : null;

    /// <summary>The sample of a signal, or null when no such price signal is mapped.</summary>
    public PriceSample? Sample(SignalName signal) => Samples.TryGetValue(signal, out var sample) ? sample : null;
}
