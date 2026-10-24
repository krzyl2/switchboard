using System.Collections.Immutable;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Domain.Connection;

/// <summary>The grid connection as configured: topology, main breaker and the reserve kept per phase for uncontrolled loads (CONN-04).</summary>
public sealed record ConnectionProfile(
    Topology Topology,
    Amperes BreakerRating,
    BreakerCharacteristic Characteristic,
    Volts Voltage,
    ImmutableDictionary<Phase, Watts> Reserve)
{
    public ImmutableArray<Phase> Phases => TopologyPhases.For(Topology);
}
