namespace Switchboard.Domain.Time;

/// <summary>
/// A local wall-clock time as an instant. A time that does not exist (the spring gap) moves one hour on, to the first valid moment after the gap; a time that
/// happens twice (the autumn hour) is the first of the two, so a deadline is the sooner one. Never throws for a zone.
/// </summary>
public static class LocalTime
{
    /// <summary>
    /// The instant of <paramref name="local"/> in <paramref name="zone"/>, carrying the zone's offset at that moment. A gap longer than one hour (none exists in
    /// the tz database) is not moved twice: the shifted time is read with the zone's standard offset.
    /// </summary>
    public static DateTimeOffset At(DateTime local, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return Resolve(Shift(local, zone), zone);
    }

    /// <summary>As <see cref="At"/>, but false when the time is still missing after the shift (a gap longer than one hour): the caller refuses it.</summary>
    public static bool TryAt(DateTime local, TimeZoneInfo zone, out DateTimeOffset instant)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var shifted = Shift(local, zone);
        if (zone.IsInvalidTime(shifted))
        {
            instant = default;
            return false;
        }

        instant = Resolve(shifted, zone);
        return true;
    }

    private static DateTime Shift(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return zone.IsInvalidTime(local) ? local.AddHours(1) : local;
    }

    private static DateTimeOffset Resolve(DateTime local, TimeZoneInfo zone)
    {
        // The larger of the two offsets of an ambiguous time is the earlier instant.
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }
}
