using System.Collections.Immutable;
using System.Globalization;
using Switchboard.Domain.Units;

namespace Switchboard.Domain.Devices;

/// <summary>
/// The vocabulary of targets and reported states: one flag per relay stage (stage number − 1),
/// or a setpoint in amperes with the enabled flag of a stop actuator (true when there is none).
/// Equality compares contents, so an unchanged target is recognised as unchanged.
/// </summary>
public sealed record DeviceLevel(ImmutableArray<bool> Relays, Amperes? Setpoint, bool Enabled)
{
    /// <summary>A relay device's level: one flag per stage.</summary>
    public static DeviceLevel Relay(params bool[] stages) => new([.. stages], null, true);

    /// <summary>A setpoint device's level.</summary>
    public static DeviceLevel At(Amperes setpoint, bool enabled = true) => new([], setpoint, enabled);

    /// <summary>The number of relay stages switched on.</summary>
    public int StagesOn => Relays.IsDefault ? 0 : Relays.Count(r => r);

    /// <summary>
    /// Numerical tolerance for comparing setpoints, not a control parameter: a setpoint read in mA
    /// and scaled to A may differ from the grid value in the last bits.
    /// </summary>
    public const double SetpointTolerance = 1e-9;

    public bool Equals(DeviceLevel? other) =>
        other is not null
        && Enabled == other.Enabled
        && SameSetpoint(Setpoint, other.Setpoint)
        && (Relays.IsDefault ? [] : Relays).SequenceEqual(other.Relays.IsDefault ? [] : other.Relays);

    // The setpoint is left out of the hash: equal levels may differ in its last bits.
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Enabled);
        hash.Add(Setpoint is null);
        foreach (var relay in Relays.IsDefault ? [] : Relays)
        {
            hash.Add(relay);
        }

        return hash.ToHashCode();
    }

    private static bool SameSetpoint(Amperes? a, Amperes? b) =>
        a is { } x && b is { } y ? Math.Abs(x.Value - y.Value) <= SetpointTolerance : a is null && b is null;

    public override string ToString() => Setpoint is { } setpoint
        ? (Enabled ? setpoint.Value.ToString("0.###", CultureInfo.InvariantCulture) + " A" : "disabled")
        : string.Join("", Relays.Select(r => r ? '1' : '0'));
}
