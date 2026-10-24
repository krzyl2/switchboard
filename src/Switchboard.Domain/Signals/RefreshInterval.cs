namespace Switchboard.Domain.Signals;

public static class RefreshInterval
{
    /// <summary>
    /// Estimates how often a source reports from the intervals between its reports. A low quantile,
    /// not the median: Home Assistant emits nothing for a repeated value, so every repeat doubles one
    /// interval, and on a slowly changing signal those doubled intervals can be the majority. Returns
    /// null below <paramref name="minIntervals"/>, so an estimate is never a guess from two samples.
    /// </summary>
    public static TimeSpan? Estimate(IReadOnlyCollection<TimeSpan> intervals, double quantile, int minIntervals)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        if (intervals.Count == 0 || intervals.Count < minIntervals)
        {
            return null;
        }

        var sorted = intervals.Select(i => i.Ticks).Order().ToArray();
        var position = quantile * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        var ticks = sorted[lower] + ((sorted[upper] - sorted[lower]) * (position - lower));
        return TimeSpan.FromTicks((long)Math.Round(ticks));
    }
}
