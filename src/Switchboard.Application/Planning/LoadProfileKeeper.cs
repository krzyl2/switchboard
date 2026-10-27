using Switchboard.Application.Presentation;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Planning;

namespace Switchboard.Application.Planning;

/// <summary>
/// Keeps the planner's load profile (15-PLANNER): loads <c>load-profile.json</c> through the store, seeds it from the statistics buckets still retained
/// on the first update, then feeds every completed local hour and saves when an hour was fed. No Recorder access and no history call: the buckets
/// of <see cref="StatisticsSnapshot"/> are the only source. Called from one writer loop (never the guard's thread); a failing save throws
/// <see cref="IOException"/> to that loop and the profile in memory stays current. Has no effect on any decision.
/// </summary>
public sealed class LoadProfileKeeper(
    IPresentationStore<LoadProfile> store,
    ForecastParameters parameters,
    TimeZoneInfo zone,
    HolidayCalendar holidays)
{
    private bool _restored;

    /// <summary>The profile as last restored or fed; empty before <see cref="Restore"/>.</summary>
    public LoadProfile Current { get; private set; } = LoadProfile.Empty;

    /// <summary>Reads the file; absent or set aside gives an empty profile (which the first <see cref="Update"/> seeds).</summary>
    public void Restore()
    {
        Current = store.Load().State ?? LoadProfile.Empty;
        _restored = true;
    }

    /// <summary>Feeds the completed local hours the statistics cover and were not fed; returns true when the profile moved on.</summary>
    /// <param name="now">Caps the data: an hour counts only after it ended.</param>
    public bool Update(StatisticsSnapshot statistics, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        if (!_restored)
        {
            Restore();
        }

        if (statistics.IntegratedTo is not { } integrated)
        {
            return false;
        }

        var through = integrated < now ? integrated : now;
        var observations = statistics.Buckets.Values.Select(b => new LoadObservation(b.Start, statistics.BucketLength, b.Observed, b.Uncontrolled));
        var next = Current.Feed(observations, through, parameters, zone, holidays);
        if (next.FedThrough == Current.FedThrough && next.Bins.SequenceEqual(Current.Bins))
        {
            return false;
        }

        Current = next;
        store.Save(next);
        return true;
    }
}
