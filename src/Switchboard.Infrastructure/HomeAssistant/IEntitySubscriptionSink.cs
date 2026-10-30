using Switchboard.Application.Signals;

namespace Switchboard.Infrastructure.HomeAssistant;

/// <summary>
/// What the Home Assistant client subscribes to and where the decoded states go. The client asks
/// for <see cref="Tracked"/> at the start of every session and again whenever
/// <see cref="TrackedChanged"/> fires.
/// </summary>
public interface IEntitySubscriptionSink
{
    /// <summary>The entities to subscribe to, sorted and distinct; empty means no subscription.</summary>
    IReadOnlyList<EntityId> Tracked { get; }

    /// <summary>
    /// The one series attribute kept per entity (15-PLANNER, slice 2), for the entities that have a series signal; nothing else of
    /// any attribute is ever kept. Empty for a sink that tracks none.
    /// </summary>
    IReadOnlyDictionary<EntityId, SeriesSpec> SeriesSpecs => System.Collections.Immutable.ImmutableDictionary<EntityId, SeriesSpec>.Empty;

    /// <summary>The attributes whose text is kept per entity (sorted), for the entities that have a timestamp signal read from an attribute (a sun time); empty for a sink that tracks none.</summary>
    IReadOnlyDictionary<EntityId, System.Collections.Immutable.ImmutableArray<string>> TimestampAttributes => System.Collections.Immutable.ImmutableDictionary<EntityId, System.Collections.Immutable.ImmutableArray<string>>.Empty;

    /// <summary>The entities that have a calendar signal: the transport keeps the five calendar attributes of each (EV-02); empty for a sink that tracks none.</summary>
    IReadOnlySet<EntityId> CalendarEntities => new HashSet<EntityId>();

    event EventHandler? TrackedChanged;

    /// <summary>Authenticated; the subscription (if any) is about to be requested.</summary>
    void SessionStarted();

    /// <summary>Nothing is tracked, so there is nothing to wait for: the session is ready.</summary>
    void SessionReady();

    /// <summary>A decoded event of the active subscription; an initial batch also means ready.</summary>
    void Deliver(EntityUpdateBatch batch);

    void SessionEnded();
}
