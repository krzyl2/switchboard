using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Switchboard.Domain.Devices;

/// <summary>
/// The logical name of a declared device (<c>heater_l1</c>). Everything above the configuration
/// layer knows a device only by this name. It can never contain a '.', so it can never be a Home
/// Assistant entity ID (SIG-01).
/// </summary>
public readonly partial record struct DeviceName : IComparable<DeviceName>
{
    private DeviceName(string value) => Value = value;

    public string Value { get; }

    public static bool TryParse(string? value, [NotNullWhen(true)] out DeviceName? name)
    {
        name = value is not null && Pattern().IsMatch(value) ? new DeviceName(value) : null;
        return name is not null;
    }

    public static DeviceName Parse(string value) =>
        TryParse(value, out var name) ? name.Value : throw new FormatException($"'{value}' is not a valid device name.");

    public int CompareTo(DeviceName other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;

    // At most 40 characters, so the derived state signal name (device_<id>_stage<n>) stays a valid signal name.
    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
