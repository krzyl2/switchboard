using System.Collections.Immutable;
using System.Globalization;
using Switchboard.Domain.Units;

namespace Switchboard.Domain.Devices;

/// <summary>
/// The vocabulary of targets and reported states: a <see cref="RelayLevel"/> (one flag per relay
/// stage, stage number − 1) or a <see cref="SetpointLevel"/> (a setpoint in amperes with the enabled
/// flag of a stop actuator, true when there is none). Equality compares contents, so an unchanged
/// target is recognised as unchanged.
/// </summary>
public abstract record DeviceLevel
{
    private protected DeviceLevel()
    {
    }

    /// <summary>
    /// Numerical tolerance for comparing setpoints, not a control parameter: a setpoint read in mA
    /// and scaled to A may differ from the grid value in the last bits.
    /// </summary>
    public const double SetpointTolerance = 1e-9;

    /// <summary>A relay device's level: one flag per stage.</summary>
    public static DeviceLevel Relay(params bool[] stages) => new RelayLevel([.. stages]);

    /// <summary>A setpoint device's level.</summary>
    public static DeviceLevel At(Amperes setpoint, bool enabled = true) => new SetpointLevel(setpoint, enabled);

    /// <summary>The stop actuator's enabled flag; true for a relay level, which has none.</summary>
    public abstract bool Enabled { get; }

    /// <summary>The number of relay stages switched on; zero for a setpoint level.</summary>
    public abstract int StagesOn { get; }
}

/// <summary>A relay device's level: one flag per stage (stage number − 1).</summary>
public sealed record RelayLevel(ImmutableArray<bool> Relays) : DeviceLevel
{
    public override bool Enabled => true;

    public override int StagesOn => Relays.IsDefault ? 0 : Relays.Count(r => r);

    public bool Equals(RelayLevel? other) =>
        other is not null && (Relays.IsDefault ? [] : Relays).SequenceEqual(other.Relays.IsDefault ? [] : other.Relays);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var relay in Relays.IsDefault ? [] : Relays)
        {
            hash.Add(relay);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => string.Join("", (Relays.IsDefault ? [] : Relays).Select(r => r ? '1' : '0'));
}

/// <summary>A setpoint device's level: the setpoint in A, and whether its stop actuator lets it run.</summary>
public sealed record SetpointLevel : DeviceLevel
{
    public SetpointLevel(Amperes setpoint, bool enabled = true)
    {
        Setpoint = setpoint;
        Enabled = enabled;
    }

    public Amperes Setpoint { get; }

    public override bool Enabled { get; }

    public override int StagesOn => 0;

    public bool Equals(SetpointLevel? other) =>
        other is not null && Enabled == other.Enabled && Math.Abs(Setpoint.Value - other.Setpoint.Value) <= SetpointTolerance;

    // The setpoint is left out of the hash: equal levels may differ in its last bits.
    public override int GetHashCode() => Enabled.GetHashCode();

    public override string ToString() => Enabled ? Setpoint.Value.ToString("0.###", CultureInfo.InvariantCulture) + " A" : "disabled";
}
