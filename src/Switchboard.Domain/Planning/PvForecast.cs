using System.Collections.Immutable;

namespace Switchboard.Domain.Planning;

/// <summary>A point of a PV power forecast: power in W at an instant.</summary>
public readonly record struct PvPowerPoint(DateTimeOffset At, double Watts);

/// <summary>A PV energy forecast for a period: Wh over [<see cref="Start"/>, <see cref="End"/>).</summary>
public readonly record struct PvPeriod(DateTimeOffset Start, DateTimeOffset End, double Wh);

/// <summary>A day's PV total in kWh and the sun times it is spread between (the degraded totals shape).</summary>
public readonly record struct PvDaylight(DateTimeOffset Rise, DateTimeOffset Set, double Kwh);

/// <summary>Where the slot energies came from: a series of points or periods, the daily totals, or nothing.</summary>
public enum PvBasis
{
    None,
    Series,
    Totals,
}

/// <summary>How much of the grid the forecast covers; a missing forecast is zero PV for the whole horizon, a partial one zero beyond its end.</summary>
public enum PvCoverage
{
    Missing,
    Partial,
    Full,
}

/// <summary>The PV energy of each slot of a grid, kWh, already derated.</summary>
public sealed record PvSlots(ImmutableArray<double> Kwh, PvBasis Basis, PvCoverage Coverage, DateTimeOffset? CoversUntil);

/// <summary>
/// The PV forecast as plain inputs (15-PLANNER, "PV forecast processing"): power points (trapezoid), period energies (assigned in
/// proportion to the overlap), or daily totals spread over daylight as a half-sine between the sun times. Slot energy is multiplied
/// by <see cref="ForecastParameters.PvDerate"/> and is zero beyond the coverage. Freshness (a forecast older than its max age is not
/// used) is the signal layer's: it hands over <see cref="None"/> instead. Pure: no entity, no clock, no signal-layer type.
/// </summary>
public sealed class PvForecast
{
    public static readonly PvForecast None = new([], PvBasis.None);

    private readonly ImmutableArray<Segment> _segments;

    private PvForecast(ImmutableArray<Segment> segments, PvBasis basis, DateTimeOffset? coversUntil = null)
    {
        _segments = segments;
        Basis = basis;
        CoversUntil = coversUntil;
    }

    public PvBasis Basis { get; }

    /// <summary>The last instant the forecast says anything about; null for <see cref="None"/>.</summary>
    public DateTimeOffset? CoversUntil { get; }

    /// <summary>Power points in W; power between two points is linear (the trapezoid), and zero before the first and after the last.</summary>
    public static PvForecast FromPowerPoints(IEnumerable<PvPowerPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var sorted = points.OrderBy(p => p.At).ToList();
        if (sorted.Count == 0)
        {
            return None;
        }

        var segments = ImmutableArray.CreateBuilder<Segment>();
        for (var i = 0; i < sorted.Count; i++)
        {
            Check(sorted[i].Watts, "power");
            if (i > 0)
            {
                if (sorted[i].At == sorted[i - 1].At)
                {
                    throw new ArgumentException("two power points share an instant", nameof(points));
                }

                segments.Add(new Linear(sorted[i - 1].At, sorted[i].At, sorted[i - 1].Watts, sorted[i].Watts));
            }
        }

        return new PvForecast(segments.ToImmutable(), PvBasis.Series, sorted[^1].At);
    }

    /// <summary>Energies in Wh of periods that do not overlap; each is spread evenly over its period.</summary>
    public static PvForecast FromPeriodEnergies(IEnumerable<PvPeriod> periods)
    {
        ArgumentNullException.ThrowIfNull(periods);
        var sorted = periods.OrderBy(p => p.Start).ToList();
        if (sorted.Count == 0)
        {
            return None;
        }

        var segments = ImmutableArray.CreateBuilder<Segment>();
        for (var i = 0; i < sorted.Count; i++)
        {
            var period = sorted[i];
            Check(period.Wh, "energy");
            if (period.End <= period.Start)
            {
                throw new ArgumentException("a period ends before it starts", nameof(periods));
            }

            if (i > 0 && period.Start < sorted[i - 1].End)
            {
                throw new ArgumentException("two periods overlap", nameof(periods));
            }

            var watts = period.Wh / (period.End - period.Start).TotalHours;
            segments.Add(new Linear(period.Start, period.End, watts, watts));
        }

        return new PvForecast(segments.ToImmutable(), PvBasis.Series, sorted.Max(p => p.End));
    }

