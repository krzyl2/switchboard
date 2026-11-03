using System.Collections.Immutable;
using Switchboard.Domain.Balance;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Dispatch;

/// <summary>The allowance gate of the dispatch loop (BAL-02 … BAL-05, 13-HOURLY-BALANCE, generalised in 14-ECONOMICS): the hourly balance and the sources and price test, narrowing what the allocation lets a device take.</summary>
public sealed partial class DispatchEngine
{
    /// <summary>How a gated device is judged by the allowance gate.</summary>
    private enum GateKind
    {
        /// <summary>No sources declared (or no economics): exactly the 0.9 balance rule, and only while the energy counters are mapped.</summary>
        Balance,

        /// <summary>Sources declared: the allowance of 14-ECONOMICS (surplus class or the active zone).</summary>
        Sources,
    }

    /// <summary>
    /// The allowance gate on the devices that declare start: headroom (BAL-04, BAL-05, ECO-06, ECO-07). It only ever narrows what
    /// dispatch may do; the guard's envelope and the allocation stay in front of it.
    /// <list type="bullet">
    /// <item>A device that declares no <c>sources</c> is judged exactly as in 0.9: by the hourly balance, while the energy counters
    /// are mapped (a missing balance gives zero allowance), and not at all otherwise.</item>
    /// <item>A device that declares <c>sources</c> is allowed by the surplus class (the settlement model says energy is wanted, and
    /// the device's value covers the forgone export price) or by the active tariff zone (it is one of the device's sources, the
    /// value covers the import price, and for a start not too close to the zone's end). A running device is kept while either holds;
    /// a device on the zone class is not stopped by the balance, one on surplus is stopped by the settlement rule as in 0.9.</item>
    /// </list>
    /// Stops come first, lowest order key first: each running device whose allowance is gone is lowered one stage and the load comes
    /// down by it. Starts then walk the devices in start order, so what is left goes to the most valuable device that could take it:
    /// a device is a candidate only if the allocation shows it could rise now (guard permission and phase headroom), and the load of
    /// each start is added before the next device is judged, whichever class allowed it (one meter: a zone start lowers the surplus
    /// of the devices behind it). With the balance or the free power unknown the surplus class gives no allowance: nothing rises on
    /// it, and a device Switchboard itself started is lowered one stage; a zone class stays available where its zone is known.
    /// Returns whether any device's limits changed, so the allocation is redone.
    /// </summary>
    private bool ApplyAllowanceGate(List<Plan> plans, Dictionary<DeviceName, DeviceAllocation> result, BalanceView? view, ImmutableArray<DevicePhasePower> powers, DispatchSetup setup, DispatchInput input)
    {
        if (setup.Balance is not { } config)
        {
            return false;
        }

        var economics = _economicsTracker.Context;
        var gated = plans.Where(p => p.Mode == OverrideMode.Auto
            && p.Device.Spec.Start == StartPolicy.Headroom
            && !p.Excluded
            && input.SelfTest?.Device != p.Device.Id).ToList();

        // Who is judged how. A device without sources is the 0.9 balance gate and exists only while the balance view does.
        var kinds = new Dictionary<Plan, GateKind>();
        foreach (var plan in gated)
        {
            if (economics?.Device(plan.Device.Id)?.Spec.Sources is not null)
            {
                kinds[plan] = GateKind.Sources;
            }
            else if (view is not null)
            {
                kinds[plan] = GateKind.Balance;
            }
        }

        if (kinds.Count == 0)
        {
            return false;
        }

        var parameters = config.Parameters;
        var changed = false;
        var settled = new HashSet<Plan>();
        foreach (var plan in gated.Where(p => kinds.TryGetValue(p, out var kind) && kind == GateKind.Sources))
        {
            // Full mode only: a device the plan runs on a grid slot, or urgently, stays on that class (the price test and the sources are not asked again).
            plan.PlanKeeps = _planReader.GateFor(plan.Entry).Opens is not null;
        }

        foreach (var plan in gated.Where(p => kinds.TryGetValue(p, out var kind) && kind == GateKind.Balance))
        {
            plan.GrantReason = ReasonCode.BalanceSurplus;
        }

        if (view is { Known: false })
        {
            foreach (var plan in gated.Where(p => kinds.TryGetValue(p, out var kind) && kind == GateKind.Balance))
            {
                plan.BalanceAllows = false;
                plan.BalanceReason = ReasonCode.BalanceUnknown;
                changed |= Cap(plan, plan.BasisIndex);
                if (plan.BasisIndex > 0 && input.Ledger.Entry(plan.Device.Id)?.SelfStarted is not null)
                {
                    changed |= LowerOneStage(plan, ReasonCode.BalanceUnknown);
                }

                settled.Add(plan);
            }
        }

        // The load is what Switchboard would run: the measured draw of every device, except that a gated
        // device counts at its planned level, so a start decided but not yet drawing (a command in flight,
        // or shadow mode, where nothing is ever applied) is already in the projection and is not started twice.
        double? load = view is { Known: true } ? view.Load!.Value.Value : _balanceTracker.OwnLoad?.Value;
        if (load is { } baseLoad)
        {
            foreach (var plan in gated)
            {
                var measured = powers.Where(p => p.Device == plan.Device.Id).Sum(p => p.Power?.Value ?? 0);
                baseLoad += PlannedPower(plan, plan.BasisIndex, config) - measured;
            }

            load = baseLoad;
        }

        var surplus = Surplus(view);

        for (var i = gated.Count - 1; i >= 0; i--)
        {
            var plan = gated[i];
            if (plan.BasisIndex <= 0 || settled.Contains(plan) || !kinds.TryGetValue(plan, out var kind))
            {
                continue;
            }

            ReasonCode? stop = null;
            if (kind == GateKind.Balance)
            {
                if (HourlyBalanceRule.Decide(view!.BalanceWh, view.Free, new Watts(load!.Value), Watts.Zero, view.UntilNext, parameters) == BalanceWant.Stop)
                {
                    stop = ReasonCode.BalanceProjectionLow;
                }
            }
            else if (!plan.PlanKeeps)
            {
                stop = StopReason(economics!, economics!.Device(plan.Device.Id)!, surplus, load, plan, input)
                    ?? DeferStop(economics!, economics!.Device(plan.Device.Id)!, surplus, load, plan);
            }

            if (stop is not { } reason)
            {
                continue;
            }

            var shed = PlannedPower(plan, plan.BasisIndex, config) - PlannedPower(plan, plan.BasisIndex - 1, config);
            if (LowerOneStage(plan, reason))
            {
                load -= shed;
                changed = true;
            }
        }

        foreach (var plan in gated)
        {
            if (settled.Contains(plan) || !kinds.TryGetValue(plan, out var kind))
            {
                continue;
            }

            var wants = result[plan.Device.Id].Uncapped;
            if (plan.BalanceStop is not null || wants <= plan.BasisIndex || !plan.GuardAllows || !plan.DataValid)
            {
                continue;
            }

            var top = Math.Min(wants, plan.CapIndex);
            var index = plan.BasisIndex;
            if (kind == GateKind.Balance)
            {
                while (index < top)
                {
                    var candidate = PlannedPower(plan, index + 1, config) - PlannedPower(plan, index, config);
                    if (candidate <= 0
                        || HourlyBalanceRule.Decide(view!.BalanceWh, view.Free, new Watts(load!.Value), new Watts(candidate), view.UntilNext, parameters, Commitment(plan, index)) != BalanceWant.Start)
                    {
                        break;
                    }

                    load += candidate;
                    index++;
                }

                plan.BalanceAllows = index > plan.BasisIndex;
                plan.BalanceReason = ReasonCode.BalanceProjectionLow;
                if (plan.BalanceAllows && economics is not null)
                {
                    plan.EnergyClass = EnergySource.Surplus.Instance;
                }
            }
            else
            {
                index = GrantWithSources(economics!, economics!.Device(plan.Device.Id)!, plan, surplus, ref load, top, config, _planReader.GateFor(plan.Entry));
            }

            changed |= Cap(plan, index);
        }

        changed |= Displace(gated, kinds, settled, economics, surplus, load, config);

        // What each judged device runs on now, for the log: only the devices the loops above left as they are.
        foreach (var plan in gated.Where(p => kinds.TryGetValue(p, out var kind) && kind == GateKind.Sources && p.EnergyClass is null && p.BasisIndex > 0 && p.BalanceStop is null))
        {
            var device = economics!.Device(plan.Device.Id)!;
            plan.EnergyClass = plan.PlanKeeps || ZoneOf(economics, device).Ok(running: true) ? new EnergySource.Grid(null)
                : PvOf(economics, device, surplus, load, 0) is { Worth: true, Allowance.Decision: AllowanceDecision.Start or AllowanceDecision.Hold } ? EnergySource.Surplus.Instance
                : null;
        }

        return changed;
    }

