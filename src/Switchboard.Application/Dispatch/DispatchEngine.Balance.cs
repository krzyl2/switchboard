using System.Collections.Immutable;
using Switchboard.Domain.Balance;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Dispatch;

/// <summary>The hourly balance as a gate of the dispatch loop (BAL-02 … BAL-05, 13-HOURLY-BALANCE), generalised to the allowance gate of 14-ECONOMICS.</summary>
public sealed partial class DispatchEngine
{
    private readonly FreePowerWindow _free = new();
    private readonly FreePowerWindow _freeNet = new();
    private BalanceView? _balance;
    private ImmutableArray<DevicePhasePower> _devicePowers = [];
    private Watts? _ownLoad;
    private Watts? _netFree;

    /// <summary>
    /// What Switchboard's own loads and the grid add up to in this step: the instantaneous free power (grid net export plus
    /// every own load added back, so the projection does not shrink by what Switchboard itself switched on) and the own load
    /// (what the controlled devices draw). Either is null while any phase's power or any own load is not known.
    /// </summary>
    private static (Watts? Export, Watts? Load) OwnFlows(DispatchInput input, DispatchSetup setup, Dictionary<Phase, Watts?> ours)
    {
        Watts? export = Watts.Zero;
        Watts? load = Watts.Zero;
        foreach (var phase in setup.Phases)
        {
            var own = ours.GetValueOrDefault(phase);
            load = load is { } running && own is { } drawn ? running + drawn : null;
            export = export is { } sum && own is { } added && input.Inputs.For(phase) is { DataValid: true, NetImport: { } netImport } ? sum + added - netImport : null;
        }

        return (export, load);
    }

    /// <summary>
    /// The hourly balance as this step reads it (BAL-02): free power smoothed over the window, and the balance of the running
    /// hour, valid only for the hour its time to the :00 is taken from and only while the monitor's reading is no older than
    /// max_age. Null while no energy counters are mapped: the balance is off until configured and nothing is gated by it.
    /// </summary>
    private BalanceView? ObserveBalance(DispatchInput input, DispatchSetup setup, Watts? export, Watts? load, DateTimeOffset now)
    {
        if (setup.Balance is not { } config || input.Inputs.Energy is null)
        {
            _free.Reset();
            return null;
        }

        var parameters = config.Parameters;
        var free = _free.Observe(now, export, parameters);
        double? balance = null;
        var untilNext = TimeSpan.Zero;
        BalanceBand? band = null;
        if (input.Balance is { } status && status.Reading.Wh is { } wh && now >= status.At && now - status.At <= parameters.MaxAge)
        {
            var remaining = status.Reading.HourStart.AddHours(1) - now;
            if (remaining > TimeSpan.Zero && remaining <= TimeSpan.FromHours(1))
            {
                balance = wh;
                untilNext = remaining;
                band = HourlyBalanceRule.Band(remaining, parameters);
            }
        }

        double? projection = balance is { } known && free is { } freePower && load is { } drawing
            ? HourlyBalanceRule.Project(known, freePower, drawing, untilNext, parameters)
            : null;
        return new BalanceView(balance, free, load, untilNext, band, projection);
    }

    /// <summary>
    /// Free power under net billing (14-ECONOMICS): there is no hour to project, the instantaneous surplus decides, smoothed over
    /// economics.net_billing_window instead of the balance's window. It needs no energy counters. Null under hourly netting
    /// (the balance's own free power is used) and while the estimate is not known.
    /// </summary>
    private Watts? ObserveNetFree(DispatchSetup setup, Watts? export, DateTimeOffset now)
    {
        if (setup.Economics is not { Settlement.Id: SettlementModelId.NetBilling } economics || setup.Balance is not { } config)
        {
            _freeNet.Reset();
            return null;
        }

        return _freeNet.Observe(now, export, config.Parameters with { SmoothingWindow = economics.Parameters.NetBillingWindow });
    }

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

        var economics = _economics;
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
        double? load = view is { Known: true } ? view.Load!.Value.Value : _ownLoad?.Value;
        if (load is { } baseLoad)
        {
            foreach (var plan in gated)
            {
                var measured = powers.Where(p => p.Device == plan.Device.Id).Sum(p => p.Power?.Value ?? 0);
                baseLoad += PlannedPower(plan, plan.BasisIndex, config) - measured;
            }

            load = baseLoad;
        }

