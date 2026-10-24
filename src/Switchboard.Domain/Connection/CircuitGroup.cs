using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Domain.Connection;

/// <summary>
/// The logical name of a circuit group (<c>carport</c>), or of a rotation group of identical
/// elements. Like a device name it can never contain a '.', so it can never be a Home Assistant
/// entity ID (SIG-01).
/// </summary>
public readonly partial record struct GroupName : IComparable<GroupName>
{
    private GroupName(string value) => Value = value;

    public string Value { get; }

    public static bool TryParse(string? value, [NotNullWhen(true)] out GroupName? name)
    {
        name = value is not null && Pattern().IsMatch(value) ? new GroupName(value) : null;
        return name is not null;
    }

    public static GroupName Parse(string value) =>
        TryParse(value, out var name) ? name.Value : throw new FormatException($"'{value}' is not a valid group name.");

    public int CompareTo(GroupName other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;

    // The device-name pattern: the derived current signals (device__<id>_current_l<n>) stay valid signal names.
    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

/// <summary>
/// A downstream breaker shared by a set of devices, with its own per-phase limit and thermal budget
/// (CONN-03, 03 "Circuit groups"), enforced alongside the main limit through the same breaker model.
/// </summary>
/// <param name="ReserveCurrent">Kept free per phase for the group's uncontrolled loads, already in amperes.</param>
/// <param name="CurrentMaxAge">max_age of the sub-meter current per phase; for an estimated group, of the main phase current.</param>
/// <param name="SubMetered">True when a current signal measures the group; otherwise its current is the worst-case sum of its members.</param>
public sealed record CircuitGroupSpec(
    GroupName Id,
    BreakerProfile Breaker,
    ImmutableArray<Phase> Phases,
    ImmutableDictionary<Phase, Amperes> ReserveCurrent,
    ImmutableDictionary<Phase, TimeSpan> CurrentMaxAge,
    bool SubMetered,
    ImmutableArray<DeviceName> Members)
{
    /// <summary>In − reserve of the group breaker.</summary>
    public Amperes ReserveThreshold(Phase phase) => Breaker.Rating - ReserveCurrent.GetValueOrDefault(phase);
}
