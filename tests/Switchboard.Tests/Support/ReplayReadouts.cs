using System.Collections.Immutable;
using Switchboard.Application.Setup.Readouts;
using Switchboard.Application.Signals;
using Switchboard.Domain.Signals;
using Switchboard.Infrastructure.Replay;

namespace Switchboard.Tests.Support;

/// <summary>
/// Candidate readouts over a recording on a virtual clock: what the discovery link's subscription
/// would have delivered by now (every row at or before the clock), with the refresh detected from
/// those reports, and a count of the holds a self-test session takes and releases.
/// </summary>
internal sealed class ReplayReadouts(ReplayData data, TimeProvider time, IReadOnlyDictionary<string, string> units) : ICandidateReadouts
{
    private readonly ImmutableDictionary<EntityId, ImmutableArray<ReplayRow>> _rows =
        data.Rows.GroupBy(r => r.Entity).ToImmutableDictionary(g => g.Key, g => g.ToImmutableArray());

    public int Holds { get; private set; }

    public int Leases { get; private set; }

    public ImmutableArray<CandidateReadout> Lease(IReadOnlyCollection<EntityId> entities)
    {
        Leases++;
        return [.. entities.Select(e => Peek(e)!)];
    }

    public IDisposable Hold(IReadOnlyCollection<EntityId> entities)
    {
        Holds++;
        return new Handle(this);
    }

    public CandidateReadout? Peek(EntityId entity)
    {
        var rows = Before(entity);
        var refresh = SetupFixtures.Control.Signals.Refresh;
        if (rows.Count == 0)
        {
            return new CandidateReadout(entity, ReadoutStatus.Pending, null, null, null, null, null, 0, refresh.MinIntervals);
        }

        var recent = rows.TakeLast(refresh.WindowIntervals + 1).ToList();
        var intervals = recent.Zip(recent.Skip(1), (a, b) => b.Timestamp - a.Timestamp).ToList();
        var now = time.GetUtcNow();
        return new CandidateReadout(
            entity,
            ReadoutStatus.Found,
            rows[^1].State,
            units.GetValueOrDefault(entity.Value),
            rows[^1].Timestamp,
            now - rows[^1].Timestamp,
            RefreshInterval.Estimate(intervals, refresh.Quantile, refresh.MinIntervals),
            intervals.Count,
            refresh.MinIntervals);
    }

    public ImmutableArray<EntityUpdate> History(EntityId entity) =>
        [.. Before(entity).TakeLast(64).Select(r => new EntityUpdate(entity, r.State, units.GetValueOrDefault(entity.Value), r.Timestamp, r.Timestamp))];

    private List<ReplayRow> Before(EntityId entity) =>
        _rows.TryGetValue(entity, out var rows) ? [.. rows.Where(r => r.Timestamp <= time.GetUtcNow())] : [];

    private sealed class Handle(ReplayReadouts owner) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (!_released)
            {
                _released = true;
                owner.Holds--;
            }
        }
    }
}