        var surplus = economics is { Setup.Settlement.Id: SettlementModelId.NetBilling }
            ? new SurplusView(null, _netFree, TimeSpan.Zero)
            : new SurplusView(view?.BalanceWh, view?.Free, view?.UntilNext ?? TimeSpan.Zero);

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
            else
            {
                stop = StopReason(economics!, economics!.Device(plan.Device.Id)!, surplus, load, plan, input);
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
                    plan.EnergyClass = EnergyClass.Pv;
                }
            }
            else
            {
                index = GrantWithSources(economics!, economics!.Device(plan.Device.Id)!, plan, surplus, ref load, top, config);
            }

            changed |= Cap(plan, index);
        }

        changed |= Displace(gated, kinds, settled, economics, surplus, load, config);

        // What each judged device runs on now, for the log: only the devices the loops above left as they are.
        foreach (var plan in gated.Where(p => kinds.TryGetValue(p, out var kind) && kind == GateKind.Sources && p.EnergyClass is null && p.BasisIndex > 0 && p.BalanceStop is null))
        {
            var device = economics!.Device(plan.Device.Id)!;
            plan.EnergyClass = ZoneOf(economics, device).Ok(running: true) ? EnergyClass.Zone
                : PvOf(economics, device, surplus, load, 0) is { Worth: true, Allowance.Decision: AllowanceDecision.Start or AllowanceDecision.Hold } ? EnergyClass.Pv
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
                if (pv.Allowance.Decision == AllowanceDecision.Start && (high.Spec.Sources is null || (high.Spec.Sources.Pv && pv.Worth))
                    && LowerOneStage(cheaper, ReasonCode.ValueRankBelowCutoff))
                {
                    return true;
                }
            }
        }

        return false;
    }

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
    /// The stages a device with sources may rise by, from its level, with the load of each granted stage added before the next
    /// is judged; sets the reason and the class the increase is made for, or why it is withheld.
    /// </summary>
    private static int GrantWithSources(EconomicsContext economics, DeviceEcon device, Plan plan, SurplusView surplus, ref double? load, int top, HourlyBalanceSetup config)
    {
        var index = plan.BasisIndex;
        EnergyClass? granted = null;
        ReasonCode? refusal = null;
        while (index < top)
        {
            var candidate = PlannedPower(plan, index + 1, config) - PlannedPower(plan, index, config);
            if (candidate <= 0)
            {
                break;
            }

            var pv = PvOf(economics, device, surplus, load, candidate, Commitment(plan, index));
            var zone = ZoneOf(economics, device);
            var pvOk = device.Spec.Sources!.Pv && pv.Allowance.Decision == AllowanceDecision.Start && pv.Worth;
            if (!pvOk && !zone.Ok(running: index > 0))
            {
                refusal = Refusal(economics, device, pv, PvOf(economics, device, surplus, load, 0).Allowance.Decision == AllowanceDecision.Start, zone);
                break;
            }

            granted ??= pvOk ? EnergyClass.Pv : EnergyClass.Zone;
            if (load is { } running)
            {
                load = running + candidate;
            }

            index++;
        }

        if (index == plan.BasisIndex && refusal is null)
        {
            // Nothing was judged (the cap leaves no stage): say why the next stage would not be given, as 0.9 says it with the balance.
            var next = plan.BasisIndex + 1 < plan.Levels.Length ? Math.Max(0, PlannedPower(plan, plan.BasisIndex + 1, config) - PlannedPower(plan, plan.BasisIndex, config)) : 0;
            var pv = PvOf(economics, device, surplus, load, next, Commitment(plan, plan.BasisIndex));
            refusal = Refusal(economics, device, pv, PvOf(economics, device, surplus, load, 0).Allowance.Decision == AllowanceDecision.Start, ZoneOf(economics, device));
        }

        plan.BalanceAllows = index > plan.BasisIndex;
        plan.BalanceReason = refusal;
        plan.EnergyClass = granted;
        plan.GrantReason = granted switch
        {
            EnergyClass.Pv => economics.Setup.Settlement.Id == SettlementModelId.NetBilling ? ReasonCode.SurplusAvailable : ReasonCode.BalanceSurplus,
            EnergyClass.Zone => ReasonCode.ZoneAllowed,
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
        if (economics.Zone is not { } zone || device.Spec.Sources is not { } sources || !sources.AllowsZone(zone))
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
    private static ReasonCode Refusal(EconomicsContext economics, DeviceEcon device, PvState pv, bool surplusPresent, ZoneState zone)
    {
        if (device.Spec.Sources!.Pv)
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
        var turnsOn = spec.IsSetpoint ? DeviceLevels.IsOff(spec, from) && !DeviceLevels.IsOff(spec, to) : to.StagesOn > from.StagesOn;
        return turnsOn ? spec.MinOn : TimeSpan.Zero;
    }

    /// <summary>The power a level draws in W: the rated power of its relay stages, or a setpoint's ceiling at the nominal voltage.</summary>
    private static double PlannedPower(Plan plan, int index, HourlyBalanceSetup config)
    {
        var spec = plan.Device.Spec;
        var level = plan.Levels[index];
        return spec.IsSetpoint
            ? spec.Phases.Sum(p => DeviceLevels.Ceiling(spec, level, p).Value) * config.Voltage.Value
            : spec.Phases.Sum(p => plan.Device.RatedPower(level, p).Value);
    }
}
