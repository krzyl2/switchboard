using System.Collections.Immutable;
using Switchboard.Domain.Balance;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Dispatch;

/// <summary>The hourly balance as a gate of the dispatch loop (BAL-02 … BAL-05, 13-HOURLY-BALANCE).</summary>
public sealed partial class DispatchEngine
{
    private readonly FreePowerWindow _free = new();
    private BalanceView? _balance;
    private ImmutableArray<DevicePhasePower> _devicePowers = [];

    /// <summary>
    /// The hourly balance as this step reads it (BAL-02): free power (grid net export plus every own load
    /// added back, so the projection does not shrink by what Switchboard itself switched on) smoothed
    /// over the window, and the balance of the running hour, valid only for the hour its time to the :00
    /// is taken from and only while the monitor's reading is no older than max_age. Null while no energy
    /// counters are mapped: the balance is off until configured and nothing is gated.
    /// </summary>
    private BalanceView? ObserveBalance(DispatchInput input, DispatchSetup setup, Dictionary<Phase, Watts?> ours, DateTimeOffset now)
    {
        if (setup.Balance is not { } config || input.Inputs.Energy is null)
        {
            _free.Reset();
            return null;
        }

        var parameters = config.Parameters;
        Watts? export = Watts.Zero;
        Watts? load = Watts.Zero;
        foreach (var phase in setup.Phases)
        {
            var own = ours.GetValueOrDefault(phase);
            load = load is { } running && own is { } drawn ? running + drawn : null;
            export = export is { } sum && own is { } added && input.Inputs.For(phase) is { DataValid: true, NetImport: { } netImport } ? sum + added - netImport : null;
        }

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
    /// The allowance rule of 13-HOURLY-BALANCE as a gate on the devices that declare start: headroom
    /// (BAL-04, BAL-05). It only ever narrows what dispatch may do. Stops come first, lowest value
    /// first: while the projection is below the floor each running device is lowered one stage and the
    /// load comes down by it. Starts then walk the devices in value order, so what is left of the
    /// surplus goes to the most valuable device that could take it; a device counts as a candidate only
    /// if the allocation shows it could rise now (guard permission and phase headroom), and the load of
    /// each start is added before the next device is judged. With the balance or the free power unknown
    /// the allowance is zero: nothing rises, and a device Switchboard itself started is lowered one
    /// stage. Returns whether any device's limits changed, so the allocation is redone.
    /// </summary>
    private static bool ApplyBalanceGate(List<Plan> plans, Dictionary<DeviceName, DeviceAllocation> result, BalanceView view, ImmutableArray<DevicePhasePower> powers, DispatchSetup setup, DispatchInput input)
    {
        if (setup.Balance is not { } config)
        {
            return false;
        }

        var parameters = config.Parameters;
        var gated = plans.Where(p => p.Mode == OverrideMode.Auto
            && p.Device.Spec.Start == StartPolicy.Headroom
            && !p.Excluded
            && input.SelfTest?.Device != p.Device.Id).ToList();
        var changed = false;
        foreach (var plan in gated)
        {
            plan.BalanceGated = true;
        }

        if (!view.Known)
        {
            foreach (var plan in gated)
            {
                plan.BalanceAllows = false;
                plan.BalanceReason = ReasonCode.BalanceUnknown;
                changed |= Cap(plan, plan.BasisIndex);
                if (plan.BasisIndex > 0 && input.Ledger.Entry(plan.Device.Id)?.SelfStarted is not null)
                {
                    changed |= LowerOneStage(plan, ReasonCode.BalanceUnknown);
                }
            }

            return changed;
        }

        // The load is what Switchboard would run: the measured draw of every device, except that a gated
        // device counts at its planned level, so a start decided but not yet drawing (a command in flight,
        // or shadow mode, where nothing is ever applied) is already in the projection and is not started twice.
        var load = view.Load!.Value.Value;
        foreach (var plan in gated)
        {
            var measured = powers.Where(p => p.Device == plan.Device.Id).Sum(p => p.Power?.Value ?? 0);
            load += PlannedPower(plan, plan.BasisIndex, config) - measured;
        }

        for (var i = gated.Count - 1; i >= 0; i--)
        {
            var plan = gated[i];
            if (plan.BasisIndex <= 0
                || HourlyBalanceRule.Decide(view.BalanceWh, view.Free, new Watts(load), Watts.Zero, view.UntilNext, parameters) != BalanceWant.Stop)
            {
                continue;
            }

            var shed = PlannedPower(plan, plan.BasisIndex, config) - PlannedPower(plan, plan.BasisIndex - 1, config);
            if (LowerOneStage(plan, ReasonCode.BalanceProjectionLow))
            {
                load -= shed;
                changed = true;
            }
        }

        foreach (var plan in gated)
        {
            var wants = result[plan.Device.Id].Uncapped;
            if (plan.BalanceStop is not null || wants <= plan.BasisIndex || !plan.GuardAllows || !plan.DataValid)
            {
                continue;
            }

            var top = Math.Min(wants, plan.CapIndex);
            var index = plan.BasisIndex;
            while (index < top)
            {
                var candidate = PlannedPower(plan, index + 1, config) - PlannedPower(plan, index, config);
                if (candidate <= 0
                    || HourlyBalanceRule.Decide(view.BalanceWh, view.Free, new Watts(load), new Watts(candidate), view.UntilNext, parameters) != BalanceWant.Start)
                {
                    break;
                }

                load += candidate;
                index++;
            }

            plan.BalanceAllows = index > plan.BasisIndex;
            plan.BalanceReason = ReasonCode.BalanceProjectionLow;
            changed |= Cap(plan, index);
        }

        return changed;
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
