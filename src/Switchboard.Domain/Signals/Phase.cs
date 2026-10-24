namespace Switchboard.Domain.Signals;

/// <summary>A line of the grid connection. Energy cannot move between phases; every limit is per phase.</summary>
public enum Phase
{
    L1,
    L2,
    L3,
}
