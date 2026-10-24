using System.Collections.Concurrent;
using Switchboard.Application.Signals;

namespace Switchboard.Tests.Support;

/// <summary>Records what the runner asks of its source.</summary>
internal sealed class RecordingSignalSource : ISignalSource
{
    private readonly ConcurrentQueue<IReadOnlyList<TrackedEntity>> _tracks = new();
    private readonly ConcurrentQueue<EntityId> _probes = new();

    public IReadOnlyList<IReadOnlyList<TrackedEntity>> Tracks => [.. _tracks];

    public IReadOnlyList<EntityId> Probes => [.. _probes];

    public void Track(IReadOnlyCollection<TrackedEntity> entities) => _tracks.Enqueue([.. entities]);

    public void RequestProbe(EntityId entity) => _probes.Enqueue(entity);
}
