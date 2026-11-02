using Switchboard.Domain.Economics;

namespace Switchboard.Domain.Planning;

/// <summary>The greedy placement of one goal on the ledger: which cells, in which order, and whether it escalates or is spread afterwards.</summary>
internal static class GoalPlacer
{
    public static void Place(PlanContext ctx, CapacityLedger ledger, GoalWork w)
    {
        if (!w.Planned)
        {
            return;
        }

        w.Capped = false;
        if (w.Critical)
        {
            // Urgent: from now, earliest first, whatever the plan would rather wait for (the forecast may have failed).
            var anyZone = w.Request.Escalate || w.Missed || w.Infeasible;
            var cells = ctx.Cells(w, anyZone, valueTest: false).OrderBy(c => c.Slot).ThenBy(c => c.Source is EnergySource.Surplus ? 0 : 1).ThenBy(c => PlanContext.Key(c.Cost)).ThenBy(c => c.Source, EnergySource.Ordering).ToList();
            ledger.Fill(ctx, w.Device, cells, w.Need, w.Allocs, urgent: true);
            return;
        }

        if (w.CapsW is null)
        {
            PlaceRegular(ctx, ledger, w);
            return;
        }

        // Power caps hold only while there is time for them: the goal is placed without them, then with them, and the capped placement stays only when it
        // places as much energy at no higher cost and without escalating. Otherwise the goal runs as if it had no caps.
        var before = ledger.Clone();
        PlaceRegular(ctx, ledger, w);
        var plain = (Allocs: w.Allocs, Placed: w.Placed, Cost: w.Cost, Escalated: w.Escalated, Ledger: ledger.Clone());

        ledger.Restore(before);
        w.Allocs = [];
        w.MarkEscalated(false);
        w.Capped = true;
        PlaceCapped(ctx, ledger, w);
        if (w.Placed >= plain.Placed - Planner.Tolerance && w.Cost <= plain.Cost + Planner.Tolerance)
        {
            return;
        }

        ledger.Restore(plain.Ledger);
        w.Allocs = plain.Allocs;
        w.MarkEscalated(plain.Escalated);
        w.Capped = false;
    }

    private static List<Cell> RegularCells(PlanContext ctx, GoalWork w)
    {
        // A spread goal finishes as late as its window allows (the equal-cost tie goes to the later slot); every other goal keeps the earliest.
        var regularCells = ctx.Cells(w, anyZone: false, valueTest: !w.Hard);
        return (w.Request.Spread ? regularCells.OrderBy(c => PlanContext.Key(c.Cost)).ThenByDescending(c => c.Slot) : regularCells.OrderBy(c => PlanContext.Key(c.Cost)).ThenBy(c => c.Slot))
            .ThenBy(c => c.Source, EnergySource.Ordering).ToList();
    }

    private static void PlaceRegular(PlanContext ctx, CapacityLedger ledger, GoalWork w)
    {
        ledger.Fill(ctx, w.Device, RegularCells(ctx, w), w.Need, w.Allocs, urgent: false);
        if (w.Hard && w.Request.Escalate && !w.BeyondHorizon && w.Shortfall > Planner.Tolerance)
        {
            var extended = ctx.Cells(w, anyZone: true, valueTest: false).OrderBy(c => PlanContext.Key(c.Cost)).ThenBy(c => c.Slot).ThenBy(c => c.Source, EnergySource.Ordering).ToList();
            var before = w.Placed;
            ledger.Fill(ctx, w.Device, extended, w.Shortfall, w.Allocs, urgent: true);
            w.MarkEscalated(w.Placed > before + Planner.Tolerance);
        }

        if (w.Request.Spread && !w.Escalated && w.Shortfall <= Planner.Tolerance)
        {
            SpreadPass.Apply(ctx, ledger, w);
        }
    }

    /// <summary>The goal's own sources under its caps: no escalation (a goal that needs other zones has no time for caps).</summary>
    private static void PlaceCapped(PlanContext ctx, CapacityLedger ledger, GoalWork w)
    {
        ledger.Fill(ctx, w.Device, RegularCells(ctx, w), w.Need, w.Allocs, urgent: false, w.CapsW);
        if (w.Request.Spread && w.Shortfall <= Planner.Tolerance)
        {
            SpreadPass.Apply(ctx, ledger, w);
        }
    }
}
