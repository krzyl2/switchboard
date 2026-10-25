using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Switchboard.Application.Configuration;
using Switchboard.Application.Setup.Discovery;
using Switchboard.Application.Setup.Readouts;
using Switchboard.Application.Signals;
using Switchboard.Domain.Signals;
using Switchboard.Infrastructure.HomeAssistant;

namespace Switchboard.Infrastructure.Replay;

/// <summary>
/// Development replay only (SWITCHBOARD_DEV_DISCOVERY next to SWITCHBOARD_DEV_REPLAY): the three
/// Home Assistant answers the discovery link would get (states.json, entity_registry.json,
/// device_registry.json, in HA's own shapes) from a fixture folder. Never used in add-on mode or
/// without replay; the composition refuses both.
/// </summary>
public sealed class ReplayDiscoveryFixture
{
    public const string StatesFile = "states.json";
    public const string EntityRegistryFile = "entity_registry.json";
    public const string DeviceRegistryFile = "device_registry.json";

    private ReplayDiscoveryFixture(JsonElement states, JsonElement entityRegistry, JsonElement deviceRegistry)
    {
        States = states;
        EntityRegistry = entityRegistry;
        DeviceRegistry = deviceRegistry;
        Units = DiscoveryResponseParser.Parse(states, null, null, DateTimeOffset.MinValue, int.MaxValue).Entities
            .Where(e => e.Unit is not null)
            .ToImmutableDictionary(e => e.Entity, e => e.Unit!);
    }

    public JsonElement States { get; }

    public JsonElement EntityRegistry { get; }

    public JsonElement DeviceRegistry { get; }

    /// <summary>The unit_of_measurement of each state in the fixture (the recording itself has no units).</summary>
    public ImmutableDictionary<EntityId, string> Units { get; }

    public static ReplayDiscoveryFixture Load(string directory)
    {
        static JsonElement Read(string path)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.Clone();
        }

        return new ReplayDiscoveryFixture(
            Read(Path.Combine(directory, StatesFile)),
            Read(Path.Combine(directory, EntityRegistryFile)),
            Read(Path.Combine(directory, DeviceRegistryFile)));
    }
}

/// <summary>Discovery in development replay: the fixture's entities, with state and last report taken from the recording at the signal clock's now.</summary>
public sealed class ReplayDiscoverySource(ReplayDiscoveryFixture fixture, ReplayCandidateReadouts readouts, SignalClock clock, int maxEntities) : IEntityDiscoverySource
{
    public Task<DiscoverySnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var snapshot = DiscoveryResponseParser.Parse(fixture.States, fixture.EntityRegistry, fixture.DeviceRegistry, clock.Time.GetUtcNow(), maxEntities);
        var overlaid = snapshot.Entities.Select(e => readouts.Peek(e.Entity) is { Status: ReadoutStatus.Found or ReadoutStatus.Unavailable } live
            ? e with { State = live.State, Reported = live.LastReported }
            : e);
        return Task.FromResult(snapshot with { Entities = [.. overlaid] });
    }
}

/// <summary>
/// Candidate readouts in development replay: computed from the recording on the signal clock, the
/// way the replay signal source answers a probe (a later row proves the entity was alive and
/// unchanged in between). Entities the recording does not hold read from the fixture's states.
/// </summary>
public sealed class ReplayCandidateReadouts : ICandidateReadouts
{
    private readonly ReplayData _data;
    private readonly ReplayDiscoveryFixture _fixture;
    private readonly TimeProvider _time;
    private readonly RefreshDetectionParameters _refresh;
    private readonly int _historyLength;
    private readonly ImmutableDictionary<EntityId, ImmutableArray<ReplayRow>> _rows;
    private readonly ImmutableDictionary<EntityId, string> _fixtureStates;

    public ReplayCandidateReadouts(ReplayData data, ReplayDiscoveryFixture fixture, SignalClock clock, RefreshDetectionParameters refresh, int historyLength)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(clock);
        _data = data;
        _fixture = fixture;
        _time = clock.Time;
        _refresh = refresh;
        _historyLength = historyLength;
        _rows = data.Rows.GroupBy(r => r.Entity).ToImmutableDictionary(g => g.Key, g => g.ToImmutableArray());
        _fixtureStates = DiscoveryResponseParser.Parse(fixture.States, null, null, DateTimeOffset.MinValue, int.MaxValue).Entities
            .Where(e => e.State is not null)
            .ToImmutableDictionary(e => e.Entity, e => e.State!);
    }

    public ImmutableArray<CandidateReadout> Lease(IReadOnlyCollection<EntityId> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        return [.. entities.Distinct().Select(e => Peek(e)!)];
    }

    public IDisposable Hold(IReadOnlyCollection<EntityId> entities) => new Released();

    public CandidateReadout? Peek(EntityId entity)
    {
        var now = _time.GetUtcNow();
        var unit = _fixture.Units.GetValueOrDefault(entity);
        var rows = Before(entity, now);
        if (rows.Count == 0)
        {
            if (_fixtureStates.TryGetValue(entity, out var state))
            {
                return new CandidateReadout(entity, Status(state), state, unit, now, TimeSpan.Zero, null, 0, _refresh.MinIntervals);
            }

            return new CandidateReadout(entity, _rows.ContainsKey(entity) ? ReadoutStatus.Pending : ReadoutStatus.NotFound, null, unit, null, null, null, 0, _refresh.MinIntervals);
        }

        var latest = rows[^1];
        var reported = Reported(entity, latest, now);
        var intervals = rows.TakeLast(_refresh.WindowIntervals + 1).Zip(rows.TakeLast(_refresh.WindowIntervals + 1).Skip(1), (a, b) => b.Timestamp - a.Timestamp).ToList();
        return new CandidateReadout(
            entity,
            Status(latest.State),
            latest.State,
            unit,
            reported,
            now - reported,
            RefreshInterval.Estimate(intervals, _refresh.Quantile, _refresh.MinIntervals),
            intervals.Count,
            _refresh.MinIntervals);
    }

    public ImmutableArray<EntityUpdate> History(EntityId entity)
    {
        var unit = _fixture.Units.GetValueOrDefault(entity);
        return [.. Before(entity, _time.GetUtcNow()).TakeLast(_historyLength).Select(r => new EntityUpdate(entity, r.State, unit, r.Timestamp, r.Timestamp))];
    }

    private static ReadoutStatus Status(string state) =>
        state is SignalNormalizer.UnavailableState or SignalNormalizer.UnknownState ? ReadoutStatus.Unavailable : ReadoutStatus.Found;

    private List<ReplayRow> Before(EntityId entity, DateTimeOffset now) =>
        _rows.TryGetValue(entity, out var rows) ? [.. rows.Where(r => r.Timestamp <= now)] : [];

    private DateTimeOffset Reported(EntityId entity, ReplayRow row, DateTimeOffset now)
    {
        var next = _rows[entity].FirstOrDefault(r => r.Timestamp > row.Timestamp);
        var reported = row.LastReported ?? next?.Timestamp ?? _data.End;
        reported = reported > now ? now : reported;
        return reported < row.Timestamp ? row.Timestamp : reported;
    }

    private sealed class Released : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
