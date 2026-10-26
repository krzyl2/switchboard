using System.Collections.Immutable;

namespace Switchboard.Domain.Economics;

/// <summary>
/// Which energy a device may take (<c>sources</c>): surplus, and the named tariff zones, or every zone. A device that
/// declares none is unspecified (0.9 behaviour) and has no SourceSet at all.
/// </summary>
/// <param name="Pv">Surplus (energy that would otherwise be exported) is allowed.</param>
/// <param name="AllZones">Every zone is allowed (<c>any</c>).</param>
/// <param name="Zones">The named zone ids, ordinal order, no duplicates; empty when <paramref name="AllZones"/>.</param>
public sealed record SourceSet(bool Pv, bool AllZones, ImmutableArray<string> Zones)
{
    public const string PvOnlyText = "pv_only";
    public const string AnyText = "any";
    public const string ZonePrefix = "pv_and_zone:";

    public static SourceSet PvOnly { get; } = new(true, false, []);

    public static SourceSet Any { get; } = new(true, true, []);

    public bool AllowsZone(string zone) => AllZones || Zones.Contains(zone);

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

        result = new SourceSet(true, false, [.. ids.Order(StringComparer.Ordinal)]);
        return true;
    }

    /// <summary>The set restricted to the zones that exist; the dropped ids are returned. Narrowing is the only direction a configuration problem may move a device.</summary>
    public SourceSet Narrow(IReadOnlySet<string> knownZones, out ImmutableArray<string> dropped)
    {
        ArgumentNullException.ThrowIfNull(knownZones);
        var missing = Zones.Where(z => !knownZones.Contains(z)).ToImmutableArray();
        dropped = missing;
        return missing.IsEmpty ? this : this with { Zones = [.. Zones.Where(knownZones.Contains)] };
    }

    public string ToText() =>
        AllZones ? AnyText : Zones.IsEmpty ? PvOnlyText : ZonePrefix + string.Join(',', Zones);

    public bool Equals(SourceSet? other) =>
        other is not null && Pv == other.Pv && AllZones == other.AllZones && Zones.SequenceEqual(other.Zones, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Pv, AllZones, string.Join(',', Zones));
}
