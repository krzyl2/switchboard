using System.Collections.Immutable;
using Switchboard.Domain.Signals;

namespace Switchboard.Domain.Economics;

/// <summary>How a price at an instant came about (the log basis of 14-ECONOMICS).</summary>
public enum PriceBasis
{
    Flat,
    Zone,

    /// <summary>The price is the value of a mapped, fresh entity plus the spec's <c>add</c> at that instant (dynamic price).</summary>
    Live,

    /// <summary>The entity is not usable (stale, unavailable, unknown, wrong unit) and the spec's static <c>fallback</c> stands in; never the last known value.</summary>
    Fallback,
}

/// <summary>A price in currency per kWh at an instant, with its basis and, for a zoned price, the zone it is from.</summary>
public readonly record struct PricePoint(double PerKwh, PriceBasis Basis, string? Zone);

/// <summary>
/// One price spec: a number (flat) or a time-of-use schedule (zones). The same shape serves the import
/// price and the export price; <see cref="DynamicPrice"/> is the one that follows an entity.
/// </summary>
public abstract record PriceSpec;

/// <param name="PerKwh">Currency per kWh, finite and not negative.</param>
public sealed record FlatPrice(double PerKwh) : PriceSpec;

/// <summary>A time-of-use schedule: zones are tried in listed order, the first match wins, the default zone takes the rest.</summary>
/// <param name="DefaultZone">The id of the zone that takes every moment no zone claims; null only when the zones cover all time.</param>
public sealed record ZonedPrice(ImmutableArray<TariffZone> Zones, string? DefaultZone) : PriceSpec
{
    /// <summary>The zone of a local moment: the first listed zone that matches, else the default zone, else null (a gap).</summary>
    public TariffZone? Resolve(DateOnly date, int minuteOfDay, HolidayCalendar holidays)
    {
        foreach (var zone in Zones)
        {
            if (zone.Matches(date, minuteOfDay, holidays))
            {
                return zone;
            }
        }

        return DefaultZone is null ? null : Zones.FirstOrDefault(z => z.Id == DefaultZone);
    }

    /// <summary>
    /// The first moment (in some month, on some weekday, holiday or not) that no zone claims, or null when
    /// the zones cover all time. Evaluated on the rules alone, not on the dates of a holiday list.
    /// </summary>
    public ZoneGap? FindGap()
    {
        for (var month = 1; month <= 12; month++)
        {
            for (var day = 1; day <= 7; day++)
            {
                var date = new DateOnly(2026, month, day);
                foreach (var holiday in new[] { false, true })
                {
                    var calendar = holiday ? new HolidayCalendar([date]) : HolidayCalendar.Empty;
                    for (var minute = 0; minute < HoursRange.MinutesPerDay; minute++)
                    {
                        if (!Zones.Any(z => z.Matches(date, minute, calendar)))
                        {
                            return new ZoneGap(month, date.DayOfWeek, holiday, minute);
                        }
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Every wall-clock minute (0 to 1439) at which some zone's rules can change, midnight included, ascending.</summary>
    internal int[] Boundaries() =>
        [.. Zones.SelectMany(z => z.Rules).SelectMany(r => r.Boundaries()).Append(0).Distinct().Order()];
}

/// <summary>
/// A price that follows a mapped entity (14-ECONOMICS, ECO-10): the signal's current value plus <c>add(t)</c> while the signal is
/// fresh (basis live), else the static <paramref name="Fallback"/> (basis fallback), never the last known value. The signal is named
/// by its logical signal name; the entity behind it is the mapping layer's secret.
/// </summary>
/// <param name="Signal">The price signal in currency per kWh (the signal layer already converted a per-MWh unit).</param>
/// <param name="Fallback">Required, static (a number or zones): what the price is while the signal is not usable.</param>
/// <param name="Add">Optional, static, added to the signal's value while it is live (usually the distribution zones); null adds nothing.</param>
/// <param name="Curve">Optional: a series signal with the forward prices (15-PLANNER, PLAN-07), read by the planner only. Dispatch never reads it, so it changes no price now.</param>
public sealed record DynamicPrice(SignalName Signal, PriceSpec Fallback, PriceSpec? Add, SignalName? Curve = null) : PriceSpec;

/// <summary>The zone schedules a price spec holds, wherever they are (the spec, or the fallback and the add of a dynamic one).</summary>
public static class PriceSpecs
{
    public static IEnumerable<ZonedPrice> Schedules(PriceSpec? spec) => spec switch
    {
        ZonedPrice zoned => [zoned],
        DynamicPrice dynamic => Schedules(dynamic.Add).Concat(Schedules(dynamic.Fallback)),
        _ => [],
    };

    /// <summary>Every zone id of the spec's schedules.</summary>
    public static IEnumerable<string> ZoneIds(PriceSpec? spec) => Schedules(spec).SelectMany(s => s.Zones).Select(z => z.Id).Distinct(StringComparer.Ordinal);

    /// <summary>The signal names a spec reads (empty for a static one).</summary>
    public static IEnumerable<SignalName> Signals(PriceSpec? spec) => spec is DynamicPrice dynamic ? [dynamic.Signal] : [];

    /// <summary>The forward-curve signal names a spec names (empty for a static one and for a dynamic one without a curve).</summary>
    public static IEnumerable<SignalName> Curves(PriceSpec? spec) => spec is DynamicPrice { Curve: { } curve } ? [curve] : [];
}

/// <summary>A moment no zone claims: a month (1 to 12), a weekday, whether the day was a holiday, and the minute of the day.</summary>
public readonly record struct ZoneGap(int Month, DayOfWeek Day, bool Holiday, int MinuteOfDay);
