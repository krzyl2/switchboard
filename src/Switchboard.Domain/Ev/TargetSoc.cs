namespace Switchboard.Domain.Ev;

/// <summary>
/// The SoC a trip needs at departure. <paramref name="TripSocPercent"/> is what the trip uses; <paramref name="ExceedsElectricRange"/> is set when
/// the trip is longer than a full battery carries (a PHEV finishes on fuel: not an error, the target is 100 %); <paramref name="Clamped"/> when the
/// rule asked for more than 100 %.
/// </summary>
public sealed record TargetSoc(double Percent, double TripSocPercent, bool ExceedsElectricRange, bool Clamped);

/// <summary>A target kept for one trip so that a drifting kWh/km does not move it (B3).</summary>
public sealed record FrozenTarget(string TripKey, TargetSoc Target, DateTimeOffset FrozenAt);

/// <summary>
/// The target SoC rule (EV-05). With trip_soc = km x kWh/km / capacity x 100:
/// <c>target = clamp(max(floor, trip_soc + floor_after + reserve), 100)</c>. A trip of 50 points needs 90 %, one of 60 points needs 100 %.
/// The trip target overrides the surplus cap. Charging losses do not change the target (it is a state of charge); they scale the energy that
/// must be drawn, see <see cref="EnergyToDrawKwh"/>. The ready margin is not part of the target: it is the planner goal's margin (the deadline
/// stays the trip start).
/// </summary>
public static class TargetSocRule
{
    // Numeric noise only, not a control parameter.
    private const int NoiseDigits = 9;

    public static TargetSoc ForTrip(double tripKm, double kwhPerKm, double capacityKwh, EvParameters p)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (!double.IsFinite(tripKm) || tripKm < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tripKm));
        }

        if (!double.IsFinite(kwhPerKm) || kwhPerKm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(kwhPerKm));
        }

        if (!double.IsFinite(capacityKwh) || capacityKwh <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityKwh));
        }

        // Rounded to a billionth of a point: 54 km x 0.20 / 18 is 60.00000000000001 in floating point, and that noise must not make the spec's
        // "60 % needs 100 %" row look clamped, or a trip of exactly a full battery look longer than the range.
        var trip = Math.Round(tripKm * kwhPerKm / capacityKwh * 100, NoiseDigits);
        var wanted = Math.Max(p.FloorPercent, trip + p.FloorAfterPercent + p.ReservePoints);
        return new TargetSoc(Math.Min(100, wanted), trip, trip > 100, wanted > 100);
    }

    /// <summary>Energy the charger must draw to take the battery from <paramref name="socPercent"/> to <paramref name="targetPercent"/>: stored energy x (1 + loss); zero when already there.</summary>
    public static double EnergyToDrawKwh(double socPercent, double targetPercent, double capacityKwh, double lossFraction) =>
        Math.Max(0, targetPercent - socPercent) / 100 * capacityKwh * (1 + lossFraction);

    /// <summary>
    /// Keeps <paramref name="frozen"/> for the same trip until the fresh target is more than <c>target_change_threshold</c> points away from it
    /// (exactly the threshold keeps it); a different trip, or none yet, takes the fresh one. When the old target is kept, so are its flags
    /// (trip SoC, exceeds range, clamped): they describe the frozen percentage, not the fresh computation that was discarded.
    /// </summary>
    public static FrozenTarget Freeze(FrozenTarget? frozen, string tripKey, TargetSoc fresh, DateTimeOffset now, EvParameters p)
    {
        ArgumentException.ThrowIfNullOrEmpty(tripKey);
        ArgumentNullException.ThrowIfNull(fresh);
        ArgumentNullException.ThrowIfNull(p);
        return frozen is not null
            && string.Equals(frozen.TripKey, tripKey, StringComparison.Ordinal)
            && Math.Abs(fresh.Percent - frozen.Target.Percent) <= p.TargetChangeThresholdPoints
                ? frozen
                : new FrozenTarget(tripKey, fresh, now);
    }

    /// <summary>
    /// Whether the weekly full charge is allowed on this local calendar day (amendment 2a, data only). The day is the add-on zone's, so the
    /// 23- and 25-hour days still have exactly one date; a holiday counts as a weekend day, which only matters when a weekend day is allowed.
    /// </summary>
    public static bool WeeklyFullAllowed(DateTimeOffset now, TimeZoneInfo zone, bool isHoliday, EvParameters p)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(p);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var days = p.WeeklyFull.Days;
        return days.Contains(local.DayOfWeek) || (isHoliday && (days.Contains(DayOfWeek.Saturday) || days.Contains(DayOfWeek.Sunday)));
    }
}
