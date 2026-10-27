using System.Collections.Immutable;

namespace Switchboard.Domain.Goals;

/// <summary>What the merge kept and what it set aside.</summary>
/// <param name="Applied">One request per device and deadline, ordered per device by deadline.</param>
/// <param name="Superseded">The losing requests: shown, not applied.</param>
public sealed record GoalMergeResult(ImmutableArray<GoalRequest> Applied, ImmutableArray<GoalRequest> Superseded);

/// <summary>
/// Merges the requests of every source (15-PLANNER, Goal sources): for one device and one deadline the higher
/// <see cref="GoalOrigin"/> wins (manual over module over entity over config); several goals of a device, two trips in one day,
/// stay as they are, ordered by deadline. Pure and deterministic: ties fall to the harder goal, then the lower revision.
/// </summary>
public static class GoalMerge
{
    public static GoalMergeResult Merge(IEnumerable<GoalRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var applied = new List<GoalRequest>();
        var superseded = new List<GoalRequest>();
        foreach (var group in requests.GroupBy(r => (r.Device, r.Deadline)))
        {
            var ranked = group
                .OrderByDescending(r => r.Origin)
                .ThenByDescending(r => r.Hard)
                .ThenBy(r => r.Revision)
                .ToList();
            applied.Add(ranked[0]);
            superseded.AddRange(ranked.Skip(1));
        }

        return new GoalMergeResult([.. Order(applied)], [.. Order(superseded)]);
    }

    /// <summary>Per device by name, then by deadline (none last), then hard before soft, then the stronger origin.</summary>
    public static IEnumerable<GoalRequest> Order(IEnumerable<GoalRequest> requests) =>
        requests
            .OrderBy(r => r.Device)
            .ThenBy(r => r.Deadline is null)
            .ThenBy(r => r.Deadline)
            .ThenByDescending(r => r.Hard)
            .ThenByDescending(r => r.Origin);
}
