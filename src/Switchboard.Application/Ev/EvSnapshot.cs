using Switchboard.Domain.Devices;
using Switchboard.Domain.Ev;

namespace Switchboard.Application.Ev;

/// <summary>
/// What the EV module offers at one moment (docs/EV.md B1): computed beside the control loop by <see cref="EvEngine"/>, published whole and never changed.
/// The guard's step reads it through <see cref="EvGoalSource"/> and does nothing but filter it by the clock, so a defect in the computation cannot reach
/// the guard.
/// </summary>
/// <param name="ValidUntil">The snapshot is no offer after this instant (<c>control.ev.snapshot_max_age</c> after it was computed).</param>
/// <param name="Composition">The goals, the primary assessment code and the notes.</param>
/// <param name="SocPercent">The SoC estimate the goals were made with; null when there is none.</param>
/// <param name="SocGap">Why there is no estimate.</param>
/// <param name="KwhPerKm">The consumption the target was computed with (learned, else the fallback).</param>
/// <param name="NextTrip">The trip the goals are made for (a trip whose start has passed is never here).</param>
/// <param name="PluggedUnavailable">A plugged signal is declared but has no usable value (an unplugged car is not this: nothing is reserved for it, an unreadable state leaves the goal inactive).</param>
/// <param name="LastFullAt">When the weekly full charge last reached its target.</param>
/// <param name="Plugged">True when plugged (or when no plugged signal is declared), false when unplugged, null when the state is unknown or unavailable; read by the manual goal to end on unplug.</param>
public sealed record EvSnapshot(
    DateTimeOffset ComputedAt,
    DateTimeOffset ValidUntil,
    DeviceName Device,
    EvComposition Composition,
    double? SocPercent,
    SocEstimateGap? SocGap,
    double KwhPerKm,
    Trip? NextTrip,
    EvMode Mode,
    bool PluggedUnavailable,
    DateTimeOffset? LastFullAt,
    bool? Plugged = true);

/// <summary>
/// The latest published snapshot, or the fact that computing it failed. A null <paramref name="Snapshot"/> without a failure means nothing has been computed
/// yet (or there is no <c>ev:</c> section): not a failure, there is simply no offer.
/// </summary>
public sealed record EvPublished(EvSnapshot? Snapshot, bool Failed)
{
    public static EvPublished None { get; } = new(null, false);
}

/// <summary>Where the module's published snapshot is read from; read without locking.</summary>
public interface IEvSnapshotSource
{
    EvPublished Current { get; }
}
