using System.Collections.Immutable;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Units;

namespace Switchboard.Domain.Planning;

/// <summary>
/// One hour-of-week bin: an exponentially weighted mean of the house load in kW (energy per observed hour), the weight it has
/// accumulated after decay, the number of samples ever counted and the hour the latest sample was for.
/// </summary>
public readonly record struct LoadBin(double MeanKw, double Weight, int Samples, DateTimeOffset? LastAt)
{
    public static LoadBin Empty => new(0, 0, 0, null);

    public bool Filled => Samples > 0;
}

/// <summary>What the statistics hold for one bucket of the uncontrolled load: where it starts, its length, how long it was observed, and its energy.</summary>
public readonly record struct LoadObservation(DateTimeOffset Start, TimeSpan Length, TimeSpan Observed, KilowattHours Uncontrolled);

/// <summary>
/// The uncontrolled house load by hour of week (15-PLANNER, "Load forecast from statistics"): 168 bins in local time, Monday 00:00
/// first; a local holiday (<see cref="HolidayCalendar"/>) counts as Sunday. The repeated 02:00 hour of a 25-hour day updates its bin twice, the
/// lost hour of a 23-hour day none. An hour is counted only when it was observed for at least
/// <see cref="ForecastParameters.LoadMinObservedFraction"/> of it; its sample is its energy per observed hour, so a mostly observed hour is not
/// biased low (unknown time is never counted as zero load). Immutable; every update returns a new profile.
/// </summary>
public sealed class LoadProfile
{
    public const int BinCount = 7 * 24;

    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    public LoadProfile(ImmutableArray<LoadBin> bins, DateTimeOffset? fedThrough)
    {
        if (bins.IsDefault || bins.Length != BinCount)
        {
            throw new ArgumentException($"a load profile has {BinCount} bins", nameof(bins));
        }

        Bins = bins;
        FedThrough = fedThrough;
    }

    public static LoadProfile Empty { get; } = new([.. Enumerable.Repeat(LoadBin.Empty, BinCount)], null);

    public ImmutableArray<LoadBin> Bins { get; }

    /// <summary>The end of the last completed local hour already fed: nothing before it is counted twice. Null before the first feed.</summary>
    public DateTimeOffset? FedThrough { get; }

    public int FilledBins => Bins.Count(b => b.Filled);

    /// <summary>The bin of the local hour containing <paramref name="instant"/>: a holiday uses the Sunday row.</summary>
    public static int BinIndex(DateTimeOffset instant, TimeZoneInfo zone, HolidayCalendar holidays)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(holidays);
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        var day = holidays.IsHoliday(DateOnly.FromDateTime(local.DateTime)) ? 6 : ((int)local.DayOfWeek + 6) % 7;
        return (day * 24) + local.Hour;
    }

    /// <summary>
    /// Counts one completed local hour. Ignored (the same profile) when it was observed for less than the minimum fraction, not at all, or its energy
    /// is not a number; a negative energy (noise in the balance identity) counts as zero load.
    /// </summary>
    public LoadProfile Observe(DateTimeOffset hourStart, TimeSpan hourLength, TimeSpan observed, KilowattHours energy, ForecastParameters parameters, TimeZoneInfo zone, HolidayCalendar holidays)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (hourLength <= TimeSpan.Zero || observed <= TimeSpan.Zero || !double.IsFinite(energy.Value))
        {
            return this;
        }

        var seen = observed > hourLength ? hourLength : observed;
        if (seen.TotalSeconds / hourLength.TotalSeconds < parameters.LoadMinObservedFraction)
        {
            return this;
        }

        var kw = Math.Max(0, energy.Value) / seen.TotalHours;
        var index = BinIndex(hourStart, zone, holidays);
        var bin = Bins[index];
        var decay = bin.LastAt is { } last ? Math.Pow(0.5, Math.Max(0, (hourStart - last).TotalDays) / parameters.LoadHalfLife.TotalDays) : 1;
        var weight = (bin.Weight * decay) + 1;
        var updated = new LoadBin(
            ((bin.MeanKw * bin.Weight * decay) + kw) / weight,
            weight,
            bin.Samples == int.MaxValue ? int.MaxValue : bin.Samples + 1,
            bin.LastAt is { } later && later > hourStart ? later : hourStart);
        return new LoadProfile(Bins.SetItem(index, updated), FedThrough);
    }

    /// <summary>
    /// Feeds every completed local hour that the buckets cover and that was not fed before: seeding (<see cref="FedThrough"/> null, the buckets
    /// the statistics retain) and the hourly feed are the same call. A bucket belongs to the local hour it starts in. An hour is completed when it
    /// ended at or before <paramref name="dataThrough"/> (the statistics' high-water mark, which the caller caps at now). Calling it again with the
    /// same buckets changes nothing.
    /// </summary>
    public LoadProfile Feed(IEnumerable<LoadObservation> observations, DateTimeOffset dataThrough, ForecastParameters parameters, TimeZoneInfo zone, HolidayCalendar holidays)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(parameters);
        var completedBefore = SlotGrid.BoundaryAtOrBefore(dataThrough, Hour, zone);
        var hours = new SortedDictionary<DateTimeOffset, (DateTimeOffset End, TimeSpan Observed, double Kwh)>();
        foreach (var observation in observations)
        {
            var start = SlotGrid.BoundaryAtOrBefore(observation.Start, Hour, zone);
            var end = SlotGrid.NextBoundary(start, Hour, zone);
            if (end > completedBefore || (FedThrough is { } fed && start < fed) || observation.Observed <= TimeSpan.Zero)
            {
                continue;
            }

            var sum = hours.TryGetValue(start, out var known) ? known : (End: end, Observed: TimeSpan.Zero, Kwh: 0.0);
            hours[start] = (end, sum.Observed + observation.Observed, sum.Kwh + observation.Uncontrolled.Value);
        }

        var profile = this;
        foreach (var (start, hour) in hours)
        {
            profile = profile.Observe(start, hour.End - start, hour.Observed, new KilowattHours(hour.Kwh), parameters, zone, holidays);
        }

        var through = FedThrough is { } previous && previous > completedBefore ? previous : completedBefore;
        return new LoadProfile(profile.Bins, through);
    }
}
