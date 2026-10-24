using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Signals;
using Switchboard.Domain.Signals;

namespace Switchboard.Infrastructure.HomeAssistant;

/// <summary>
/// The live signal source: the Home Assistant client's subscription delivers pushed changes, the
/// state reader answers freshness probes. Everything lands in the store's inbox; source status
/// follows the session (connecting at authentication, connected once the initial state is in).
/// </summary>
public sealed class HomeAssistantSignalSource : ISignalSource, IEntitySubscriptionSink, IDisposable
{
    private readonly SignalInbox _inbox;
    private readonly IEntityStateReader _reader;
    private readonly ILogger<HomeAssistantSignalSource> _logger;
    private readonly ConcurrentDictionary<EntityId, byte> _probing = new();
    private readonly CancellationTokenSource _stopping = new();
    private ImmutableArray<EntityId> _tracked = [];

    public HomeAssistantSignalSource(SignalInbox inbox, IEntityStateReader reader, ILogger<HomeAssistantSignalSource> logger)
    {
        _inbox = inbox;
        _reader = reader;
        _logger = logger;
    }

    public event EventHandler? TrackedChanged;

    public IReadOnlyList<EntityId> Tracked => _tracked;

    public void Track(IReadOnlyCollection<TrackedEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ImmutableArray<EntityId> next = [.. entities.Select(e => e.Entity).Distinct().Order()];
        var previous = ImmutableInterlocked.InterlockedExchange(ref _tracked, next);
        if (!previous.SequenceEqual(next))
        {
            TrackedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>At most one read per entity in flight; the answer arrives as a probe batch.</summary>
    public void RequestProbe(EntityId entity)
    {
        if (!_probing.TryAdd(entity, 0))
        {
            return;
        }

        _ = ProbeAsync(entity);
    }

    public void SessionStarted() => _inbox.Post(SignalSourceStatus.Connecting);

    public void SessionReady() => _inbox.Post(SignalSourceStatus.Connected);

    public void Deliver(EntityUpdateBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        _inbox.Post(batch);
        if (batch.Origin == BatchOrigin.Initial)
        {
            _inbox.Post(SignalSourceStatus.Connected);
        }
    }

    public void SessionEnded() => _inbox.Post(SignalSourceStatus.Disconnected);

    /// <summary>Abandons probes in flight. The token source is cancelled, not disposed, so a late request cannot throw.</summary>
    public void Dispose() => _stopping.Cancel();

    private async Task ProbeAsync(EntityId entity)
    {
        try
        {
            var result = await _reader.ReadAsync(entity, _stopping.Token).ConfigureAwait(false);
            switch (result)
            {
                case EntityStateRead.Found found:
                    _inbox.Post(new EntityUpdateBatch(BatchOrigin.Probe, [found.Update], [], []));
                    break;
                case EntityStateRead.NotFound:
                    _inbox.Post(new EntityUpdateBatch(BatchOrigin.Probe, [], [], [entity]));
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogDebug("State read of {Entity} failed: {Reason}", entity, ex.Message);
        }
        finally
        {
            _probing.TryRemove(entity, out _);
        }
    }
}
