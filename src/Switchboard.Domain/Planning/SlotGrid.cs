using System.Collections.Immutable;

namespace Switchboard.Domain.Planning;

/// <summary>One planning slot: [<see cref="Start"/>, <see cref="End"/>), both carrying the local UTC offset of their instant.</summary>
public readonly record struct Slot(DateTimeOffset Start, DateTimeOffset End)
{
    public TimeSpan Duration => End - Start;

    public double Hours => Duration.TotalHours;
}

/// <summary>
/// The planner's slot grid (15-PLANNER, "The slot grid"): slots start on local slot boundaries and are cut at every
/// UTC-offset transition (a 23-hour day has 23 hourly slots, a 25-hour day 25, a zone with a half-hour offset or a half-hour
/// DST step still works), the first slot is shortened to <c>now</c>, and any cut instant (a goal deadline) splits the slot it
/// falls in. Pure: the zone's rules and the instants passed in, no clock.
/// </summary>
public sealed class SlotGrid
{
    // Structural safety bound (48 h of 15-minute slots is 192): a grid is never built from an absurd horizon.
    internal const int MaxSlots = 4096;

    private SlotGrid(ImmutableArray<Slot> slots, DateTimeOffset from, DateTimeOffset to, TimeSpan slotLength, TimeZoneInfo zone)
    {
        Slots = slots;
        From = from;
        To = to;
        SlotLength = slotLength;
        Zone = zone;
    }

    public ImmutableArray<Slot> Slots { get; }

    /// <summary>The instant the grid starts at (<c>now</c>).</summary>
    public DateTimeOffset From { get; }

    /// <summary>The horizon end.</summary>
    public DateTimeOffset To { get; }

    public TimeSpan SlotLength { get; }

    public TimeZoneInfo Zone { get; }

    public int Count => Slots.Length;

    /// <param name="now">The first slot starts here (the local boundary at or before it is not planned any more).</param>
    /// <param name="end">The horizon end; the last slot is cut to it. An <paramref name="end"/> not after <paramref name="now"/> gives an empty grid.</param>
    /// <param name="slot">A whole number of minutes dividing an hour.</param>
    /// <param name="cuts">Instants (deadlines) that must be a slot edge; those outside (now, end) are ignored.</param>
    public static SlotGrid Build(DateTimeOffset now, DateTimeOffset end, TimeSpan slot, TimeZoneInfo zone, IEnumerable<DateTimeOffset>? cuts = null)
    {
        ArgumentNullException.ThrowIfNull(zone);
        CheckSlot(slot);
        var inside = (cuts ?? []).Where(c => c > now && c < end).Distinct().Order().ToList();
        var slots = ImmutableArray.CreateBuilder<Slot>();
        var cursor = now;
        var cut = 0;
        while (cursor < end)
        {
            var next = NextBoundary(cursor, slot, zone);
            if (next > end)
            {
                next = end;
            }

            while (cut < inside.Count && inside[cut] <= cursor)
            {
                cut++;
            }

            if (cut < inside.Count && inside[cut] < next)
            {
                next = inside[cut];
            }

            if (slots.Count >= MaxSlots)
            {
                throw new ArgumentOutOfRangeException(nameof(end), end, $"the horizon needs more than {MaxSlots} slots");
            }

            slots.Add(new Slot(TimeZoneInfo.ConvertTime(cursor, zone), TimeZoneInfo.ConvertTime(next, zone)));
            cursor = next;
        }

        return new SlotGrid(slots.ToImmutable(), now, end, slot, zone);
    }

    /// <summary>The index of the slot containing <paramref name="t"/>, or -1 outside the grid.</summary>
    public int IndexOf(DateTimeOffset t)
    {
        for (var i = 0; i < Slots.Length; i++)
        {
            if (t >= Slots[i].Start && t < Slots[i].End)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The first local slot boundary strictly after <paramref name="t"/>, or the UTC-offset transition if that comes first.</summary>
    internal static DateTimeOffset NextBoundary(DateTimeOffset t, TimeSpan slot, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(t, zone);
        var timeOfDay = local.TimeOfDay;
        var boundary = TimeSpan.FromTicks(((timeOfDay.Ticks / slot.Ticks) + 1) * slot.Ticks);
        var candidate = t + (boundary - timeOfDay);
        return zone.GetUtcOffset(candidate) == local.Offset ? candidate : FirstChange(t, candidate, local.Offset, zone);
    }

    /// <summary>The local slot boundary at or before <paramref name="t"/>, or the UTC-offset transition if that is later.</summary>
    internal static DateTimeOffset BoundaryAtOrBefore(DateTimeOffset t, TimeSpan slot, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(t, zone);
        var timeOfDay = local.TimeOfDay;
        var floor = TimeSpan.FromTicks(timeOfDay.Ticks / slot.Ticks * slot.Ticks);
        var candidate = t - (timeOfDay - floor);
        var offset = zone.GetUtcOffset(candidate);
        return offset == local.Offset ? candidate : FirstChange(candidate, t, offset, zone);
    }

    /// <summary>The first instant in (<paramref name="from"/>, <paramref name="to"/>] whose offset is no longer <paramref name="offsetAtFrom"/>.</summary>
    private static DateTimeOffset FirstChange(DateTimeOffset from, DateTimeOffset to, TimeSpan offsetAtFrom, TimeZoneInfo zone)
    {
        var lo = from.UtcTicks;
        var hi = to.UtcTicks;
        while (hi - lo > 1)
        {
            var mid = lo + ((hi - lo) / 2);
            if (zone.GetUtcOffset(new DateTimeOffset(mid, TimeSpan.Zero)) == offsetAtFrom)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return new DateTimeOffset(hi, TimeSpan.Zero);
    }

    private static void CheckSlot(TimeSpan slot)
    {
        var whole = slot.Ticks % TimeSpan.TicksPerMinute == 0 && slot >= TimeSpan.FromMinutes(1) && slot <= TimeSpan.FromHours(1);
        if (!whole || TimeSpan.FromHours(1).Ticks % slot.Ticks != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "a slot is a whole number of minutes that divides an hour");
        }
    }
}
