namespace Switchboard.Infrastructure.Persistence;

/// <summary>
/// The decision log's keep rule (MODE-04), shared by the writer's retention and the history reader,
/// so the history shows exactly the day files the log keeps on disk.
/// </summary>
internal static class DecisionRetention
{
    /// <summary>
    /// The names of the day files kept: those within <paramref name="retention"/> days of today, and
    /// always the newest <paramref name="retention"/> files, so a long downtime or a clock jump never
    /// empties the log.
    /// </summary>
    public static HashSet<string> Kept(IEnumerable<(string Name, DateOnly Date)> days, DateOnly today, int retention)
    {
        ArgumentNullException.ThrowIfNull(days);
        var ordered = days.OrderBy(d => d.Date).ToList();
        var oldest = today.AddDays(-retention);
        return [.. ordered.Where(d => d.Date >= oldest).Select(d => d.Name).Concat(ordered.TakeLast(retention).Select(d => d.Name))];
    }
}
