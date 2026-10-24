using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Signals;
using Switchboard.Domain.Signals;

namespace Switchboard.Infrastructure.Replay;

/// <summary>
/// Development replay: stands in for Home Assistant and plays recorded history on the signal clock.
/// Tracking an entity delivers its latest row at or before now; later rows are delivered as their
/// time comes. A probe answers last_reported = min(now, the entity's next row, end of data): a later
/// row proves the entity was alive and unchanged in between, so a legitimately constant signal stays
/// fresh, and after the data ends every signal goes stale. The recording has no units, so updates
/// carry the declared unit.
/// </summary>
public sealed class ReplaySignalSource : ISignalSource, IDisposable
{
    private readonly ReplayData _data;
    private readonly SignalInbox _inbox;
    private readonly TimeProvider _time;
    private readonly ILogger<ReplaySignalSource> _logger;
    private readonly ImmutableDictionary<EntityId, ImmutableArray<int>> _rowsByEntity;
    private readonly object _gate = new();
    private ImmutableDictionary<EntityId, string?> _tracked = ImmutableDictionary<EntityId, string?>.Empty;
    private ITimer? _timer;
    private int _next;
    private bool _ended;

    public ReplaySignalSource(ReplayData data, SignalInbox inbox, SignalClock clock, ILogger<ReplaySignalSource> logger)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(clock);
        _data = data;
        _inbox = inbox;
        _time = clock.Time;
        _logger = logger;
        _rowsByEntity = Enumerable.Range(0, data.Rows.Length)
            .GroupBy(i => data.Rows[i].Entity)
            .ToImmutableDictionary(g => g.Key, g => g.ToImmutableArray());
    }

    /// <summary>Starts playing from the current signal-clock time.</summary>
    public void Start()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            _next = FirstAfter(now);
            _timer = _time.CreateTimer(_ => Emit(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _inbox.Post(SignalSourceStatus.Connected);
            Arm(now);
        }
    }

    public void Track(IReadOnlyCollection<TrackedEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        lock (_gate)
        {
            _tracked = entities.ToImmutableDictionary(e => e.Entity, e => e.DeclaredUnit);
            if (_tracked.IsEmpty)
            {
                return;
            }

            var now = _time.GetUtcNow();
            var updates = ImmutableArray.CreateBuilder<EntityUpdate>();
            var missing = ImmutableArray.CreateBuilder<EntityId>();
            foreach (var (entity, unit) in _tracked.OrderBy(t => t.Key))
            {
                if (LatestAtOrBefore(entity, now) is { } index)
                {
                    var row = _data.Rows[index];
                    updates.Add(new EntityUpdate(entity, row.State, unit, row.Timestamp, row.Timestamp));
                }
                else
                {
                    missing.Add(entity);
                }
            }

            _inbox.Post(new EntityUpdateBatch(BatchOrigin.Initial, updates.ToImmutable(), [], missing.ToImmutable()));
        }
    }

    public void RequestProbe(EntityId entity)
    {
        lock (_gate)
        {
            if (!_tracked.TryGetValue(entity, out var unit))
            {
                return;
            }

            var now = _time.GetUtcNow();
            if (LatestAtOrBefore(entity, now) is not { } index)
            {
                _inbox.Post(new EntityUpdateBatch(BatchOrigin.Probe, [], [], [entity]));
                return;
            }

            var row = _data.Rows[index];
            var reported = row.LastReported ?? NextOf(entity, index) ?? _data.End;
            if (reported > now)
            {
                reported = now;
            }

            if (reported < row.Timestamp)
            {
                reported = row.Timestamp;
            }

            _inbox.Post(new EntityUpdateBatch(BatchOrigin.Probe, [new EntityUpdate(entity, row.State, unit, row.Timestamp, reported)], [], []));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Emit()
    {
        lock (_gate)
        {
            if (_timer is null)
            {
                return;
            }

            var now = _time.GetUtcNow();
            var updates = ImmutableArray.CreateBuilder<EntityUpdate>();
            while (_next < _data.Rows.Length && _data.Rows[_next].Timestamp <= now)
            {
                var row = _data.Rows[_next++];
                if (_tracked.TryGetValue(row.Entity, out var unit))
                {
                    updates.Add(new EntityUpdate(row.Entity, row.State, unit, row.Timestamp, row.Timestamp));
                }
            }

            if (updates.Count > 0)
            {
                _inbox.Post(new EntityUpdateBatch(BatchOrigin.Change, updates.ToImmutable(), [], []));
            }

            Arm(now);
        }
    }

    private void Arm(DateTimeOffset now)
    {
        if (_next < _data.Rows.Length)
        {
            var due = _data.Rows[_next].Timestamp - now;
            _timer!.Change(due > TimeSpan.Zero ? due : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
        else if (!_ended)
        {
            _ended = true;
            _logger.LogInformation("Replay reached the end of the recorded data ({End:O}); signals now go stale", _data.End);
        }
    }

    private int FirstAfter(DateTimeOffset now)
    {
        var index = 0;
        while (index < _data.Rows.Length && _data.Rows[index].Timestamp <= now)
        {
            index++;
        }

        return index;
    }

    private int? LatestAtOrBefore(EntityId entity, DateTimeOffset now)
    {
        if (!_rowsByEntity.TryGetValue(entity, out var rows))
        {
            return null;
        }

        var position = LastPositionAtOrBefore(rows, now);
        return position < 0 ? null : rows[position];
    }

    /// <summary>Binary search: the position of the last row of the entity at or before <paramref name="time"/>, or -1.</summary>
    private int LastPositionAtOrBefore(ImmutableArray<int> rows, DateTimeOffset time)
    {
        int low = 0, high = rows.Length - 1, found = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (_data.Rows[rows[middle]].Timestamp <= time)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found;
    }

    private DateTimeOffset? NextOf(EntityId entity, int index)
    {
        var rows = _rowsByEntity[entity];
        var position = LastPositionAtOrBefore(rows, _data.Rows[index].Timestamp);
        return position + 1 < rows.Length ? _data.Rows[rows[position + 1]].Timestamp : null;
    }
}
