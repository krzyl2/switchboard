using System.Collections.Immutable;
using Switchboard.Application.Configuration;
using Switchboard.Application.Signals;

namespace Switchboard.Application.Setup.Readouts;

public enum ReadoutStatus
{
    /// <summary>Subscribed, no state received yet (or the link is reconnecting).</summary>
    Pending,

    Found,

    /// <summary>Home Assistant does not know the entity.</summary>
    NotFound,

    /// <summary>The entity exists and reports unavailable or unknown.</summary>
    Unavailable,
}

/// <summary>
/// What the wizard shows beside a picked entity (UI-04): its value as Home Assistant reports it,
/// the age of its last report (last_reported, never last_changed) on the signal clock, and its
/// detected refresh (SIG-07) once enough pushed reports came in.
/// </summary>
/// <param name="Samples">Report intervals seen so far; <paramref name="Needed"/> are needed before a refresh is reported.</param>
public sealed record CandidateReadout(
    EntityId Entity,
    ReadoutStatus Status,
    string? State,
    string? Unit,
    DateTimeOffset? LastReported,
    TimeSpan? Age,
    TimeSpan? Refresh,
    int Samples,
    int Needed);

/// <summary>
/// Live readouts of entities the wizard is showing, through a bounded, temporary, read-only
/// subscription. A lease is renewed by every request and lapses on its own; a hold (a self-test
/// session) keeps its entities subscribed independently of any polling. History and detected
/// refresh outlive a lease, so a refresh detected in step 1 is still known at the save.
/// </summary>
public interface ICandidateReadouts
{
    /// <summary>Subscribes (or keeps subscribed) these entities for one lease and returns their readouts now.</summary>
    ImmutableArray<CandidateReadout> Lease(IReadOnlyCollection<EntityId> entities);

    /// <summary>Keeps these entities subscribed until the returned handle is disposed.</summary>
    IDisposable Hold(IReadOnlyCollection<EntityId> entities);

    /// <summary>The retained readout of an entity, without renewing anything; null when it was never subscribed.</summary>
    CandidateReadout? Peek(EntityId entity);

    /// <summary>The retained reports of an entity, oldest first (the initial state included).</summary>
    ImmutableArray<EntityUpdate> History(EntityId entity);
}

/// <summary>
/// The memory behind the readouts: a bounded report history and a refresh tracker per entity, kept
/// for the discovery link's lifetime. Initial states and probe answers never count as refresh
/// intervals (they are not pushes), and an interval never spans a reconnect.
/// </summary>
public sealed class CandidateReadoutStore
{
    private readonly int _historyLength;
    private readonly int _maxRetained;
    private readonly RefreshDetectionParameters _refresh;
    private readonly object _gate = new();
    private readonly Dictionary<EntityId, Entry> _entries = [];
    private long _touches;

    public CandidateReadoutStore(int historyLength, int maxRetained, RefreshDetectionParameters refresh)
    {
        ArgumentNullException.ThrowIfNull(refresh);
        _historyLength = historyLength;
        _maxRetained = maxRetained;
        _refresh = refresh;
    }

    /// <summary>Marks entities as wanted (for eviction order); unknown ones start pending.</summary>
    public void Touch(IEnumerable<EntityId> entities, IReadOnlySet<EntityId> held)
    {
        ArgumentNullException.ThrowIfNull(entities);
        lock (_gate)
        {
            foreach (var entity in entities)
            {
                if (!_entries.TryGetValue(entity, out var entry))
                {
                    entry = new Entry();
                    _entries[entity] = entry;
                }

                entry.TouchedAt = ++_touches;
            }

            while (_entries.Count > _maxRetained)
            {
                var oldest = _entries.Where(e => !held.Contains(e.Key)).OrderBy(e => e.Value.TouchedAt).Select(e => (EntityId?)e.Key).FirstOrDefault();
                if (oldest is not { } victim)
                {
                    break;
                }

                _entries.Remove(victim);
            }
        }
    }

    public void Deliver(EntityUpdateBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        lock (_gate)
        {
            foreach (var update in batch.Updates)
            {
                if (!_entries.TryGetValue(update.Entity, out var entry))
                {
                    continue;
                }

                if (batch.Origin == BatchOrigin.Probe)
                {
                    // A probe confirms the value in use; it is a report, not a push.
                    entry.Record(update, _historyLength);
                    continue;
                }

                // An initial state carries Home Assistant's last report, possibly hours old: it starts
                // no interval. The first push starts the chain, the second gives the first interval.
                if (batch.Origin is BatchOrigin.Initial)
                {
                    entry.Refresh.Break();
                }
                else
                {
                    entry.Refresh.Observe(update.Reported, _refresh.WindowIntervals);
                }

                entry.Record(update, _historyLength);
            }

            foreach (var gone in batch.Missing.Concat(batch.Removed))
            {
                if (_entries.TryGetValue(gone, out var entry))
                {
                    entry.NotFound = true;
                    entry.Refresh.Break();
                }
            }
        }
    }

    /// <summary>The subscription ended: values stay, but no interval spans the gap.</summary>
    public void SessionEnded()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                entry.Refresh.Break();
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    public CandidateReadout? Read(EntityId entity, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(entity, out var entry))
            {
                return null;
            }

            var latest = entry.History.Count == 0 ? null : entry.History[^1];
            var status = entry.NotFound ? ReadoutStatus.NotFound
                : latest is null ? ReadoutStatus.Pending
                : latest.State is SignalNormalizer.UnavailableState or SignalNormalizer.UnknownState ? ReadoutStatus.Unavailable
                : ReadoutStatus.Found;
            var reported = latest?.Reported;
            return new CandidateReadout(
                entity,
                status,
                latest?.State,
                latest?.Unit,
                reported,
                reported is { } at ? (now > at ? now - at : TimeSpan.Zero) : null,
                entry.Refresh.Estimate(_refresh),
                entry.Refresh.Count,
                _refresh.MinIntervals);
        }
    }

    public ImmutableArray<EntityUpdate> History(EntityId entity)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(entity, out var entry) ? [.. entry.History] : [];
        }
    }

    /// <summary>The last report of an entity, for the freshness probe of a silent candidate.</summary>
    public DateTimeOffset? LastReported(EntityId entity)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(entity, out var entry) && entry.History.Count > 0 ? entry.History[^1].Reported : null;
        }
    }

    private sealed class Entry
    {
        public List<EntityUpdate> History { get; } = [];

        public RefreshTracker Refresh { get; } = new();

        public bool NotFound { get; set; }

        public long TouchedAt { get; set; }

        public void Record(EntityUpdate update, int historyLength)
        {
            NotFound = false;
            if (History.Count > 0 && History[^1].State == update.State && History[^1].LastUpdated == update.LastUpdated)
            {
                // The same state confirmed again: only its report time moves on.
                if (update.Reported > History[^1].Reported)
                {
                    History[^1] = update;
                }

                return;
            }

            History.Add(update);
            if (History.Count > historyLength)
            {
                History.RemoveRange(0, History.Count - historyLength);
            }
        }
    }
}
