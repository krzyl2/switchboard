using Switchboard.Domain.Units;

namespace Switchboard.Domain.Economics;

/// <summary>
/// The energy of one settlement period as savings accounting needs it (14-ECONOMICS, Savings), kWh, each not negative.
/// </summary>
/// <param name="Exported">Exported to the grid during the period (what the meter saw, step by step).</param>
/// <param name="Imported">Imported from the grid during the period.</param>
/// <param name="Controlled">The energy the controlled devices took, all of them.</param>
/// <param name="ControlledOffsettingImport">The part of <paramref name="Controlled"/> that ran in a step which imported from the grid:
/// with the loads off that step would have imported that much less. The rest would have been exported (net billing pays the two apart).</param>
public readonly record struct PeriodEnergy(KilowattHours Exported, KilowattHours Imported, KilowattHours Controlled, KilowattHours ControlledOffsettingImport);

/// <summary>
/// The device-off counterfactual of one settlement period (open question 4, decided): what the period's settlement lost by the
/// controlled loads running, <c>Settle(b + E) - Settle(b)</c>, with <c>b</c> the actual net export and <c>E</c> the controlled energy.
/// Pure: the prices are the period's time-weighted ones and are handed in; no clock, no entity, no currency label.
/// </summary>
public static class Counterfactual
{
    // Settle is a sum of products; what lies below this is floating-point noise around an exact zero, not a negative cost.
    private const double Noise = 1e-9;

    /// <summary>
    /// The extra settlement cost of the controlled energy, never negative for prices that are not negative (a kWh more exported earns,
    /// a kWh less imported saves); null when a price the flows need is missing (the period is then not counted).
    /// </summary>
    public static Money? ExtraCost(ISettlementModel model, PeriodEnergy energy, PriceBook prices)
    {
        ArgumentNullException.ThrowIfNull(model);
        var controlled = Math.Max(0, energy.Controlled.Value);
        var offset = Math.Min(Math.Max(0, energy.ControlledOffsettingImport.Value), Math.Min(controlled, energy.Imported.Value));
        var actual = new PeriodFlows(energy.Exported, energy.Imported);
        var without = new PeriodFlows(
            new KilowattHours(energy.Exported.Value + (controlled - offset)),
            new KilowattHours(energy.Imported.Value - offset));
        if (model.Settle(actual, prices) is not { } now || model.Settle(without, prices) is not { } off)
        {
            return null;
        }

        var extra = off.Amount - now.Amount;
        return new Money(extra is < 0 and > -Noise ? 0 : extra);
    }

    /// <summary>The share of a period's extra cost one device carries: proportional to its energy; zero when no controlled energy was taken.</summary>
    public static double Share(double extraCost, double deviceEnergy, double controlledEnergy) =>
        controlledEnergy > 0 ? extraCost * (deviceEnergy / controlledEnergy) : 0;
}
