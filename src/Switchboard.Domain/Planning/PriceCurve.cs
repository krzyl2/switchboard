using System.Collections.Immutable;
using Switchboard.Domain.Economics;

namespace Switchboard.Domain.Planning;

/// <summary>A forward price in currency per kWh for [<see cref="Start"/>, <see cref="End"/>) (the signal layer divided per-MWh prices already).</summary>
public readonly record struct PriceCurveInterval(DateTimeOffset Start, DateTimeOffset End, double PerKwh);

/// <summary>
/// A forward price curve as plain intervals (Nord Pool, Tibber, ENTSO-E style), never an entity. Intervals must not overlap; gaps are allowed and are
/// priced from the tariff. A market may publish a negative price, so a negative value is kept; a non-finite one is refused.
/// </summary>
public sealed class PriceCurve
{
    /// <summary>A configured curve with no usable data: every slot is priced from the tariff and the plan says the curve is partial.</summary>
    public static readonly PriceCurve Unusable = new([]);

    private PriceCurve(ImmutableArray<PriceCurveInterval> intervals)
    {
        Intervals = intervals;
    }

    public ImmutableArray<PriceCurveInterval> Intervals { get; }

    public DateTimeOffset? CoversUntil => Intervals.IsEmpty ? null : Intervals[^1].End;

    public static PriceCurve From(IEnumerable<PriceCurveInterval> intervals)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        var sorted = intervals.OrderBy(i => i.Start).ToImmutableArray();
        for (var i = 0; i < sorted.Length; i++)
        {
            if (sorted[i].End <= sorted[i].Start || !double.IsFinite(sorted[i].PerKwh))
            {
                throw new ArgumentException("a price interval has an end after its start and a finite price", nameof(intervals));
            }

            if (i > 0 && sorted[i].Start < sorted[i - 1].End)
            {
                throw new ArgumentException("two price intervals overlap", nameof(intervals));
            }
        }

        return new PriceCurve(sorted);
    }

    /// <summary>Prices given by their start: each lasts until the next start, the last one <paramref name="lastInterval"/>.</summary>
    public static PriceCurve FromStarts(IEnumerable<(DateTimeOffset Start, double PerKwh)> starts, TimeSpan lastInterval)
    {
        ArgumentNullException.ThrowIfNull(starts);
        if (lastInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lastInterval), lastInterval, "the last interval is positive");
        }

        var sorted = starts.OrderBy(s => s.Start).ToList();
        return From(sorted.Select((s, i) => new PriceCurveInterval(s.Start, i + 1 < sorted.Count ? sorted[i + 1].Start : s.Start + lastInterval, s.PerKwh)));
    }

    /// <summary>The interval containing <paramref name="t"/>, or null.</summary>
    internal PriceCurveInterval? At(DateTimeOffset t)
    {
        foreach (var interval in Intervals)
        {
            if (t >= interval.Start && t < interval.End)
            {
                return interval;
            }
        }

        return null;
    }

    /// <summary>The start of the first interval after <paramref name="t"/>, or null.</summary>
    internal DateTimeOffset? NextStart(DateTimeOffset t) => Intervals.Where(i => i.Start > t).Select(i => (DateTimeOffset?)i.Start).FirstOrDefault();
}

/// <summary>Where a slot's price came from: the tariff (schedule or fallback), the forward curve, or both inside one slot.</summary>
public enum SlotPriceBasis
{
    Tariff,
    Curve,
    Mixed,
}

/// <summary>A slot's price in currency per kWh: the time-weighted mean of the prices in force during it, and the zone when one zone holds the whole slot.</summary>
public readonly record struct SlotPrice(double PerKwh, SlotPriceBasis Basis, string? Zone);

/// <summary>Prices per slot (15-PLANNER, "The slot grid": the settlement rule's time weighting, zones and hourly markets change on the :00).</summary>
public static class SlotPrices
{
    /// <summary>
    /// The mean price of <paramref name="slot"/>: the curve where it has an interval, the tariff (<see cref="PeriodPrice.Mean"/>) where it has none.
    /// Null when a part of the slot has no price at all (no tariff, a moment no zone claims). <paramref name="curveGap"/> says part of the slot was priced
    /// from the tariff although a curve was supplied.
    /// </summary>
    public static SlotPrice? Mean(Slot slot, Tariff? tariff, PriceCurve? curve, out bool curveGap)
    {
        curveGap = false;
        var weighted = 0.0;
        var fromCurve = false;
        var fromTariff = false;
        var cursor = slot.Start;
        while (cursor < slot.End)
        {
            DateTimeOffset end;
            if (curve?.At(cursor) is { } interval)
            {
                end = interval.End < slot.End ? interval.End : slot.End;
                weighted += interval.PerKwh * (end - cursor).TotalHours;
                fromCurve = true;
            }
            else
            {
                end = curve?.NextStart(cursor) is { } next && next < slot.End ? next : slot.End;
                if (tariff is null || PeriodPrice.Mean(tariff, cursor, end) is not { } price)
                {
                    return null;
                }

                weighted += price.PerKwh * (end - cursor).TotalHours;
                fromTariff = true;
                curveGap |= curve is not null;
            }

            cursor = end;
        }

        if (slot.End <= slot.Start)
        {
            return null;
        }

        var basis = fromCurve && fromTariff ? SlotPriceBasis.Mixed : fromCurve ? SlotPriceBasis.Curve : SlotPriceBasis.Tariff;
        return new SlotPrice(weighted / slot.Hours, basis, ZoneOf(slot, tariff));
    }

    private static string? ZoneOf(Slot slot, Tariff? tariff)
    {
        if (tariff is null || (tariff.NextChange(slot.Start) is { } change && change.At < slot.End))
        {
            return null;
        }

        return tariff.ZoneAt(slot.Start)?.Id;
    }
}
