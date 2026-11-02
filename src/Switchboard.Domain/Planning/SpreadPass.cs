using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Units;

namespace Switchboard.Domain.Planning;

/// <summary>
/// Spreads a goal's grid energy evenly over the contiguous window of equal-cost slots it ends in, instead of at the device's ceiling (a long night at 14 A, not
/// a short burst at 20 A): the lowest power that still holds the energy, raised to the device's next current step, never below its minimum and never above its
/// ceiling. The energy and its cost do not change, only where in the window it sits (the latest slots first); what cannot be spread is left as it was.
/// An urgent, infeasible or missed goal never comes here: it runs at the ceiling from now.
/// </summary>
internal static class SpreadPass
{
    public static void Apply(PlanContext ctx, CapacityLedger ledger, GoalWork w)
    {
        var device = w.Device;
        var ceilingW = ctx.CeilingW(device);
        if (device.Kind != PlanDeviceKind.Modulating || ceilingW <= 0 || ctx.CannotRun(device) || w.Critical)
        {
            return;
        }

        // Right after the fill: every non-urgent grid placement is the fill's (a capped fill carries its slot's cap, which bounds the spread there too).
        var grid = w.Allocs.Where(a => a.Source is not EnergySource.Surplus && !a.Urgent).ToList();
        if (grid.Count == 0)
        {
            return;
        }

        // The dearest level the goal had to use is the one that was not exhausted; the cheaper ones are full and stay as they are.
        var level = grid.Max(a => PlanContext.Key(a.Cost));
        var cells = new Dictionary<int, Cell>();
        foreach (var cell in ctx.Cells(w, anyZone: false, valueTest: !w.Hard))
        {
            if (cell.Source is not EnergySource.Surplus && PlanContext.Key(cell.Cost) == level)
            {
                cells.TryAdd(cell.Slot, cell);
            }
        }

        var last = grid.Where(a => PlanContext.Key(a.Cost) == level).Max(a => a.Slot);
        var window = new List<int>();
        for (var t = last; cells.ContainsKey(t); t--)
        {
            window.Add(t);
        }

        // A window of one slot (the last hour of a night charge, or a short deadline) is not spread: with the SoC arriving in whole percent steps the lowest power
        // of a single slot would be recomputed to a new step at every replan, and a constant ceiling there only finishes the last slot early, never late.
        if (window.Count < 2)
        {
            return;
        }

        var mine = grid.Where(a => PlanContext.Key(a.Cost) == level && window.Contains(a.Slot)).ToList();
        var energy = mine.Sum(a => a.Kwh);
        ledger.Release(device, mine);
        var minePool = mine.ToHashSet();
        w.Allocs = [.. w.Allocs.Where(a => !minePool.Contains(a))];

        var capacity = window.ToDictionary(t => t, t => ledger.Room(ctx, device, cells[t]));
        double SlotPower(double powerW, int t) => w.Capped && w.CapsW is { } caps ? Math.Min(powerW, caps[t]) : powerW;
        double Held(double powerW) => window.Sum(t => Math.Min((new Watts(SlotPower(powerW, t)).ToKilowatts() * cells[t].Available).Value, capacity[t]));

        if (Held(ceilingW) < energy - Planner.Tolerance)
        {
            Recommit(ledger, w, mine);
            return;
        }

        double low = Math.Min(device.MinW, ceilingW), high = ceilingW;
        for (var i = 0; i < 60 && high - low > 1e-9; i++)
        {
            var mid = (low + high) / 2;
            if (Held(mid) >= energy - Planner.Tolerance)
            {
                high = mid;
            }
            else
            {
                low = mid;
            }
        }

        var power = Math.Max(high, device.MinW);
        if (device.StepW is { } step && step > 0)
        {
            power = device.MinW + (Math.Ceiling(((power - device.MinW) / step) - 1e-6) * step);
        }

        power = Math.Min(power, ceilingW);
        var minFill = (new Watts(device.MinW) * device.MinRun).Value;
        var placed = new List<Alloc>();
        var remaining = energy;
        foreach (var t in window)
        {
            if (remaining <= Planner.Tolerance)
            {
                break;
            }

            var slotPower = SlotPower(power, t);
            var take = Math.Min(remaining, Math.Min((new Watts(slotPower).ToKilowatts() * cells[t].Available).Value, capacity[t]));
            if (take <= Planner.Tolerance || (take < minFill - Planner.Tolerance && take < remaining - Planner.Tolerance))
            {
                continue;
            }

            ledger.Commit(device, cells[t], take, placed, urgent: false, slotPower);
            remaining -= take;
        }

        if (remaining > Planner.Tolerance)
        {
            ledger.Release(device, placed);
            Recommit(ledger, w, mine);
            return;
        }

        w.Allocs.AddRange(placed);
    }

    private static void Recommit(CapacityLedger ledger, GoalWork w, List<Alloc> allocs)
    {
        foreach (var a in allocs)
        {
            ledger.Commit(w.Device, new Cell(a.Slot, a.Source, a.Cost, TimeSpan.Zero), a.Kwh, w.Allocs, a.Urgent, a.CapW);
        }
    }
}
