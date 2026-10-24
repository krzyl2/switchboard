using System.Collections.Concurrent;
using System.Threading.Channels;
using Switchboard.Application.Signals;
using Switchboard.Infrastructure.HomeAssistant;

namespace Switchboard.Tests.Support;

/// <summary>Stands in for the signal source behind the HA client: a settable tracked set, and a log of everything delivered.</summary>
internal sealed class RecordingSubscriptionSink : IEntitySubscriptionSink
{
    private readonly Channel<EntityUpdateBatch> _batches = Channel.CreateUnbounded<EntityUpdateBatch>();
    private readonly ConcurrentQueue<string> _events = new();
    private IReadOnlyList<EntityId> _tracked = [];

    public event EventHandler? TrackedChanged;

    public IReadOnlyList<EntityId> Tracked => Volatile.Read(ref _tracked);

    /// <summary>Session events in order: started, ready, initial, change, added, probe, ended.</summary>
    public IReadOnlyList<string> Events => [.. _events];

    public void SetTracked(params string[] entities)
    {
        Volatile.Write(ref _tracked, [.. entities.Select(EntityId.Parse).Order()]);
        TrackedChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SessionStarted() => _events.Enqueue("started");

    public void SessionReady() => _events.Enqueue("ready");

    public void Deliver(EntityUpdateBatch batch)
    {
        _events.Enqueue(batch.Origin.ToString().ToLowerInvariant());
        _batches.Writer.TryWrite(batch);
    }

    public void SessionEnded() => _events.Enqueue("ended");

    public async Task<EntityUpdateBatch> NextBatchAsync() => await _batches.Reader.ReadAsync().Soon();

    public bool TryReadBatch(out EntityUpdateBatch? batch) => _batches.Reader.TryRead(out batch);
}
