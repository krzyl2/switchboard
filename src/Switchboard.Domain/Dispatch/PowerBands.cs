using System.Collections.Immutable;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Time;

namespace Switchboard.Domain.Dispatch;

/// <summary>A ceiling of a device's grid power during local hours (<c>control.ev.power_bands</c> for the EV charger).</summary>
/// <param name="Hours">The local hours, end exclusive; a start after the end wraps past midnight.</param>
/// <param name="MaxPowerW">The most the device may draw from the grid in them.</param>
public sealed record PowerBand(HoursRange Hours, double MaxPowerW);

/// <summary>One band on one local date, as instants.</summary>
public readonly record struct PowerBandWindow(DateTimeOffset From, DateTimeOffset To, double MaxW);

/// <summary>
/// Power bands as instants, the one place their local hours are resolved: the EV module hands them to its goals as caps, and dispatch keeps a device to them
/// whenever no plan entry sets its ceiling. Each edge is a local wall-clock time, so a 23- or 25-hour day keeps its bands at their hours.
/// </summary>
public static class PowerBands
{
    /// <summary>
    /// Every band on every local date from the day before <paramref name="now"/> (an evening band may still run past midnight) to the day after
    /// <paramref name="until"/>; bands that ended by <paramref name="now"/> are left out.
    /// </summary>
    public static ImmutableArray<PowerBandWindow> Windows(ImmutableArray<PowerBand> bands, DateTimeOffset now, DateTimeOffset until, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (bands.IsDefaultOrEmpty)
        {
            return [];
        }

        var first = TimeZoneInfo.ConvertTime(now, zone).Date.AddDays(-1);
        var last = TimeZoneInfo.ConvertTime(until, zone).Date.AddDays(1);
        var windows = ImmutableArray.CreateBuilder<PowerBandWindow>();
        for (var date = first; date <= last; date = date.AddDays(1))
        {
            foreach (var band in bands)
            {
                var from = LocalTime.At(date.AddMinutes(band.Hours.StartMinute), zone);
                var to = LocalTime.At((band.Hours.Wraps ? date.AddDays(1) : date).AddMinutes(band.Hours.EndMinute), zone);
                if (to > now && to > from)
                {
                    windows.Add(new PowerBandWindow(from, to, band.MaxPowerW));
                }
            }
        }

        return windows.ToImmutable();
    }

    /// <summary>The ceiling in force at <paramref name="now"/>: the lowest band that covers it; null outside every band.</summary>
    public static double? CeilingAt(ImmutableArray<PowerBand> bands, DateTimeOffset now, TimeZoneInfo zone) =>
        Windows(bands, now, now, zone).Where(w => w.From <= now && now < w.To).Select(w => (double?)w.MaxW).Min();
}
