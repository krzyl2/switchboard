namespace Switchboard.Domain.Ev;

/// <summary>
/// The manual mode of the car (EV-09). "Off" is not a mode: it is the device's existing <c>force_off</c> override, set by the user and never by the module
/// (docs/EV.md section 3, point 3).
/// </summary>
public enum EvMode
{
    /// <summary>The module's own rules: trip, weekly full charge, cap, floor.</summary>
    Auto,

    /// <summary>Surplus only: no grid and no floor, the trip is not planned for.</summary>
    PvOnly,

    /// <summary>A one-off full charge: the weekly goal at 100 % on any day, ending as late as the window allows; back to <see cref="Auto"/> once it is reached.</summary>
    FullCharge,
}

/// <summary>A running "charge now": it ends at its target, on unplug, or at <paramref name="ExpiresAt"/>.</summary>
public sealed record EvChargeNow(DateTimeOffset StartedAt, double TargetPercent, DateTimeOffset ExpiresAt);

/// <summary>The trip last planned for, with the target frozen for it, so a restart does not recompute it.</summary>
public sealed record EvLastTrip(string TripKey, double TargetPercent, double TripSocPercent, bool ExceedsElectricRange, bool Clamped, DateTimeOffset FrozenAt)
{
    public static EvLastTrip From(FrozenTarget frozen)
    {
        ArgumentNullException.ThrowIfNull(frozen);
        var t = frozen.Target;
        return new EvLastTrip(frozen.TripKey, t.Percent, t.TripSocPercent, t.ExceedsElectricRange, t.Clamped, frozen.FrozenAt);
    }

    /// <summary>The frozen target as it was, so a restart keeps the percentage and every flag instead of recomputing them.</summary>
    public FrozenTarget ToFrozen() => new(TripKey, new TargetSoc(TargetPercent, TripSocPercent, ExceedsElectricRange, Clamped), FrozenAt);
}

/// <summary>Everything the EV module remembers across a restart (<c>ev.json</c>, own file, never <c>state.json</c>).</summary>
/// <param name="LastFullAt">When the weekly full charge last reached its target (S5): one a week. Optional in <c>ev.json</c>: a file without it reads as never.</param>
public sealed record EvState(EvMode Mode, EvChargeNow? ChargeNow, KwhPerKmState Learned, EvLastTrip? LastTrip, DateTimeOffset? LastFullAt = null)
{
    public static EvState Default { get; } = new(EvMode.Auto, null, KwhPerKmState.Empty, null);
}
