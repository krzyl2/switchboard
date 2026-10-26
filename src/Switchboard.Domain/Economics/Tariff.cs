namespace Switchboard.Domain.Economics;

/// <summary>The next moment a tariff's zone differs from the current one, and the zone it changes to.</summary>
public readonly record struct TariffChange(DateTimeOffset At, string Zone);

/// <summary>
/// A price spec evaluated on the local clock of a time zone: the instant is converted with the zone's rules,
/// so a 23-hour day has no 02:xx and a 25-hour day has 02:xx twice, and a rule 02:00-03:00 matches both.
/// Pure: no clock inside (the caller passes the instant), no entity, no vendor. A dynamic spec reads its price signal from the
/// <see cref="PriceInputs"/> the caller passes: live (signal plus add) while that value is usable, else the static fallback, never
/// the last known value; without inputs a dynamic spec is at its fallback.
/// </summary>
public sealed class Tariff
{
    // Beyond this a zone that never differs is reported as "no change" rather than scanned for ever.
    private const int HorizonDays = 400;

    private readonly PriceSpec _spec;
    private readonly HolidayCalendar _holidays;
    private readonly TimeZoneInfo _zone;
    private readonly int[] _boundaries;
    private readonly DynamicPrice? _dynamic;
    private readonly Tariff? _add;
    private readonly Tariff? _fallback;

    public Tariff(PriceSpec spec, HolidayCalendar holidays, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(holidays);
        ArgumentNullException.ThrowIfNull(zone);
        _spec = spec;
        _holidays = holidays;
        _zone = zone;
        _boundaries = spec is ZonedPrice zoned ? zoned.Boundaries() : [0];
        if (spec is DynamicPrice dynamic)
        {
            _dynamic = dynamic;
            _fallback = new Tariff(dynamic.Fallback, holidays, zone);
            _add = dynamic.Add is { } add ? new Tariff(add, holidays, zone) : null;
        }
    }

    public bool IsZoned => _spec is ZonedPrice || _add?.IsZoned == true || _fallback?.IsZoned == true;

    /// <summary>The zone in force at <paramref name="t"/>; null for a flat price (and for a moment no zone claims, which a validated schedule has none of).</summary>
    /// <param name="prices">For a dynamic spec: the zone is the add's while the price is live, the fallback's while it is not.</param>
    public TariffZone? ZoneAt(DateTimeOffset t, PriceInputs? prices = null) =>
        _dynamic is not null ? Schedule(t, prices)?.ZoneAt(t) : _spec is ZonedPrice zoned ? Resolve(zoned, t) : null;

    /// <summary>The import (or export) price at <paramref name="t"/>; null only for a moment no zone claims.</summary>
    /// <param name="prices">The price signals; only a dynamic spec reads them.</param>
    public PricePoint? PriceAt(DateTimeOffset t, PriceInputs? prices = null)
    {
        if (_dynamic is { } dynamic)
        {
            return Dynamic(dynamic, t, prices);
        }

        if (_spec is FlatPrice flat)
        {
            return new PricePoint(flat.PerKwh, PriceBasis.Flat, null);
        }

        return ZoneAt(t) is { } zone ? new PricePoint(zone.Price, PriceBasis.Zone, zone.Id) : null;
    }

    /// <summary>
    /// A dynamic price: the signal plus add while the signal is usable and the sum is a price (finite, not negative: a market may
    /// publish a negative value, but a negative total is not a price the rest of the system can compare, so it is not guessed
    /// at and the fallback stands in); otherwise the fallback spec's price with the basis fallback.
    /// </summary>
    private PricePoint? Dynamic(DynamicPrice dynamic, DateTimeOffset t, PriceInputs? prices)
    {
        if (prices?.Usable(dynamic.Signal) is { } live)
        {
            var add = _add?.PriceAt(t);
            if (_add is null || add is not null)
            {
                var total = live + (add?.PerKwh ?? 0);
                if (double.IsFinite(total) && total >= 0)
                {
                    return new PricePoint(total, PriceBasis.Live, add?.Zone);
                }
            }
        }

        return _fallback!.PriceAt(t) is { } fallback ? fallback with { Basis = PriceBasis.Fallback } : null;
    }

    /// <summary>The schedule that names the zone now: for a dynamic spec the add while live, the fallback otherwise.</summary>
    private Tariff? Schedule(DateTimeOffset t, PriceInputs? prices) =>
        Dynamic(_dynamic!, t, prices) is { Basis: PriceBasis.Live } ? _add : _fallback;

    /// <summary>
    /// The first minute after <paramref name="t"/> whose zone differs from the zone at <paramref name="t"/>,
    /// or null for a flat price and for a zone that does not change within a year.
    /// </summary>
    public TariffChange? NextChange(DateTimeOffset t, PriceInputs? prices = null)
    {
        if (_dynamic is not null)
        {
            return Schedule(t, prices)?.NextChange(t);
        }

        if (_spec is not ZonedPrice zoned)
        {
            return null;
        }

        var x = FloorToMinute(t);
        var current = Resolve(zoned, x)?.Id;
        var horizon = x.AddDays(HorizonDays);
        while (x < horizon)
        {
            x = NextCheckpoint(x);
            var zone = Resolve(zoned, x);
            if (zone is not null && zone.Id != current)
            {
                return new TariffChange(x, zone.Id);
            }
        }

        return null;
    }

    private TariffZone? Resolve(ZonedPrice zoned, DateTimeOffset t)
    {
        var local = TimeZoneInfo.ConvertTime(t, _zone);
        return zoned.Resolve(DateOnly.FromDateTime(local.DateTime), (local.Hour * 60) + local.Minute, _holidays);
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
