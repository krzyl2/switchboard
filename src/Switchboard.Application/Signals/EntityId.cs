using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Switchboard.Application.Signals;

/// <summary>
/// A Home Assistant entity ID (<c>sensor.meter_l1_current</c>). It exists only in the signal
/// mapping layer and the configuration; Domain and every consumer work with logical signal names
/// (SIG-01).
/// </summary>
public readonly partial record struct EntityId : IComparable<EntityId>
{
    private EntityId(string value) => Value = value;

    public string Value { get; }

    public static bool TryParse(string? value, [NotNullWhen(true)] out EntityId? entityId)
    {
        entityId = value is not null && Pattern().IsMatch(value) ? new EntityId(value) : null;
        return entityId is not null;
    }

    public static EntityId Parse(string value) =>
        TryParse(value, out var entityId) ? entityId.Value : throw new FormatException($"'{value}' is not a valid entity ID.");

    public int CompareTo(EntityId other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;

    // Home Assistant's own VALID_ENTITY_ID: no "__", no leading or trailing "_" on either side of the
    // dot. Anything looser passes here and is refused by HA for the whole subscription.
    [GeneratedRegex("^(?!.+__)(?!_)[a-z0-9_]+(?<!_)\\.(?!_)[a-z0-9_]+(?<!_)$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
