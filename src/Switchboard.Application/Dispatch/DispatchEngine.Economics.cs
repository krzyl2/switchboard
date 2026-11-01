using Switchboard.Domain.Balance;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Dispatch;

/// <summary>
/// The sources and price test half of the allowance gate (14-ECONOMICS, slice 3): what the surplus class and the active tariff
/// zone say for a device with <c>sources</c>, and why a start or an increase is withheld or a run is stopped. The prices and
/// values themselves are the <see cref="EconomicsTracker"/>'s. Nothing here can raise a load: the gate only narrows, and the guard
/// never sees any of it.
/// </summary>
public sealed partial class DispatchEngine
{
    /// <summary>
    /// Why a running device with sources should come down one stage, or null while it stays (14-ECONOMICS: a stop needs neither
    /// class to hold; the zone class is never stopped by the balance). The surplus class stops on the settlement rule as in 0.9;
    /// an unknown balance stops only a device Switchboard started; a value that no longer covers the forgone export stops it too.
    /// </summary>
    private static ReasonCode? StopReason(EconomicsContext economics, DeviceEcon device, SurplusView surplus, double? load, Plan plan, DispatchInput input)
    {
        if (ZoneOf(economics, device).Ok(running: true))
        {
            return null;
        }

        var pv = PvOf(economics, device, surplus, load, 0);
        return pv.Allowance.Decision switch
        {
            AllowanceDecision.Stop => PvReason(economics, AllowanceDecision.Stop),
            AllowanceDecision.Unknown => input.Ledger.Entry(plan.Device.Id)?.SelfStarted is not null ? ReasonCode.BalanceUnknown : null,
            _ => pv.Worth ? null : ReasonCode.ValueBelowCost,
        };
    }

    /// <summary>
    /// A plan entry of <c>defer</c> also brings a running device down when it runs on the active zone (a device whose sources do not open the zone now is left to <see cref="StopReason"/>) and the surplus does not hold it: the plan places its energy in a later slot, so buying
    /// it from the zone now is what the plan is there to avoid (15-PLANNER). The surplus class is not the plan's to refuse: a device the surplus rule still holds
    /// (settlement says start or hold and the value covers the forgone export) keeps running. It is the stop a zone's end makes, one stage a step
    /// through <see cref="LowerOneStage"/>, so min_on, persistence and the guard act as for any other stop; without an entry (mode off, no valid plan) nothing here runs.
    /// </summary>
    private ReasonCode? DeferStop(EconomicsContext economics, DeviceEcon device, SurplusView surplus, double? load, Plan plan)
    {
        if (!_planReader.GateFor(plan.Entry).Defers)
        {
            return null;
        }

        // Only a device that is running on the active zone: with the zone not open for it (a pv_only device, outside its zone, value below the
        // price) StopReason alone decides, so an unknown balance stays on the v2 side (it stops only a device Switchboard started).
        if (!ZoneOf(economics, device).Ok(running: true))
        {
            return null;
        }

        // The surplus holds the run (settlement says start or hold and the value covers the forgone export), and an unknown balance holds it too: a blip of the
        // balance input must not end a run the surplus rule would restart a minute later (v2 stops on an unknown balance only a device Switchboard started).
        if (PvOf(economics, device, surplus, load, 0) is { Allowance.Decision: AllowanceDecision.Unknown } or { Worth: true, Allowance.Decision: AllowanceDecision.Start or AllowanceDecision.Hold })
        {
            return null;
        }

        return ReasonCode.PlanDeferred;
    }

    /// <summary>
    /// The stages a device with sources may rise by, from its level, with the load of each granted stage added before the next
    /// is judged; sets the reason and the class the increase is made for, or why it is withheld.
    /// </summary>
    private static int GrantWithSources(EconomicsContext economics, DeviceEcon device, Plan plan, SurplusView surplus, ref double? load, int top, HourlyBalanceSetup config, PlanGate planGate)
    {
        var index = plan.BasisIndex;
        EnergySource? granted = null;
        ReasonCode? refusal = null;
        ReasonCode? opened = null;
        while (index < top)
        {
            var candidate = PlannedPower(plan, index + 1, config) - PlannedPower(plan, index, config);
            if (candidate <= 0)
            {
                break;
            }

            var pv = PvOf(economics, device, surplus, load, candidate, Commitment(plan, index));
            var zone = ZoneOf(economics, device);
            var pvOk = pv.Allowance.Decision == AllowanceDecision.Start && pv.Worth;
            var zoneOk = zone.Ok(running: index > 0);
            if (!pvOk && zoneOk && planGate.Defers)
            {
                // The plan places this device's energy in a later slot: the zone class is not used now (the surplus class is not the plan's to refuse).
                refusal = ReasonCode.PlanDeferred;
                break;
            }

            if (!pvOk && !zoneOk && planGate.Opens is { } opening)
            {
                // The class the plan adds, behind everything above (guard, envelope, persistence, lockouts): its slot, or an urgent goal. No price test, no zone-end margin.
                opened ??= opening;
                granted ??= new EnergySource.Grid(null);
                if (load is { } drawing)
                {
                    load = drawing + candidate;
                }

                index++;
                continue;
            }

            if (!pvOk && !zoneOk)
            {
                refusal = Refusal(economics, pv, PvOf(economics, device, surplus, load, 0).Allowance.Decision == AllowanceDecision.Start, zone);
                break;
            }

            granted ??= pvOk ? EnergySource.Surplus.Instance : new EnergySource.Grid(null);
            if (load is { } running)
            {
                load = running + candidate;
            }

            index++;
        }

        if (index == plan.BasisIndex && refusal is null)
        {
            // Nothing was judged (the cap leaves no stage): say why the next stage would not be given, as 0.9 says it with the balance.
            // When an energy class is open for it the cap (guard, envelope, rank) is the limit, not the sources: no energy refusal then.
            var next = plan.BasisIndex + 1 < plan.Levels.Length ? Math.Max(0, PlannedPower(plan, plan.BasisIndex + 1, config) - PlannedPower(plan, plan.BasisIndex, config)) : 0;
            var pv = PvOf(economics, device, surplus, load, next, Commitment(plan, plan.BasisIndex));
            var zone = ZoneOf(economics, device);
            var open = (pv.Allowance.Decision == AllowanceDecision.Start && pv.Worth) || zone.Ok(running: plan.BasisIndex > 0);
            if (!open)
            {
                refusal = Refusal(economics, pv, PvOf(economics, device, surplus, load, 0).Allowance.Decision == AllowanceDecision.Start, zone);
            }
        }

        plan.BalanceAllows = index > plan.BasisIndex;
        plan.BalanceReason = refusal;
        plan.EnergyClass = granted;
        plan.GrantReason = opened is { } byPlan && index > plan.BasisIndex ? byPlan : granted switch
        {
            EnergySource.Surplus => economics.Setup.Settlement.Id == SettlementModelId.NetBilling ? ReasonCode.SurplusAvailable : ReasonCode.BalanceSurplus,
            EnergySource.Grid => ReasonCode.ZoneAllowed,
            _ => null,
        };
        return index;
    }

