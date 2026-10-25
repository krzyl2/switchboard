using System.Collections.Immutable;
using Switchboard.Application.Setup.Discovery;
using Switchboard.Application.Setup.Readouts;
using Switchboard.Application.Signals;

namespace Switchboard.Infrastructure.HomeAssistant;

/// <summary>No Home Assistant endpoint (standalone without SWITCHBOARD_HA_URL): discovery is unavailable and nothing is subscribed.</summary>
public sealed class UnavailableSetupSource : IEntityDiscoverySource, ICandidateReadouts
{
    public Task<DiscoverySnapshot> ReadAsync(CancellationToken cancellationToken) =>
        Task.FromException<DiscoverySnapshot>(new DiscoveryUnavailableException(DiscoveryUnavailableException.HomeAssistantUnavailable, "No Home Assistant endpoint is configured."));

    public ImmutableArray<CandidateReadout> Lease(IReadOnlyCollection<EntityId> entities) =>
        [.. entities.Distinct().Select(e => new CandidateReadout(e, ReadoutStatus.Pending, null, null, null, null, null, 0, 0))];

    public IDisposable Hold(IReadOnlyCollection<EntityId> entities) => new Nothing();

    public CandidateReadout? Peek(EntityId entity) => null;

    public ImmutableArray<EntityUpdate> History(EntityId entity) => [];

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
