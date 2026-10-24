using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Switchboard.Domain.Signals;

/// <summary>
/// The logical name of a signal (<c>grid_current_l1</c>). Everything above the signal mapping
/// layer works with these names, never with Home Assistant entity IDs (SIG-01).
/// </summary>
public readonly partial record struct SignalName : IComparable<SignalName>
{
    private SignalName(string value) => Value = value;

    public string Value { get; }

    public static bool TryParse(string? value, [NotNullWhen(true)] out SignalName? name)
    {
        name = value is not null && Pattern().IsMatch(value) ? new SignalName(value) : null;
        return name is not null;
    }

    public static SignalName Parse(string value) =>
        TryParse(value, out var name) ? name.Value : throw new FormatException($"'{value}' is not a valid signal name.");

    public int CompareTo(SignalName other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
