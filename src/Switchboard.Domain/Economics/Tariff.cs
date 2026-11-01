using System.Diagnostics;

namespace Switchboard.Domain.Economics;

/// <summary>The next moment a tariff's zone differs from the current one, and the zone it changes to.</summary>
public readonly record struct TariffChange(DateTimeOffset At, string Zone);

/// <summary>
/// A price spec evaluated on the local clock of a time zone: the instant is converted with the zone's rules,
/// so a 23-hour day has no 02:xx and a 25-hour day has 02:xx twice, and a rule 02:00-03:00 matches both.
/// Pure: no clock inside (the caller passes the instant), no entity, no vendor. A dynamic spec reads its price signal from the
/// <see cref="PriceInputs"/> the caller passes: live (signal plus add) while that value is usable, else the static fallback, never
/// the last known value; without inputs a dynamic spec is at its fallback. One subtype per kind of spec (flat, zoned, dynamic),
/// made by <see cref="For"/>.
/// </summary>
public abstract class Tariff
{
    private protected Tariff()
    {
    }

    /// <summary>The tariff of a spec: flat, zoned or dynamic (whose fallback and add are tariffs of their own).</summary>
    public static Tariff For(PriceSpec spec, HolidayCalendar holidays, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(holidays);
        ArgumentNullException.ThrowIfNull(zone);
        return spec switch
        {
            FlatPrice flat => new FlatTariff(flat),
            ZonedPrice zoned => new ZonedTariff(zoned, holidays, zone),
            DynamicPrice dynamic => new DynamicTariff(dynamic, For(dynamic.Fallback, holidays, zone), dynamic.Add is { } add ? For(add, holidays, zone) : null),
            _ => throw new UnreachableException($"A price spec of a kind the tariff does not know: {spec.GetType().Name}. Add its subtype to Tariff.For."),
        };
    }

    public abstract bool IsZoned { get; }

    /// <summary>The zone in force at <paramref name="t"/>; null for a flat price (and for a moment no zone claims, which a validated schedule has none of).</summary>
    /// <param name="prices">For a dynamic spec: the zone is the add's while the price is live, the fallback's while it is not.</param>
    public abstract TariffZone? ZoneAt(DateTimeOffset t, PriceInputs? prices = null);

    /// <summary>The import (or export) price at <paramref name="t"/>; null only for a moment no zone claims.</summary>
    /// <param name="prices">The price signals; only a dynamic spec reads them.</param>
    public abstract PricePoint? PriceAt(DateTimeOffset t, PriceInputs? prices = null);

    /// <summary>
    /// The first minute after <paramref name="t"/> whose zone differs from the zone at <paramref name="t"/>,
    /// or null for a flat price and for a zone that does not change within a year.
    /// </summary>
    public abstract TariffChange? NextChange(DateTimeOffset t, PriceInputs? prices = null);
}
