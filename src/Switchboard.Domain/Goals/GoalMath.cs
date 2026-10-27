namespace Switchboard.Domain.Goals;

/// <summary>The remaining-need arithmetic of a goal (15-PLANNER, The goal model): pure, in kWh and percent.</summary>
public static class GoalMath
{
    /// <summary>Below this a goal counts as met: a float residue of the arithmetic is not an unmet need.</summary>
    public const double MetEpsilonKwh = 1e-9;

    /// <summary>
    /// The energy still to draw for an energy goal: <c>max(0, target - delivered) x (1 + loss_fraction)</c>. The loss
    /// fraction turns energy into the battery or the tank into energy drawn from the grid or the surplus.
    /// </summary>
    public static double RemainingEnergyKwh(double targetKwh, double deliveredKwh, double lossFraction) =>
        Math.Max(0, targetKwh - deliveredKwh) * (1 + lossFraction);

    /// <summary>The energy still to draw for a SoC goal: <c>(target - soc) / 100 x capacity_kwh x (1 + loss_fraction)</c>, never negative.</summary>
    public static double RemainingSocKwh(double targetPercent, double socPercent, double capacityKwh, double lossFraction) =>
        Math.Max(0, targetPercent - socPercent) / 100 * capacityKwh * (1 + lossFraction);

    public static bool IsMet(double remainingKwh) => remainingKwh <= MetEpsilonKwh;
}
