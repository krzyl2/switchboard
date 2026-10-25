using System.Collections.Frozen;
using System.Collections.Immutable;
using Switchboard.Application.Signals;

namespace Switchboard.Application.Setup.Discovery;

/// <summary>
/// One entity as Home Assistant's state machine and entity registry describe it, read-only.
/// <see cref="Platform"/> is the integration that provides it (from the registry): what tells a
/// primary measurement from a derived one. <see cref="InRegistry"/> is false for an entity defined
/// in YAML without a unique_id, whose source therefore cannot be verified.
/// </summary>
public sealed record DiscoveredEntity(
    EntityId Entity,
    string? Name,
    string? DeviceClass,
    string? StateClass,
    string? Unit,
    string? Platform,
    string? DeviceId,
    bool Disabled,
    bool Hidden,
    string? State,
    DateTimeOffset? Reported,
    bool InRegistry)
{
    public string Domain => Entity.Value[..Entity.Value.IndexOf('.', StringComparison.Ordinal)];
}

public sealed record DiscoveredDevice(string Id, string? Name, string? Manufacturer, string? Model);

/// <summary>
/// One discovery: every entity (bounded, <see cref="Truncated"/> when the bound cut it) and the
/// devices they belong to. <see cref="RegistryAvailable"/> is false when the entity registry could
/// not be read: then no entity's platform is known and derived sensors cannot be told apart.
/// </summary>
public sealed record DiscoverySnapshot(
    DateTimeOffset TakenAt,
    ImmutableArray<DiscoveredEntity> Entities,
    ImmutableArray<DiscoveredDevice> Devices,
    bool RegistryAvailable,
    bool Truncated)
{
    // Built on first use for the entities it was built from; a copy made with `with` builds its own.
    // One reference, published whole: concurrent requests sharing a snapshot never see half an index.
    private EntityIndex? _index;

    /// <summary>
    /// The platform of every entity in the registry (null when the entry names none), never cut by the
    /// discovery bound: a derived sensor past the cut is still recognised (07).
    /// </summary>
    public IReadOnlyDictionary<EntityId, string?> RegistryPlatforms { get; init; } = FrozenDictionary<EntityId, string?>.Empty;

    public DiscoveredEntity? Find(EntityId entity)
    {
        var built = Volatile.Read(ref _index);
        if (built is null || built.For != Entities)
        {
            built = new EntityIndex(Entities, Entities.DistinctBy(e => e.Entity).ToFrozenDictionary(e => e.Entity));
            Volatile.Write(ref _index, built);
        }

        return built.Map.GetValueOrDefault(entity);
    }

    private sealed record EntityIndex(ImmutableArray<DiscoveredEntity> For, FrozenDictionary<EntityId, DiscoveredEntity> Map);
}

/// <summary>Reads the entities Home Assistant offers, with read-only queries only.</summary>
public interface IEntityDiscoverySource
{
    /// <exception cref="DiscoveryUnavailableException">Home Assistant cannot be reached or answered in a way that cannot be used.</exception>
    Task<DiscoverySnapshot> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Why a discovery has no answer; <see cref="Code"/> is what the API returns.</summary>
public sealed class DiscoveryUnavailableException(string code, string message) : Exception(message)
{
    public const string HomeAssistantUnavailable = "home_assistant_unavailable";

    /// <summary>An answer above the transport's message limit (a very large installation).</summary>
    public const string TooLarge = "discovery_too_large";

    public string Code { get; } = code;
}

/// <summary>
/// 07-PITFALLS: a sensor computed from other sensors is never a control input. A sensor is derived
/// when its registry platform is one of the shipped derived platforms (template, statistics,
/// powercalc, ...). An entity that is not in the registry (YAML without a unique_id) cannot be
/// verified either way and is flagged, never passed silently.
/// </summary>
public static class DerivedSensorRule
{
    public static DerivedCheck Check(DiscoveredEntity? entity, DiscoverySnapshot? snapshot, SetupDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        if (snapshot is null || !snapshot.RegistryAvailable)
        {
            return DerivedCheck.RegistryUnavailable;
        }

        if (entity is null || !entity.InRegistry)
        {
            return DerivedCheck.NotInRegistry;
        }

        return entity.Platform is { } platform && defaults.DerivedPlatforms.Contains(platform, StringComparer.Ordinal)
            ? DerivedCheck.Derived
            : DerivedCheck.Primary;
    }

    /// <summary>
    /// The check for an entity ID: the discovered entity when the bound kept it, else its registry
    /// entry, so truncating the list shown to the user can never hide a derived platform.
    /// </summary>
    public static DerivedCheck Check(EntityId entity, DiscoverySnapshot? snapshot, SetupDefaults defaults)
    {
        if (snapshot?.Find(entity) is { } discovered)
        {
            return Check(discovered, snapshot, defaults);
        }

        if (snapshot is { RegistryAvailable: true } && snapshot.RegistryPlatforms.TryGetValue(entity, out var platform))
        {
            return platform is not null && defaults.DerivedPlatforms.Contains(platform, StringComparer.Ordinal) ? DerivedCheck.Derived : DerivedCheck.Primary;
        }

        return Check((DiscoveredEntity?)null, snapshot, defaults);
    }

    /// <summary>The registry platform of an entity, whether or not the bound kept it in the list.</summary>
    public static string? PlatformOf(EntityId entity, DiscoverySnapshot? snapshot) =>
        snapshot?.Find(entity)?.Platform ?? (snapshot?.RegistryPlatforms.GetValueOrDefault(entity));
}

public enum DerivedCheck
{
    Primary,
    Derived,
    NotInRegistry,
    RegistryUnavailable,
}

/// <summary>
/// Discovery answers reused for a while (DiscoveryCacheTtl, host clock) and refreshed on request;
/// one read at a time, so a burst of wizard requests sends the queries once.
/// </summary>
public sealed class EntityDiscoveryCache
{
    private readonly IEntityDiscoverySource _source;
    private readonly TimeProvider _time;
    private readonly TimeSpan _ttl;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Cached? _cached;

    public EntityDiscoveryCache(IEntityDiscoverySource source, TimeProvider time, TimeSpan ttl)
    {
        _source = source;
        _time = time;
        _ttl = ttl;
    }

    /// <summary>The last answer, however old; null before the first.</summary>
    public DiscoverySnapshot? Last => Volatile.Read(ref _cached)?.Snapshot;

    public async Task<DiscoverySnapshot> GetAsync(bool refresh, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            if (!refresh && _cached is { } cached && now >= cached.ReadAt && now - cached.ReadAt < _ttl)
            {
                return cached.Snapshot;
            }

            var snapshot = await _source.ReadAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _cached, new Cached(snapshot, _time.GetUtcNow()));
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record Cached(DiscoverySnapshot Snapshot, DateTimeOffset ReadAt);
}
