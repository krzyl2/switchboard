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

    event EventHandler? TrackedChanged;

    /// <summary>Authenticated; the subscription (if any) is about to be requested.</summary>
    void SessionStarted();

    /// <summary>Nothing is tracked, so there is nothing to wait for: the session is ready.</summary>
    void SessionReady();

    /// <summary>A decoded event of the active subscription; an initial batch also means ready.</summary>
    void Deliver(EntityUpdateBatch batch);

    void SessionEnded();
}
