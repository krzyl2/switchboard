using System.Collections.Immutable;

namespace Switchboard.Application.Signals;

/// <summary>
/// One entity state as Home Assistant reported it. <see cref="LastUpdated"/> identifies the state
/// (it changes whenever the state or its attributes change); <see cref="Reported"/> is the latest
/// moment HA confirmed it: <c>last_reported</c> where HA provides it, otherwise <c>last_updated</c>,
/// otherwise <c>last_changed</c>, exactly as HA itself falls back (SIG-06).
/// </summary>
/// <param name="Unit">The <c>unit_of_measurement</c> attribute; null when the entity has none.</param>
public sealed record EntityUpdate(EntityId Entity, string State, string? Unit, DateTimeOffset LastUpdated, DateTimeOffset Reported)
{
    /// <summary>Schema bounds on an <c>options</c> attribute (a select's choices); a longer list or option is read as no options.</summary>
    public const int MaxOptions = 64;

    /// <inheritdoc cref="MaxOptions"/>
    public const int MaxOptionLength = 255;

    /// <summary>
    /// The <c>options</c> attribute (what a select offers), as Home Assistant reported it; null when
    /// the state carries no usable options list.
    /// </summary>
    public ImmutableArray<string>? Options { get; init; }

    /// <summary>
    /// The whitelisted attribute of a series signal, parsed and bounded by the transport; null when the entity has no series spec
    /// (and for a probe answer, which never carries one: a probe may confirm a series, never replace it).
    /// </summary>
    public ParsedSeries? Series { get; init; }

    /// <summary>
    /// The text of each whitelisted attribute of the timestamp signals of this entity (a sun time kept under <c>next_rising</c>), by attribute name, bounded by
    /// the transport: an empty text where the state carries no usable attribute (absent, not text, longer than <see cref="MaxAttributeText"/>); null when the
    /// entity has no such attribute declared (and for a probe answer, which never carries attributes).
    /// </summary>
    public ImmutableDictionary<string, string>? Attributes { get; init; }

    /// <summary>Schema bound on the text of a timestamp attribute; a longer value is read as no usable attribute.</summary>
    public const int MaxAttributeText = 64;

    /// <summary>An options list within the schema bounds, or null: never an error, only "not known".</summary>
    public static ImmutableArray<string>? BoundedOptions(IReadOnlyList<string?>? options) =>
        options is { Count: > 0 and <= MaxOptions } && options.All(o => o is { Length: > 0 and <= MaxOptionLength })
            ? [.. options!]
            : null;
}

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
public sealed record TrackedEntity(EntityId Entity, string? DeclaredUnit)
{
    /// <summary>The one attribute of this entity that is kept as a series (15-PLANNER, slice 2); null for every ordinary signal.</summary>
    public SeriesSpec? Series { get; init; }

    /// <summary>The one attribute of this entity whose text is the instant of a timestamp signal (a sun time); null for every other signal.</summary>
    public string? Attribute { get; init; }
}

/// <summary>How a series attribute is laid out; <see cref="Auto"/> takes a JSON list as a list and a JSON object as a mapping.</summary>
public enum SeriesShape
{
    Auto,
    List,
    Dict,
}

/// <summary>
/// Which attribute of an entity is kept as a series and how to read it: the per-signal whitelist (no other attribute is ever kept).
/// A list holds objects whose start, optional end and value sit under the named keys; a dict maps a time to a number.
/// </summary>
public sealed record SeriesSpec(string Attribute, SeriesShape Shape, string StartKey, string? EndKey, string ValueKey)
{
    public const string DefaultStartKey = "start";
    public const string DefaultEndKey = "end";
    public const string DefaultValueKey = "value";
}

public enum SeriesProblem
{
    None,

    /// <summary>The attribute is absent, empty, or neither a list nor a mapping.</summary>
    Missing,

    /// <summary>An item is malformed (no time, a time without a UTC offset when the add-on has no zone to read it in, a non-finite number, a duplicate time, an end before the start): the whole series is refused.</summary>
    Malformed,

    /// <summary>More than <see cref="Switchboard.Domain.Signals.TimeSeries.MaxPoints"/> points or <see cref="Switchboard.Domain.Signals.TimeSeries.MaxBytes"/> bytes: dropped whole, never truncated.</summary>
    TooLarge,
}

/// <summary>A series attribute as the transport read it: the raw numbers (the signal layer gives them their unit), time-sorted, or the reason there are none.</summary>
public sealed record ParsedSeries(ImmutableArray<Switchboard.Domain.Signals.SeriesPoint> Points, SeriesProblem Problem)
{
    public static ParsedSeries Of(SeriesProblem problem) => new([], problem);
}
