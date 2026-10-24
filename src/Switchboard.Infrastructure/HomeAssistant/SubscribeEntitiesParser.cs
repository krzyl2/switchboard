using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Switchboard.Application.Signals;

namespace Switchboard.Infrastructure.HomeAssistant;

/// <summary>
/// Decodes the compressed state stream of one <c>subscribe_entities</c> subscription. The first
/// event is the initial state of every requested entity (<c>a</c>); later events carry additions
/// (<c>a</c>), diffs (<c>c</c> with <c>+</c> and <c>-</c>) and removals (<c>r</c>). A state carries
/// <c>s</c>, <c>a</c> (attributes), <c>lc</c> (last_changed) and <c>lu</c> (last_updated, left out
/// when equal to lc); <c>lr</c> (last_reported) is read when present. Stateful: diffs only make
/// sense against the cached state, so one parser serves one subscription.
/// </summary>
public sealed class SubscribeEntitiesParser
{
    private const string UnitAttribute = "unit_of_measurement";

    private readonly ImmutableArray<EntityId> _requested;
    private readonly Dictionary<EntityId, CachedState> _states = [];
    private bool _initialSeen;

    public SubscribeEntitiesParser(IEnumerable<EntityId> requested)
    {
        _requested = [.. requested];
    }

    /// <summary>
    /// Parses one event body. Returns null with a reason when the event is malformed; the caller
    /// logs it and the session continues (nothing from that event is applied).
    /// </summary>
    public EntityUpdateBatch? Parse(JsonElement body, out string? problem)
    {
        problem = null;
        if (body.ValueKind != JsonValueKind.Object)
        {
            problem = "the event is not an object";
            return null;
        }

        // Work on a copy so a malformed event leaves the cache untouched.
        var working = new Dictionary<EntityId, CachedState>(_states);
        var updates = ImmutableArray.CreateBuilder<EntityUpdate>();
        var removed = ImmutableArray.CreateBuilder<EntityId>();
        var origin = _initialSeen ? BatchOrigin.Change : BatchOrigin.Initial;
        try
        {
            if (body.TryGetProperty("a", out var additions))
            {
                if (_initialSeen)
                {
                    origin = BatchOrigin.Added;
                }

                foreach (var entity in Entries(additions, "a"))
                {
                    if (Track(entity.Name) is { } id)
                    {
                        var state = Full(entity.Value);
                        working[id] = state;
                        updates.Add(state.ToUpdate(id));
                    }
                }
            }

            if (body.TryGetProperty("c", out var changes))
            {
                foreach (var entity in Entries(changes, "c"))
                {
                    if (Track(entity.Name) is { } id && working.TryGetValue(id, out var cached))
                    {
                        var state = Diff(cached, entity.Value);
                        working[id] = state;
                        updates.Add(state.ToUpdate(id));
                    }
                }
            }

            if (body.TryGetProperty("r", out var removals))
            {
                if (removals.ValueKind != JsonValueKind.Array)
                {
                    throw new FormatException("'r' is not a list");
                }

                foreach (var item in removals.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && Track(item.GetString()) is { } id)
                    {
                        working.Remove(id);
                        removed.Add(id);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException)
        {
            problem = ex.Message;
            return null;
        }

        var missing = ImmutableArray<EntityId>.Empty;
        if (!_initialSeen)
        {
            _initialSeen = true;
            missing = [.. _requested.Where(id => !working.ContainsKey(id))];
        }

        _states.Clear();
        foreach (var (id, state) in working)
        {
            _states[id] = state;
        }

        return new EntityUpdateBatch(origin, updates.ToImmutable(), removed.ToImmutable(), missing);
    }

    private EntityId? Track(string? name) =>
        EntityId.TryParse(name, out var id) && _requested.Contains(id.Value) ? id : null;

    private static IEnumerable<JsonProperty> Entries(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject()
            : throw new FormatException($"'{key}' is not an object");

    private static CachedState Full(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("a compressed state is not an object");
        }

        var lastChanged = Timestamp(state, "lc") ?? throw new FormatException("a compressed state has no 'lc'");
        return new CachedState(
            State(state) ?? throw new FormatException("a compressed state has no 's'"),
            Unit(state, null),
            lastChanged,
            Timestamp(state, "lu") ?? lastChanged,
            Timestamp(state, "lr"));
    }

    private static CachedState Diff(CachedState cached, JsonElement diff)
    {
        if (diff.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("a state diff is not an object");
        }

        var next = cached with { LastReported = null };
        if (diff.TryGetProperty("+", out var plus))
        {
            if (plus.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("'+' is not an object");
            }

            next = next with { State = State(plus) ?? next.State, Unit = Unit(plus, next.Unit) };

            // A new last_changed means a new state, and HA updates last_updated with it.
            if (Timestamp(plus, "lc") is { } lastChanged)
            {
                next = next with { LastChanged = lastChanged, LastUpdated = lastChanged };
            }

            if (Timestamp(plus, "lu") is { } lastUpdated)
            {
                next = next with { LastUpdated = lastUpdated };
            }

            next = next with { LastReported = Timestamp(plus, "lr") };
        }

        if (diff.TryGetProperty("-", out var minus) && minus.ValueKind == JsonValueKind.Object
            && minus.TryGetProperty("a", out var removedAttributes) && removedAttributes.ValueKind == JsonValueKind.Array
            && removedAttributes.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.String && a.GetString() == UnitAttribute))
        {
            next = next with { Unit = null };
        }

        return next;
    }

    private static string? State(JsonElement state) =>
        state.TryGetProperty("s", out var s)
            ? s.ValueKind == JsonValueKind.String ? s.GetString() : throw new FormatException("'s' is not a string")
            : null;

    private static string? Unit(JsonElement state, string? current)
    {
        if (!state.TryGetProperty("a", out var attributes))
        {
            return current;
        }

        if (attributes.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("'a' is not an object");
        }

        if (!attributes.TryGetProperty(UnitAttribute, out var unit))
        {
            return current;
        }

        return unit.ValueKind == JsonValueKind.String ? unit.GetString() : null;
    }

    /// <summary>
    /// HA writes timestamps as epoch seconds with microseconds. Read as decimal, not double, so a
    /// value can be compared exactly with the same instant read elsewhere.
    /// </summary>
    private static DateTimeOffset? Timestamp(JsonElement state, string key)
    {
        if (!state.TryGetProperty(key, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !decimal.TryParse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            throw new FormatException($"'{key}' is not a timestamp");
        }

        var microseconds = (long)decimal.Round(seconds * 1_000_000m, MidpointRounding.AwayFromZero);
        return DateTimeOffset.UnixEpoch.AddTicks(microseconds * (TimeSpan.TicksPerMillisecond / 1000));
    }

    private sealed record CachedState(string State, string? Unit, DateTimeOffset LastChanged, DateTimeOffset LastUpdated, DateTimeOffset? LastReported)
    {
        /// <summary>last_reported where present, else the newer of last_updated and last_changed, as HA defines it.</summary>
        public EntityUpdate ToUpdate(EntityId id)
        {
            var updated = LastUpdated > LastChanged ? LastUpdated : LastChanged;
            return new EntityUpdate(id, State, Unit, updated, LastReported is { } reported && reported > updated ? reported : updated);
        }
    }
}