    /// <summary>What the surplus class says for a candidate: the settlement model's allowance, and whether the value covers the forgone export (the test is off while no export price exists).</summary>
    private readonly record struct PvState(Allowance Allowance, bool Worth);

    private static PvState PvOf(EconomicsContext economics, DeviceEcon device, SurplusView surplus, double? load, double candidate, TimeSpan commitment = default)
    {
        var allowance = load is { } running
            ? economics.Setup.Settlement.Decide(surplus, new Watts(running), new Watts(candidate), commitment)
            : new Allowance(AllowanceDecision.Unknown, AllowanceReason.InputUnknown);
        var cost = EnergyCost.Pv(economics.Setup.Settlement, economics.Now, economics.Prices);
        return new PvState(allowance, cost is not { } price || ValueTest.Worth(device.VEff, price, economics.Setup.Parameters.MinMargin));
    }

    /// <summary>What the active tariff zone says for a device: whether it is one of its sources, whether the value covers the import price, and whether a start still has the zone-end margin.</summary>
    private readonly record struct ZoneState(bool Allowed, bool Worth, bool MarginOk)
    {
        public bool Ok(bool running) => Allowed && Worth && (running || MarginOk);
    }

    private static ZoneState ZoneOf(EconomicsContext economics, DeviceEcon device)
    {
        if (ZoneId.Of(economics.Zone) is not { } zone || device.Spec.Sources is not { } sources || !sources.AllowsZone(zone))
        {
            return default;
        }

        var worth = EnergyCost.ZoneEnergy(economics.Prices) is { } cost && ValueTest.Worth(device.VEff, cost, economics.Setup.Parameters.MinMargin);
        var margin = economics.NextChange is not { } next || next.At - economics.Now >= economics.Setup.Parameters.ZoneEndMargin;
        return new ZoneState(true, worth, margin);
    }

    /// <summary>
    /// Why a start or increase is withheld: the first energy class in the device's sources that is open now says (surplus, then the
    /// active zone); the surplus is open when its inputs are unknown (the reason is then that nothing can be told), when the
    /// settlement says energy is wanted but the value does not cover the price, or when there is surplus that the candidate does
    /// not fit in, or while the settlement says Hold (the projection sits inside the band, as in 0.9). With no such class open the reason is source_not_allowed: what is available now is not among the sources.
    /// </summary>
    private static ReasonCode Refusal(EconomicsContext economics, PvState pv, bool surplusPresent, ZoneState zone)
    {
        if (pv.Allowance.Decision == AllowanceDecision.Unknown)
        {
            return ReasonCode.BalanceUnknown;
        }

        if (pv.Allowance.Decision == AllowanceDecision.Start)
        {
            return ReasonCode.ValueBelowCost;
        }

        if (surplusPresent || pv.Allowance.Decision == AllowanceDecision.Hold)
        {
            return economics.Setup.Settlement.Id == SettlementModelId.NetBilling ? ReasonCode.SurplusLow : ReasonCode.BalanceProjectionLow;
        }

        return zone is { Allowed: true, Worth: false } ? ReasonCode.ValueBelowCost : ReasonCode.SourceNotAllowed;
    }

    /// <summary>The reason code of a settlement decision: the hourly balance's projection or net billing's surplus.</summary>
    private static ReasonCode PvReason(EconomicsContext economics, AllowanceDecision decision) =>
        decision switch
        {
            AllowanceDecision.Unknown => ReasonCode.BalanceUnknown,
            AllowanceDecision.Start => economics.Setup.Settlement.Id == SettlementModelId.NetBilling ? ReasonCode.SurplusAvailable : ReasonCode.BalanceSurplus,
            _ => economics.Setup.Settlement.Id == SettlementModelId.NetBilling ? ReasonCode.SurplusLow : ReasonCode.BalanceProjectionLow,
        };
}
