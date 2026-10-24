using System.Collections.Immutable;
using Switchboard.Domain.Signals;

namespace Switchboard.Domain.Connection;

/// <summary>How the grid connection is metered and limited (CONN-01).</summary>
public enum Topology
{
    /// <summary>Three phases, an independent limit per phase, a current measured on every phase.</summary>
    ThreePhase,

    /// <summary>Three phases limited through a summed measurement only: the degraded mode of D2.</summary>
    ThreePhaseSummed,

    SinglePhase,
}

public enum BreakerCharacteristic
{
    B,
    C,
    Unknown,
}

public static class TopologyPhases
{
    private static readonly ImmutableArray<Phase> Three = [Phase.L1, Phase.L2, Phase.L3];
    private static readonly ImmutableArray<Phase> One = [Phase.L1];

    public static ImmutableArray<Phase> For(Topology topology) => topology == Topology.SinglePhase ? One : Three;
}
