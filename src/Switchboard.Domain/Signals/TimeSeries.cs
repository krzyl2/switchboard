using System.Collections.Immutable;
using System.Globalization;

namespace Switchboard.Domain.Signals;

/// <summary>What the numbers of a series mean; decided by the configuration of the signal, never guessed from the data.</summary>
public enum SeriesValueKind
{
    /// <summary>Power in W at the point's time (a PV forecast).</summary>
    PowerW,

    /// <summary>Energy in Wh produced during the period the point starts (a PV forecast).</summary>
    EnergyWhPeriod,

    /// <summary>A price in currency per kWh, in force from the point's start to its end or the next point (a price curve).</summary>
    PricePerKwh,
}

/// <summary>One point of a series: a start, an optional end (the period the value is in force for) and a number.</summary>
public readonly record struct SeriesPoint(DateTimeOffset Start, DateTimeOffset? End, double Value);

/// <summary>
/// A series as the signal layer hands it on: points sorted by start (strictly increasing), every value finite, at most
/// <see cref="MaxPoints"/> of them. <see cref="CoversUntil"/> is the coverage, which is a different thing from freshness: a series
/// reported a minute ago that ends in six hours is a fresh, partial forecast (15-PLANNER, "Inputs and their signal roles").
/// </summary>
public sealed record TimeSeries(ImmutableArray<SeriesPoint> Points, SeriesValueKind Kind)
{
    /// <summary>Schema bound on one series: more points is a frame that is dropped, never truncated.</summary>
    public const int MaxPoints = 512;

    /// <summary>Schema bound on the attribute's size as Home Assistant wrote it (UTF-8 bytes).</summary>
    public const int MaxBytes = 64 * 1024;

    public DateTimeOffset? From => Points.IsDefaultOrEmpty ? null : Points[0].Start;

    /// <summary>
    /// The moment after which the series says nothing: the last point's end, else its start (an energy period or a price without an
    /// end is open-ended in the data, and the coverage is then conservatively the last start).
    /// </summary>
    public DateTimeOffset? CoversUntil => Points.IsDefaultOrEmpty ? null : Points[^1].End ?? Points[^1].Start;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Kind} series of {Points.Length} points");
}

/// <summary>Reads an instant that carries its UTC offset (<c>Z</c> or <c>+hh:mm</c>); a time without one is refused, not read as local time.</summary>
public static class IsoInstant
{
    public static bool TryParse(string? text, out DateTimeOffset instant)
    {
        instant = default;
        if (string.IsNullOrEmpty(text) || text.Length > 40 || !HasOffset(text))
        {
            return false;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out instant);
    }

    private static bool HasOffset(string text)
    {
        if (text.EndsWith('Z') || text.EndsWith('z'))
        {
            return true;
        }

        // ...+hh, ...+hhmm or ...+hh:mm after a time part; a date-only string has none.
        var time = text.IndexOfAny(['T', 't', ' ']);
        return time >= 0 && text.LastIndexOfAny(['+', '-']) > time;
    }
}
