using Switchboard.Application.Configuration;
using Switchboard.Application.Presentation;
using Switchboard.Application.Signals;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Planning;

namespace Switchboard.Application.Planning;

/// <summary>
/// Owns the <see cref="LoadProfileKeeper"/> of the running configuration (15-PLANNER). Without a <c>planner:</c> section it does nothing at all: it never
/// reads or writes <c>load-profile.json</c>, so a 0.10 file leaves no new file behind and decides and persists exactly as before. With one, the keeper is
/// built from that configuration's forecast parameters, the clock's zone and the holidays of <c>economics:</c> (none without it), restored from the file
/// on first use, and rebuilt (and restored again, the file being the profile) when a reload changes any of them. Safe to call from the statistics
/// writer loop and from a request thread; a failing save throws <see cref="IOException"/> to the caller that fed it.
/// </summary>
public sealed class LoadProfileFeed(IPresentationStore<LoadProfile> store, Func<SwitchboardConfiguration?> configuration, SignalClock clock)
{
    private readonly object _gate = new();
    private LoadProfileKeeper? _keeper;
    private (ForecastParameters Parameters, string Zone, HolidayCalendar Holidays)? _built;

    /// <summary>The profile as last restored or fed; empty while there is no planner section.</summary>
    public LoadProfile Current
    {
        get
        {
            lock (_gate)
            {
                return KeeperFor(configuration())?.Current ?? LoadProfile.Empty;
            }
        }
    }

    /// <summary>Feeds the completed hours of <paramref name="statistics"/>; false (and no file access) without a planner section.</summary>
    public bool Update(StatisticsSnapshot statistics, DateTimeOffset now)
    {
        lock (_gate)
        {
            return KeeperFor(configuration())?.Update(statistics, now) ?? false;
        }
    }

    private LoadProfileKeeper? KeeperFor(SwitchboardConfiguration? current)
    {
        if (current?.Planner is null)
        {
            return null;
        }

        var zone = clock.Time.LocalTimeZone;
        var parameters = current.Control.Planner.Forecast();
        var holidays = current.Economics?.Holidays ?? HolidayCalendar.Empty;
        if (_keeper is null || _built is not { } built || built.Parameters != parameters || built.Zone != zone.Id || !SameDates(built.Holidays, holidays))
        {
            _keeper = new LoadProfileKeeper(store, parameters, zone, holidays);
            _keeper.Restore();
            _built = (parameters, zone.Id, holidays);
        }

        return _keeper;
    }

    // A reload builds a new calendar from the same list; only a changed list changes the bins a holiday falls in.
    private static bool SameDates(HolidayCalendar a, HolidayCalendar b) => a.Dates.Count == b.Dates.Count && a.Dates.All(b.IsHoliday);
}
