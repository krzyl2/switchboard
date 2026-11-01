using System.Collections.Immutable;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Units;

namespace Switchboard.Tests.Support;

/// <summary>Reads of a level that assertions make without caring which kind it is: nothing for the kind it is not.</summary>
internal static class DeviceLevelExtensions
{
    /// <summary>The setpoint of a setpoint level; null for a relay level or no level.</summary>
    public static Amperes? Setpoint(this DeviceLevel? level) => (level as SetpointLevel)?.Setpoint;

    /// <summary>The flags of a relay level; the default array for a setpoint level or no level.</summary>
    public static ImmutableArray<bool> Relays(this DeviceLevel? level) => level is RelayLevel relay ? relay.Relays : default;
}