    /// <summary>
    /// Displacement (14-ECONOMICS, "Displacement"): the most valuable gated device that the allocation lets rise but the projection refuses
    /// takes the place of a running device worth less, when it would start with that device one stage lower. The cheaper device (lowest
    /// value first) must be worth less by economics.min_margin and by economics.order_min_gap_fraction, must not be held by min_on (a
    /// displacement never stops a device early) and must not run on the zone class. It is lowered with value_rank_below_cutoff; the
    /// dearer one starts on a later step by the ordinary rule. One displacement per step, and only with an economics section (ECO-15).
    /// It lowers a load and starts nothing, so it cannot raise anything above what the guard allows.
    /// </summary>
    private static bool Displace(List<Plan> gated, Dictionary<Plan, GateKind> kinds, HashSet<Plan> settled, EconomicsContext? economics, SurplusView surplus, double? load, HourlyBalanceSetup config)
    {
        if (economics is null || load is not { } running || gated.Any(p => p.BalanceStop == ReasonCode.ValueRankBelowCutoff))
        {
            return false;
        }

        var parameters = economics.Setup.Parameters;
        foreach (var wanted in gated)
        {
            if (settled.Contains(wanted) || !kinds.ContainsKey(wanted) || wanted.BalanceAllows || wanted.BalanceReason != ReasonCode.BalanceProjectionLow
                || wanted.BalanceStop is not null || !wanted.GuardAllows || !wanted.DataValid || wanted.BasisIndex + 1 >= wanted.Levels.Length
                || economics.Device(wanted.Device.Id) is not { } high)
            {
                continue;
            }

            var candidate = PlannedPower(wanted, wanted.BasisIndex + 1, config) - PlannedPower(wanted, wanted.BasisIndex, config);
            if (candidate <= 0)
            {
                continue;
            }

            var commitment = Commitment(wanted, wanted.BasisIndex);
            foreach (var cheaper in gated
                .Where(p => p != wanted && p.BasisIndex > 0 && p.BalanceStop is null && !settled.Contains(p) && kinds.ContainsKey(p))
                .OrderBy(p => economics.Device(p.Device.Id)?.VEff.PerKwh ?? double.MaxValue))
            {
                if (economics.Device(cheaper.Device.Id) is not { } low
                    || high.VEff.PerKwh - low.VEff.PerKwh < parameters.MinMargin
                    || high.VEff.PerKwh <= low.VEff.PerKwh
                    || (high.VEff.PerKwh - low.VEff.PerKwh) / high.VEff.PerKwh < parameters.OrderMinGapFraction
                    || (low.Spec.Sources is not null && ZoneOf(economics, low).Ok(running: true)))
                {
                    continue;
                }

                var shed = PlannedPower(cheaper, cheaper.BasisIndex, config) - PlannedPower(cheaper, cheaper.BasisIndex - 1, config);
                if (shed <= 0 || Math.Max(cheaper.MinAllowed, cheaper.BasisIndex - 1) >= cheaper.BasisIndex)
                {
                    continue;
                }

                var pv = PvOf(economics, high, surplus, running - shed, candidate, commitment);
                if (pv.Allowance.Decision == AllowanceDecision.Start && (high.Spec.Sources is null || pv.Worth)
                    && LowerOneStage(cheaper, ReasonCode.ValueRankBelowCutoff))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Limits what a device may reach to a level index (never below what a lockout holds on); true when that lowered it.</summary>
    private static bool Cap(Plan plan, int index)
    {
        var capped = Math.Max(plan.MinAllowed, Math.Min(plan.MaxAllowed, index));
        if (capped >= plan.MaxAllowed)
        {
            return false;
        }

        plan.MaxAllowed = capped;
        return true;
    }

    /// <summary>The surplus of this step as the allowance gate judges it: the net billing window's free power, else the hourly balance's view.</summary>
    private SurplusView Surplus(BalanceView? view) =>
        _economicsTracker.Context is { Setup.Settlement.Id: SettlementModelId.NetBilling }
            ? new SurplusView(null, _balanceTracker.NetFree, TimeSpan.Zero)
            : new SurplusView(view?.BalanceWh, view?.Free, view?.UntilNext ?? TimeSpan.Zero);

    /// <summary>Asks a running device to come down one stage; false when a min_on lockout keeps it where it is.</summary>
    private static bool LowerOneStage(Plan plan, ReasonCode reason)
    {
        var lowered = Math.Max(plan.MinAllowed, plan.BasisIndex - 1);
        if (lowered >= plan.BasisIndex)
        {
            return false;
        }

        plan.MaxAllowed = Math.Min(plan.MaxAllowed, lowered);
        plan.BalanceStop = reason;
        return true;
    }

    /// <summary>
    /// How long the step from <paramref name="index"/> to the next level cannot be undone: the device's min_on when the step turns a
    /// relay stage on or takes a setpoint device out of off (a lockout holds it on for that long), nothing for a step that only changes
    /// what already runs. The rule charges what this commits beyond the :00 to the hour.
    /// </summary>
    private static TimeSpan Commitment(Plan plan, int index)
    {
        if (index + 1 >= plan.Levels.Length)
        {
            return TimeSpan.Zero;
        }

        var spec = plan.Device.Spec;
        var from = plan.Levels[index];
        var to = plan.Levels[index + 1];
        var turnsOn = spec is SetpointSpec ? DeviceLevels.IsOff(spec, from) && !DeviceLevels.IsOff(spec, to) : to.StagesOn > from.StagesOn;
        return turnsOn ? spec.MinOn : TimeSpan.Zero;
    }

    /// <summary>The power a level draws in W: the rated power of its relay stages, or a setpoint's ceiling at the nominal voltage.</summary>
    private static double PlannedPower(Plan plan, int index, HourlyBalanceSetup config)
    {
        var spec = plan.Device.Spec;
        var level = plan.Levels[index];
        return spec is SetpointSpec
            ? spec.Phases.Sum(p => DeviceLevels.Ceiling(spec, level, p).Value) * config.Voltage.Value
            : spec.Phases.Sum(p => plan.Device.RatedPower(level, p).Value);
    }
}
