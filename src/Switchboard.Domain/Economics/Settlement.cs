using Switchboard.Domain.Balance;
using Switchboard.Domain.Units;

namespace Switchboard.Domain.Economics;

/// <summary>The netting period of a model: the interval inside which import and export cancel.</summary>
public enum NettingPeriod
{
    /// <summary>The local hour (hourly netting).</summary>
    Hour,

    /// <summary>One step, a statistics bucket (net billing, feed-in tariff).</summary>
    Step,
}

/// <summary>What a settlement model says about the energy a candidate load would take.</summary>
public enum AllowanceDecision
{
    /// <summary>Not known: no allowance. Never a start, never a stop.</summary>
    Unknown,

    /// <summary>Energy is wanted: start or increase.</summary>
    Start,

    /// <summary>Keep what runs.</summary>
    Hold,

    /// <summary>Energy is not wanted: the load should come down.</summary>
    Stop,
}

/// <summary>Why a model decided as it did; dispatch maps these onto the closed reason code list (balance_*, surplus_*), nothing here is a code of it.</summary>
public enum AllowanceReason
{
    /// <summary>The balance or the free power is not known.</summary>
    InputUnknown,

    /// <summary>Hourly netting: the projection is above the band.</summary>
    BalanceSurplus,

    /// <summary>Hourly netting: the projection is below the floor.</summary>
    BalanceProjectionLow,

    /// <summary>Net billing: the instantaneous surplus covers the candidate and the start hysteresis.</summary>
    SurplusAvailable,

    /// <summary>Net billing: the instantaneous surplus is below the stop hysteresis.</summary>
    SurplusLow,

    /// <summary>Neither more nor less energy is wanted.</summary>
    WithinBand,
}

public readonly record struct Allowance(AllowanceDecision Decision, AllowanceReason Reason);

/// <summary>
/// What a model needs to judge surplus, in W and Wh (the signal layer already converted): the running hour's balance, the
/// smoothed free power and the time to the next :00. Null where not known.
/// </summary>
public readonly record struct SurplusView(double? BalanceWh, Watts? Free, TimeSpan UntilNextHour);

/// <summary>
/// How a period is settled (D6): whether surplus energy is wanted, what a kWh not exported earns, and the money of a period.
/// Pure: no clock, no entity. Dispatch rules never depend on <see cref="Settle"/>; it serves savings and the settlement estimate.
/// </summary>
public interface ISettlementModel
{
    SettlementModelId Id { get; }

    NettingPeriod NettingPeriod { get; }

    /// <param name="surplus">The balance, free power and time to the :00.</param>
    /// <param name="load">Power of the loads Switchboard runs now.</param>
    /// <param name="candidate">Power of the next device or stage in merit order.</param>
    Allowance Decide(SurplusView surplus, Watts load, Watts candidate);

    /// <summary>What a kWh not exported earns at <paramref name="t"/>; null only when no export price exists.</summary>
    PricePoint? ExportValue(DateTimeOffset t, PriceBook prices);

    /// <summary>Money of one period at its (time-weighted) prices; positive = receivable. Null when a price the flows need is missing.</summary>
    Money? Settle(PeriodFlows flows, PriceBook prices);
}

/// <summary>Hourly netting (BAL): the allowance is <see cref="HourlyBalanceRule"/> unchanged; the hour's net export is paid at the export price, a net import costs the import price.</summary>
public sealed class HourlyNettingModel(HourlyBalanceParameters parameters) : ISettlementModel
{
    public SettlementModelId Id => SettlementModelId.HourlyNetting;

    public NettingPeriod NettingPeriod => NettingPeriod.Hour;

    public Allowance Decide(SurplusView surplus, Watts load, Watts candidate) =>
        HourlyBalanceRule.Decide(surplus.BalanceWh, surplus.Free, load, candidate, surplus.UntilNextHour, parameters) switch
        {
            BalanceWant.Start => new Allowance(AllowanceDecision.Start, AllowanceReason.BalanceSurplus),
            BalanceWant.Stop => new Allowance(AllowanceDecision.Stop, AllowanceReason.BalanceProjectionLow),
            BalanceWant.Hold => new Allowance(AllowanceDecision.Hold, AllowanceReason.WithinBand),
            _ => new Allowance(AllowanceDecision.Unknown, AllowanceReason.InputUnknown),
        };

    public PricePoint? ExportValue(DateTimeOffset t, PriceBook prices) => prices.Export;

    public Money? Settle(PeriodFlows flows, PriceBook prices)
    {
        var net = flows.NetExport.Value;
        if (net == 0)
        {
            return Money.Zero;
        }

        return (net > 0 ? prices.Export : prices.Import) is { } price ? new Money(net * price.PerKwh) : null;
    }
}

/// <summary>
/// Net billing (and, with a flat export price, a feed-in tariff): no hour to project, the instantaneous surplus
/// <c>free - load</c> decides. A start needs the candidate plus the start hysteresis, a stop happens below the stop hysteresis
/// (negative: a little import is tolerated). Exports and imports are paid apart.
/// </summary>
/// <param name="StartHysteresis">dispatch.start_hysteresis_w.</param>
/// <param name="StopHysteresis">dispatch.stop_hysteresis_w (negative).</param>
/// <param name="Window">economics.net_billing_window: the smoothing the supplier of the free power applies; the model only carries it.</param>
public sealed record NetBillingModel(Watts StartHysteresis, Watts StopHysteresis, TimeSpan Window) : ISettlementModel
{
    public SettlementModelId Id => SettlementModelId.NetBilling;

    public NettingPeriod NettingPeriod => NettingPeriod.Step;

    public Allowance Decide(SurplusView surplus, Watts load, Watts candidate)
    {
        if (surplus.Free is not { } free || !double.IsFinite(free.Value))
        {
            return new Allowance(AllowanceDecision.Unknown, AllowanceReason.InputUnknown);
        }

        var surplusPower = free - load;
        if (surplusPower >= candidate + StartHysteresis)
        {
            return new Allowance(AllowanceDecision.Start, AllowanceReason.SurplusAvailable);
        }

        return surplusPower < StopHysteresis
            ? new Allowance(AllowanceDecision.Stop, AllowanceReason.SurplusLow)
            : new Allowance(AllowanceDecision.Hold, AllowanceReason.WithinBand);
    }

    public PricePoint? ExportValue(DateTimeOffset t, PriceBook prices) => prices.Export;

    public Money? Settle(PeriodFlows flows, PriceBook prices)
    {
        var income = 0.0;
        if (flows.Exported.Value != 0)
        {
            if (prices.Export is not { } export)
            {
                return null;
            }

            income = flows.Exported.Value * export.PerKwh;
        }

        var cost = 0.0;
        if (flows.Imported.Value != 0)
        {
            if (prices.Import is not { } import)
            {
                return null;
            }

            cost = flows.Imported.Value * import.PerKwh;
        }

        return new Money(income - cost);
    }
}
