using System.Collections.Immutable;

namespace Switchboard.Domain.Economics;

/// <summary>
/// Which energy a device may take (<c>sources</c>): surplus always, and either the named tariff zones or every zone. Built through
/// <see cref="PvOnly"/>, <see cref="Any"/>, <see cref="PvAnd"/> or <see cref="TryParse"/>, so a set always holds what the grammar allows. A device that
/// declares none is unspecified (0.9 behaviour) and has no SourceSet at all.
/// </summary>
public sealed record SourceSet
{
    public const string PvOnlyText = "pv_only";
    public const string AnyText = "any";
    public const string ZonePrefix = "pv_and_zone:";

    private SourceSet(bool allZones, ImmutableArray<ZoneId> zones)
    {
        AllZones = allZones;
        Zones = zones;
    }

    public static SourceSet PvOnly { get; } = new(false, []);

    public static SourceSet Any { get; } = new(true, []);

    /// <summary>Every zone is allowed (<c>any</c>).</summary>
    public bool AllZones { get; }

    /// <summary>The named zone ids, ordinal order, no duplicates; empty when <see cref="AllZones"/> or for surplus only.</summary>
    public ImmutableArray<ZoneId> Zones { get; }

    /// <summary>Surplus and the named zones; no zone at all is <see cref="PvOnly"/>. A zone named twice is one zone.</summary>
    public static SourceSet PvAnd(IEnumerable<ZoneId> zones)
    {
        ArgumentNullException.ThrowIfNull(zones);
        var ordered = zones.Distinct().Order().ToImmutableArray();
        return ordered.IsEmpty ? PvOnly : new SourceSet(false, ordered);
    }

    public bool AllowsZone(ZoneId zone) => AllZones || Zones.Contains(zone);

    /// <summary>The grammar: <c>pv_only</c>, <c>any</c> or <c>pv_and_zone:&lt;id&gt;[,&lt;id&gt;]</c>. Whether a zone exists is for <see cref="Narrow"/>.</summary>
    public static bool TryParse(string text, out SourceSet? result, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(text);
        result = null;
        problem = null;
        var value = text.Trim();
        if (value == PvOnlyText)
        {
            result = PvOnly;
            return true;
        }

        if (value == AnyText)
        {
            result = Any;
            return true;
        }

        if (!value.StartsWith(ZonePrefix, StringComparison.Ordinal))
        {
            problem = $"'{text}' is not one of: {PvOnlyText}, {ZonePrefix}<id>[,<id>], {AnyText}.";
            return false;
        }

        var ids = value[ZonePrefix.Length..].Split(',').Select(s => s.Trim()).ToArray();
        if (ids.Any(id => id.Length == 0 || id.Any(char.IsWhiteSpace)))
        {
            problem = $"'{text}' names an empty or malformed zone id; write {ZonePrefix}<id>[,<id>] with ids separated by commas.";
            return false;
        }

        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
        {
            problem = $"'{text}' names a zone twice.";
            return false;
        }

        result = PvAnd(ids.Select(id => new ZoneId(id)));
        return true;
    }

    /// <summary>The set restricted to the zones that exist; the dropped ids are returned. Narrowing is the only direction a configuration problem may move a device.</summary>
    public SourceSet Narrow(IReadOnlySet<ZoneId> knownZones, out ImmutableArray<ZoneId> dropped)
    {
        ArgumentNullException.ThrowIfNull(knownZones);
        var missing = Zones.Where(z => !knownZones.Contains(z)).ToImmutableArray();
        dropped = missing;
        return missing.IsEmpty ? this : PvAnd(Zones.Where(knownZones.Contains));
    }

    public string ToText() =>
        AllZones ? AnyText : Zones.IsEmpty ? PvOnlyText : ZonePrefix + string.Join(',', Zones.Select(z => z.Value));

    public bool Equals(SourceSet? other) =>
        other is not null && AllZones == other.AllZones && Zones.SequenceEqual(other.Zones);

    public override int GetHashCode() => HashCode.Combine(AllZones, string.Join(',', Zones.Select(z => z.Value)));
}
