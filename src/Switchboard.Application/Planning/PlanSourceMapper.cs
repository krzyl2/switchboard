using Switchboard.Application.Configuration;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Forecast;
using Switchboard.Domain.Planning;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Planning;

/// <summary>
/// Maps the signal layer's forecast and price-curve samples (<see cref="ForecastInputs"/>) and the configuration's tariffs to the planner's plain
/// <see cref="PlanInputSources"/> (15-PLANNER, "Inputs and their signal roles"). A sample that is not usable (stale, unavailable, refused, absent) gives
/// <see cref="PvForecast.None"/> or <see cref="PriceCurve.Unusable"/>: the last known curve is never used, and a configured curve is never silently dropped.
/// Without a usable series, the day totals and the sun times give the degraded shape (<see cref="PvForecast.FromDailyTotals"/>): a total spread over
/// the daylight of its day, which the sun times only place (<see cref="Daylight"/>); a total, or the sun, that is not usable gives no PV for that day.
/// A point or period whose end the data does not give lasts until the next start; the last such one is left out, because the signal layer itself
/// counts the series as covering only until that start (<see cref="TimeSeries.CoversUntil"/>): the plan never assumes more than the coverage says.
/// </summary>
public static class PlanSourceMapper
{
    /// <param name="now">The instant the plan is made for; the local day of the daily totals is its day in <paramref name="zone"/>.</param>
    public static PlanInputSources Map(SwitchboardConfiguration configuration, ForecastInputs? forecast, LoadProfile load, TimeZoneInfo zone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(zone);
        var economics = configuration.Economics;
        var holidays = economics?.Holidays ?? HolidayCalendar.Empty;
        var import = economics?.Tariff;
        var export = economics?.Settlement?.ExportPrice;
        return new PlanInputSources(
            zone,
            holidays,
            import is null ? null : new Tariff(import, holidays, zone),
            export is null ? null : new Tariff(export, holidays, zone),
            Curve(import, forecast),
            Curve(export, forecast),
            Pv(forecast, zone, now),
            load);
    }

    /// <summary>
    /// The usable PV series as a forecast; else the usable day totals spread over daylight; <see cref="PvForecast.None"/> for anything else. A usable series
    /// always wins: the totals are used only when no usable series exists, never beside it.
    /// </summary>
    public static PvForecast Pv(ForecastInputs? forecast, TimeZoneInfo zone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (forecast?.Usable(SignalRole.PvForecast)?.Series is not { } series)
        {
            return DailyTotals(forecast, zone, now);
        }

        try
        {
            return series.Kind switch
            {
                SeriesValueKind.PowerW => PvForecast.FromPowerPoints(series.Points.Select(p => new PvPowerPoint(p.Start, p.Value))),
                SeriesValueKind.EnergyWhPeriod => PvForecast.FromPeriodEnergies(Periods(series).Select(p => new PvPeriod(p.Start, p.End, p.Value))),
                _ => PvForecast.None,
            };
        }
        catch (ArgumentException)
        {
            // A negative or non-finite value, or points the forecast refuses: unusable, never repaired.
            return PvForecast.None;
        }
    }

    /// <summary>Today's and tomorrow's totals (kWh, the whole day) over their daylight; a day whose total or sun times are not usable is left out.</summary>
    private static PvForecast DailyTotals(ForecastInputs? forecast, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (forecast?.Usable(SignalRole.SunRising)?.At is not { } rising || forecast.Usable(SignalRole.SunSetting)?.At is not { } setting)
        {
            return PvForecast.None;
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var days = new List<PvDaylight>();
        foreach (var (role, day) in new[] { (SignalRole.PvForecastToday, today), (SignalRole.PvForecastTomorrow, today.AddDays(1)) })
        {
            if (forecast.Usable(role)?.EnergyKwh is { } kwh && Daylight(rising, setting, day, zone) is { } window)
            {
                days.Add(new PvDaylight(window.Rise, window.Set, kwh));
            }
        }

        try
        {
            return PvForecast.FromDailyTotals(days);
        }
        catch (ArgumentException)
        {
            // A negative or non-finite total, a day the sun times do not make sense of, or days that overlap: unusable, never repaired.
            return PvForecast.None;
        }
    }

    /// <summary>
    /// The daylight of the local <paramref name="day"/> from the next sunrise and the next sunset (a sun entity gives the next events, so after sunset both are
    /// tomorrow's). The events are moved by whole 24-hour spans of absolute time, not by local clock time: the sun does not follow a DST change, so a day of
    /// 23 or 25 hours keeps its sun times in UTC, and the shift of a few minutes a day over the few days involved is far below a slot. The rise is the one
    /// that falls on <paramref name="day"/> in <paramref name="zone"/>, the set is the first one after it; null when there is none within
    /// <see cref="MaxSunShiftDays"/>, or the day would be longer than 24 hours (polar day): then the day has no PV forecast.
    /// </summary>
    private static (DateTimeOffset Rise, DateTimeOffset Set)? Daylight(DateTimeOffset rising, DateTimeOffset setting, DateOnly day, TimeZoneInfo zone)
    {
        var shifts = Enumerable.Range(-MaxSunShiftDays, (2 * MaxSunShiftDays) + 1).OrderBy(Math.Abs).ToList();
        DateTimeOffset? rise = shifts.Select(n => rising.AddDays(n)).Cast<DateTimeOffset?>()
            .FirstOrDefault(r => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(r!.Value, zone).DateTime) == day);
        if (rise is not { } start)
        {
            return null;
        }

        var set = shifts.Select(n => setting.AddDays(n)).Where(s => s > start && s - start < TimeSpan.FromDays(1)).Cast<DateTimeOffset?>().Min();
        return set is { } end ? (start, end) : null;
    }

    /// <summary>How many 24-hour spans the next sun events are moved to reach a given day: a sun entity reports within a day or two, and its max_age bounds how stale it can be.</summary>
    private const int MaxSunShiftDays = 3;

    private static PriceCurve? Curve(PriceSpec? spec, ForecastInputs? forecast)
    {
        var names = PriceSpecs.Curves(spec).ToList();
        if (names.Count == 0)
        {
            return null;
        }

        if (forecast?.Sample(names[0]) is not { Usable: true, Series: { Kind: SeriesValueKind.PricePerKwh } series })
        {
            return PriceCurve.Unusable;
        }

        try
        {
            var curve = PriceCurve.From(Periods(series).Select(p => new PriceCurveInterval(p.Start, p.End, p.Value)));
            return curve.Intervals.IsEmpty ? PriceCurve.Unusable : curve;
        }
        catch (ArgumentException)
        {
            return PriceCurve.Unusable;
        }
    }

    private static IEnumerable<(DateTimeOffset Start, DateTimeOffset End, double Value)> Periods(TimeSeries series)
    {
        var points = series.Points;
        for (var i = 0; i < points.Length; i++)
        {
            var next = i + 1 < points.Length ? points[i + 1].Start : (DateTimeOffset?)null;
            var end = points[i].End ?? next;
            if (end is not { } e)
            {
                yield break;
            }

            if (next is { } n && e > n)
            {
                e = n;
            }

            if (e > points[i].Start)
            {
                yield return (points[i].Start, e, points[i].Value);
            }
        }
    }
}
