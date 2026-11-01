using Switchboard.Domain.Economics;

namespace Switchboard.Tests.Support;

/// <summary>The two energy sources a dispatch decision or plan names, typed as <see cref="EnergySource"/> so tuples and asserts compare like with like.</summary>
internal static class TestEnergy
{
    public static EnergySource Pv { get; } = EnergySource.Surplus.Instance;

    /// <summary>Grid energy with no zone: what dispatch records (the zone is the economics block's <c>Zone</c>).</summary>
    public static EnergySource Zone { get; } = new EnergySource.Grid(null);

    public static EnergySource ZoneOf(string id) => new EnergySource.Grid(new ZoneId(id));
}
