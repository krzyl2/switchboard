namespace Switchboard.Domain.Economics;

/// <summary>A time-of-use schedule read on the local clock of a time zone; the inputs are not read.</summary>
internal sealed class ZonedTariff : Tariff
{
    // Beyond this a zone that never differs is reported as "no change" rather than scanned for ever.
    private const int HorizonDays = 400;

    private readonly ZonedPrice _spec;
    private readonly HolidayCalendar _holidays;
    private readonly TimeZoneInfo _zone;
    private readonly int[] _boundaries;

    public ZonedTariff(ZonedPrice spec, HolidayCalendar holidays, TimeZoneInfo zone)
    {
        _spec = spec;
        _holidays = holidays;
        _zone = zone;
        _boundaries = spec.Boundaries();
    }

    public override bool IsZoned => true;

    public override TariffZone? ZoneAt(DateTimeOffset t, PriceInputs? prices = null) => Resolve(t);

    public override PricePoint? PriceAt(DateTimeOffset t, PriceInputs? prices = null) =>
        Resolve(t) is { } zone ? new PricePoint(zone.Price, PriceBasis.Zone, zone.Id) : null;

    public override TariffChange? NextChange(DateTimeOffset t, PriceInputs? prices = null)
    {
        var x = FloorToMinute(t);
        var current = Resolve(x)?.Id;
        var horizon = x.AddDays(HorizonDays);
        while (x < horizon)
        {
            x = NextCheckpoint(x);
            var zone = Resolve(x);
            if (zone is not null && zone.Id != current)
            {
                return new TariffChange(x, zone.Id);
            }
        }

        return null;
    }

    private TariffZone? Resolve(DateTimeOffset t)
    {
        var local = TimeZoneInfo.ConvertTime(t, _zone);
        return _spec.Resolve(DateOnly.FromDateTime(local.DateTime), (local.Hour * 60) + local.Minute, _holidays);
    }

    private static DateTimeOffset FloorToMinute(DateTimeOffset t) => t.AddTicks(-(t.UtcTicks % TimeSpan.TicksPerMinute));

    /// <summary>
    /// The next instant at which the zone can change: the next wall-clock boundary of any rule (midnight
    /// included), or the moment the UTC offset changes (a DST step) if that comes first.
    /// </summary>
    private DateTimeOffset NextCheckpoint(DateTimeOffset x)
    {
        var local = TimeZoneInfo.ConvertTime(x, _zone);
        var minute = (local.Hour * 60) + local.Minute;
        var next = HoursRange.MinutesPerDay;
        foreach (var boundary in _boundaries)
        {
            if (boundary > minute)
            {
                next = boundary;
                break;
            }
        }

        var steps = next - minute;
        var candidate = x.AddMinutes(steps);
        if (_zone.GetUtcOffset(candidate) == local.Offset)
        {
            return candidate;
        }

        // The offset changes inside (x, candidate]: find the first minute on the other side.
        int lo = 0, hi = steps;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (_zone.GetUtcOffset(x.AddMinutes(mid)) == local.Offset)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return x.AddMinutes(hi);
    }
}
