using System.Collections.Immutable;

namespace Switchboard.Application.Signals;

/// <summary>
/// One entity state as Home Assistant reported it. <see cref="LastUpdated"/> identifies the state
/// (it changes whenever the state or its attributes change); <see cref="Reported"/> is the latest
/// moment HA confirmed it: <c>last_reported</c> where HA provides it, otherwise <c>last_updated</c>,
/// otherwise <c>last_changed</c>, exactly as HA itself falls back (SIG-06).
/// </summary>
/// <param name="Unit">The <c>unit_of_measurement</c> attribute; null when the entity has none.</param>
public sealed record EntityUpdate(EntityId Entity, string State, string? Unit, DateTimeOffset LastUpdated, DateTimeOffset Reported);

public enum BatchOrigin
{
    /// <summary>The full state of every tracked entity at the start of a subscription.</summary>
    Initial,

    /// <summary>A state change pushed by Home Assistant.</summary>
    Change,

    /// <summary>An entity that appeared after the subscription started.</summary>
    Added,

    /// <summary>The answer to a freshness probe (a read of the entity's current state).</summary>
    Probe,
}

/// <summary>Updates that arrived together; the store applies a batch as a whole.</summary>
/// <param name="Removed">Entities Home Assistant removed.</param>
/// <param name="Missing">Tracked entities absent from an initial state, or unknown to a probe.</param>
public sealed record EntityUpdateBatch(
    BatchOrigin Origin,
    ImmutableArray<EntityUpdate> Updates,
    ImmutableArray<EntityId> Removed,
    ImmutableArray<EntityId> Missing)
{
    public static EntityUpdateBatch Of(BatchOrigin origin, params EntityUpdate[] updates) => new(origin, [.. updates], [], []);
}

/// <summary>An entity the store needs, with the unit the configuration declares for it.</summary>
public sealed record TrackedEntity(EntityId Entity, string? DeclaredUnit);
