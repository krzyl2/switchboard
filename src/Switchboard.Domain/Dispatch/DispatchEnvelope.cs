using System.Collections.Immutable;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Domain.Dispatch;

/// <summary>A limit dispatch must respect: a main phase (no group) or one phase of a circuit group.</summary>
public readonly record struct ConstraintKey(GroupName? Group, Phase Phase)
{
    public static ConstraintKey Main(Phase phase) => new(null, phase);

    public override string ToString() => Group is { } group ? $"{group}/{Phase}" : Phase.ToString();
}

/// <summary>
/// The headroom the guard leaves for dispatch on one constraint (03: dispatch never exceeds the
/// envelope the guard publishes). Amperes reach dispatch only through here (SIG-04).
/// </summary>
/// <param name="Available">E = min(In − reserve, may_add top × In) − I_est: what dispatch may still add.</param>
/// <param name="BreakerRoom">may_add top × In − I_est: the room a manual override may use, which ignores the reserve but never the breaker.</param>
/// <param name="EstimatedCurrent">I_est: the measured current plus every counted in-flight change.</param>
/// <param name="InFlight">Σ in-flight changes counted in <paramref name="EstimatedCurrent"/>.</param>
/// <param name="IncreasesAllowed">The guard allows adding load here (may_add band, budget not low, no safe state, no kill switch).</param>
/// <param name="ReductionRequested">The guard asks dispatch to reduce here (reserve threshold, persisted).</param>
/// <param name="BudgetLow">The overload budget is below stop_increases_below.</param>
/// <param name="CurrentReportedAt">When the current behind this entry was reported: part of every settling window's closing set.</param>
public sealed record EnvelopeEntry(
    ConstraintKey Key,
    bool DataValid,
    Amperes? Available,
    Amperes? BreakerRoom,
    Amperes? EstimatedCurrent,
    Amperes InFlight,
    bool IncreasesAllowed,
    bool ReductionRequested,
    bool BudgetLow,
    DateTimeOffset? CurrentReportedAt)
{
    /// <summary>
    /// What a manual override may still add here: everything <see cref="IncreasesAllowed"/> asks
    /// except the reserve threshold (an override may use the reserve, never the breaker: the
    /// estimate in the may_add band, the budget not low, no safe state, no kill switch).
    /// </summary>
    public bool OverrideAllowed { get; init; }
}

/// <summary>
/// One device as the load model sees it in this evaluation: its contribution to the measured
/// current per phase, and per constraint (main phase or group phase) its estimate (contribution,
/// or a circuit group's worst-case term, plus the counted in-flight change) and that change.
/// </summary>
/// <param name="Reported">The applied state as a level; null when unknown.</param>
/// <param name="Limits">What protection holds its stages to (stage number → limit).</param>
/// <param name="DrawFresh">Every declared draw-current signal is fresh (true when none is declared).</param>
public sealed record DeviceLoad(
    DeviceName Device,
    DeviceLevel? Reported,
    ImmutableDictionary<Phase, Amperes> Contribution,
    ImmutableDictionary<ConstraintKey, Amperes> Estimate,
    ImmutableDictionary<ConstraintKey, Amperes> InFlight,
    ImmutableDictionary<int, StageLimit> Limits,
    bool DrawFresh,
    ImmutableDictionary<Phase, DateTimeOffset> DrawReportedAt)
{
    public Amperes EstimateOn(ConstraintKey key) => Estimate.GetValueOrDefault(key);
}

/// <summary>Everything dispatch receives from the guard: the envelope per constraint and the load model per device.</summary>
public sealed record DispatchEnvelope(ImmutableArray<EnvelopeEntry> Entries, ImmutableArray<DeviceLoad> Devices)
{
    public static DispatchEnvelope Empty { get; } = new([], []);

    public EnvelopeEntry? For(ConstraintKey key) => Entries.FirstOrDefault(e => e.Key == key);

    public DeviceLoad? Device(DeviceName device) => Devices.FirstOrDefault(d => d.Device == device);
}
