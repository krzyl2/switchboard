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
/// when equal to lc); <c>lr</c> (last_reported) is read when present. Of the attributes only the
/// unit and a select's <c>options</c> are kept, and for an entity with a <see cref="SeriesSpec"/> the one
/// named series attribute (bounded, see <see cref="SeriesAttributeParser"/>), and for an entity read from an attribute (a sun time) that attribute's text. Stateful: diffs only make sense against the cached
/// state, so one parser serves one subscription.
/// </summary>
public sealed class SubscribeEntitiesParser
{
    private const string UnitAttribute = "unit_of_measurement";
    private const string OptionsAttribute = "options";

    private readonly ImmutableArray<EntityId> _requested;
    private readonly IReadOnlyDictionary<EntityId, SeriesSpec> _series;
    private readonly IReadOnlyDictionary<EntityId, ImmutableArray<string>> _attributes;
    private readonly Dictionary<EntityId, CachedState> _states = [];
    private readonly TimeZoneInfo? _localZone;
    private readonly IReadOnlySet<EntityId> _calendars;
    private bool _initialSeen;

    /// <param name="localZone">The zone a series time without a UTC offset is local in (the add-on's, as the slot grid); null refuses such a time.</param>
    /// <param name="calendars">The entities whose five calendar attributes are kept (calendar signals); empty keeps none.</param>
    public SubscribeEntitiesParser(IEnumerable<EntityId> requested, IReadOnlyDictionary<EntityId, SeriesSpec>? series = null, IReadOnlyDictionary<EntityId, ImmutableArray<string>>? attributes = null, TimeZoneInfo? localZone = null, IReadOnlySet<EntityId>? calendars = null)
    {
        _localZone = localZone;
        _calendars = calendars ?? new HashSet<EntityId>();
        _requested = [.. requested];
        _series = series ?? new Dictionary<EntityId, SeriesSpec>();
        _attributes = attributes ?? new Dictionary<EntityId, ImmutableArray<string>>();
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
                        var state = Full(entity.Value, _series.GetValueOrDefault(id), _attributes.GetValueOrDefault(id), _calendars.Contains(id));
                        working[id] = state;
                        updates.Add(state.ToUpdate(id, _localZone));
                    }
                }
            }

            if (body.TryGetProperty("c", out var changes))
            {
                foreach (var entity in Entries(changes, "c"))
                {
                    if (Track(entity.Name) is { } id && working.TryGetValue(id, out var cached))
                    {
                        var state = Diff(cached, entity.Value, _series.GetValueOrDefault(id), _attributes.GetValueOrDefault(id), _calendars.Contains(id));
                        working[id] = state;
                        updates.Add(state.ToUpdate(id, _localZone));
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

    private CachedState Full(JsonElement state, SeriesSpec? series, ImmutableArray<string> attributes, bool calendar)
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
            Timestamp(state, "lr"))
        {
            Options = Options(state, null),
            Series = series is null ? null : Series(state, series, ParsedSeries.Of(SeriesProblem.Missing)),
            Attributes = attributes.IsDefaultOrEmpty ? null : AttributeTexts(state, attributes, null),
            Calendar = !calendar ? null : state.TryGetProperty("a", out var calendarAttributes) ? CalendarFields.Empty.Apply(calendarAttributes) : CalendarFields.Empty,
        };
    }

    private CachedState Diff(CachedState cached, JsonElement diff, SeriesSpec? series, ImmutableArray<string> attributes, bool calendar)
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

            next = next with { State = State(plus) ?? next.State, Unit = Unit(plus, next.Unit), Options = Options(plus, next.Options) };
            if (series is not null)
            {
                next = next with { Series = Series(plus, series, next.Series) };
            }

            if (!attributes.IsDefaultOrEmpty)
            {
                next = next with { Attributes = AttributeTexts(plus, attributes, next.Attributes) };
            }

            if (calendar && plus.TryGetProperty("a", out var calendarAttributes))
            {
                next = next with { Calendar = (next.Calendar ?? CalendarFields.Empty).Apply(calendarAttributes) };
            }

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
            && minus.TryGetProperty("a", out var removedAttributes) && removedAttributes.ValueKind == JsonValueKind.Array)
        {
            var removed = removedAttributes.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()).ToList();
            if (removed.Contains(UnitAttribute))
            {
                next = next with { Unit = null };
            }

            if (removed.Contains(OptionsAttribute))
            {
                next = next with { Options = null };
            }

            if (series is not null && removed.Contains(series.Attribute))
            {
                next = next with { Series = ParsedSeries.Of(SeriesProblem.Missing) };
            }

            if (calendar)
            {
                next = next with { Calendar = (next.Calendar ?? CalendarFields.Empty).Remove(removed) };
            }

            if (!attributes.IsDefaultOrEmpty)
            {
                next = next with { Attributes = (next.Attributes ?? ImmutableDictionary<string, string>.Empty).SetItems(attributes.Where(removed.Contains).Select(a => KeyValuePair.Create(a!, string.Empty))) };
            }
        }

        return next;
    }

    /// <summary>
    /// A select's options from the attributes, replacing <paramref name="current"/> when the
    /// attributes carry them; a list of the wrong shape or beyond the schema bounds reads as unknown
    /// (null), never as an error that would drop the state itself.
    /// </summary>
    private static ImmutableArray<string>? Options(JsonElement state, ImmutableArray<string>? current)
    {
        if (!state.TryGetProperty("a", out var attributes) || attributes.ValueKind != JsonValueKind.Object || !attributes.TryGetProperty(OptionsAttribute, out var options))
        {
            return current;
        }

        return options.ValueKind == JsonValueKind.Array && options.GetArrayLength() <= EntityUpdate.MaxOptions
            ? EntityUpdate.BoundedOptions([.. options.EnumerateArray().Select(o => o.ValueKind == JsonValueKind.String ? o.GetString() : null)])
            : null;
    }

    /// <summary>
    /// The whitelisted series attribute replacing <paramref name="current"/> when the attributes carry it; a problem is part of the
    /// result (the signal is refused with the reason), never an error that would drop the state itself.
    /// </summary>
    private ParsedSeries? Series(JsonElement state, SeriesSpec spec, ParsedSeries? current) =>
        state.TryGetProperty("a", out var attributes) && attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty(spec.Attribute, out var attribute)
            ? SeriesAttributeParser.Parse(attribute, spec, _localZone)
            : current;

    /// <summary>
    /// The text of each whitelisted attribute, replacing the one in <paramref name="current"/> when the attributes carry it; a name never seen is empty. A value
    /// that is not text or longer than <see cref="EntityUpdate.MaxAttributeText"/> reads as empty (no usable attribute), never as an error that would drop the
    /// state itself.
    /// </summary>
    private static ImmutableDictionary<string, string> AttributeTexts(JsonElement state, ImmutableArray<string> names, ImmutableDictionary<string, string>? current)
    {
        var attributes = state.TryGetProperty("a", out var element) && element.ValueKind == JsonValueKind.Object ? element : (JsonElement?)null;
        var texts = ImmutableDictionary.CreateBuilder<string, string>();
        foreach (var name in names)
        {
            if (attributes is { } carried && carried.TryGetProperty(name, out var attribute))
            {
                texts[name] = attribute.ValueKind == JsonValueKind.String && attribute.GetString() is { Length: > 0 and <= EntityUpdate.MaxAttributeText } text ? text : string.Empty;
            }
            else
            {
                texts[name] = current is not null && current.TryGetValue(name, out var kept) ? kept : string.Empty;
            }
        }

        return texts.ToImmutable();
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
        public ImmutableArray<string>? Options { get; init; }

        public ParsedSeries? Series { get; init; }

        public ImmutableDictionary<string, string>? Attributes { get; init; }

        internal CalendarFields? Calendar { get; init; }

        /// <summary>last_reported where present, else the newer of last_updated and last_changed, as HA defines it.</summary>
        public EntityUpdate ToUpdate(EntityId id, TimeZoneInfo? localZone)
        {
            var updated = LastUpdated > LastChanged ? LastUpdated : LastChanged;
            return new EntityUpdate(id, State, Unit, updated, LastReported is { } reported && reported > updated ? reported : updated) { Options = Options, Series = Series, Attributes = Attributes, Calendar = Calendar?.Parse(localZone) };
        }
    }
}