    /// <summary>Energies in Wh given by their start (a series of <c>{start, value}</c>): each period lasts until the next start, the last one <paramref name="lastPeriod"/>.</summary>
    public static PvForecast FromPeriodStarts(IEnumerable<(DateTimeOffset Start, double Wh)> starts, TimeSpan lastPeriod)
    {
        ArgumentNullException.ThrowIfNull(starts);
        if (lastPeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lastPeriod), lastPeriod, "the last period is positive");
        }

        var sorted = starts.OrderBy(s => s.Start).ToList();
        var periods = sorted.Select((s, i) => new PvPeriod(s.Start, i + 1 < sorted.Count ? sorted[i + 1].Start : s.Start + lastPeriod, s.Wh));
        return FromPeriodEnergies(periods);
    }

    /// <summary>Daily totals in kWh, each spread over [rise, set] as a half-sine (the integral of the sine over the daylight is the total).</summary>
    public static PvForecast FromDailyTotals(IEnumerable<PvDaylight> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        var sorted = days.OrderBy(d => d.Rise).ToList();
        if (sorted.Count == 0)
        {
            return None;
        }

        var segments = ImmutableArray.CreateBuilder<Segment>();
        for (var i = 0; i < sorted.Count; i++)
        {
            var day = sorted[i];
            Check(day.Kwh, "energy");
            if (day.Set <= day.Rise || day.Set - day.Rise > TimeSpan.FromDays(1))
            {
                throw new ArgumentException("the sun sets before it rises, or the day is longer than 24 hours", nameof(days));
            }

            if (i > 0 && day.Rise < sorted[i - 1].Set)
            {
                throw new ArgumentException("two days overlap", nameof(days));
            }

            segments.Add(new HalfSine(day.Rise, day.Set, day.Kwh));
        }

        return new PvForecast(segments.ToImmutable(), PvBasis.Totals, sorted.Max(d => d.Set));
    }

    /// <summary>The derated energy of every slot of <paramref name="grid"/>, zero where the forecast says nothing.</summary>
    public PvSlots Slots(SlotGrid grid, ForecastParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(parameters);
        var kwh = grid.Slots
            .Select(s => _segments.Sum(seg => seg.Kwh(s.Start, s.End)) * parameters.PvDerate)
            .ToImmutableArray();
        var coverage = CoversUntil is not { } until || until <= grid.From
            ? PvCoverage.Missing
            : until < grid.To ? PvCoverage.Partial : PvCoverage.Full;
        return new PvSlots(kwh, Basis, coverage, CoversUntil);
    }

    private static void Check(double value, string what)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentException($"a PV {what} is finite and not negative");
        }
    }

    private abstract record Segment(DateTimeOffset Start, DateTimeOffset End)
    {
        /// <summary>Energy in kWh over the part of [a, b) this segment covers.</summary>
        public double Kwh(DateTimeOffset a, DateTimeOffset b)
        {
            var from = a > Start ? a : Start;
            var to = b < End ? b : End;
            return to > from ? Integral(from, to) : 0;
        }

        protected abstract double Integral(DateTimeOffset from, DateTimeOffset to);
    }

    /// <summary>Linear power between two instants, W; a constant is p0 = p1.</summary>
    private sealed record Linear(DateTimeOffset Start, DateTimeOffset End, double P0, double P1) : Segment(Start, End)
    {
        protected override double Integral(DateTimeOffset from, DateTimeOffset to)
        {
            var span = (End - Start).Ticks;
            var pa = P0 + ((P1 - P0) * (from - Start).Ticks / span);
            var pb = P0 + ((P1 - P0) * (to - Start).Ticks / span);
            return (pa + pb) / 2 * (to - from).TotalHours / 1000;
        }
    }

    /// <summary>A day's total spread as sin(pi x) over x in [0, 1] between rise and set.</summary>
    private sealed record HalfSine(DateTimeOffset Start, DateTimeOffset End, double Total) : Segment(Start, End)
    {
        protected override double Integral(DateTimeOffset from, DateTimeOffset to)
        {
            var span = (double)(End - Start).Ticks;
            var fa = (from - Start).Ticks / span;
            var fb = (to - Start).Ticks / span;
            return Total * (Math.Cos(Math.PI * fa) - Math.Cos(Math.PI * fb)) / 2;
        }
    }
}
