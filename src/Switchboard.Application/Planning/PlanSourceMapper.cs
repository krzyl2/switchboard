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
/// A point or period whose end the data does not give lasts until the next start; the last such one is left out, because the signal layer itself
/// counts the series as covering only until that start (<see cref="TimeSeries.CoversUntil"/>): the plan never assumes more than the coverage says.
/// </summary>
public static class PlanSourceMapper
{
    public static PlanInputSources Map(SwitchboardConfiguration configuration, ForecastInputs? forecast, LoadProfile load, TimeZoneInfo zone)
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
            Pv(forecast),
            load);
    }

    /// <summary>The usable PV series as a forecast; <see cref="PvForecast.None"/> for anything else.</summary>
    public static PvForecast Pv(ForecastInputs? forecast)
    {
        if (forecast?.Usable(SignalRole.PvForecast)?.Series is not { } series)
        {
            return PvForecast.None;
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
