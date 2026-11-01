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

        if (w.Critical)
        {
            // Urgent: from now, earliest first, whatever the plan would rather wait for (the forecast may have failed).
            var anyZone = w.Request.Escalate || w.Missed || w.Infeasible;
            var cells = ctx.Cells(w, anyZone, valueTest: false).OrderBy(c => c.Slot).ThenBy(c => c.Source is EnergySource.Surplus ? 0 : 1).ThenBy(c => PlanContext.Key(c.Cost)).ThenBy(c => c.Source, EnergySource.Ordering).ToList();
            ledger.Fill(ctx, w.Device, cells, w.Need, w.Allocs, urgent: true);
            return;
        }

        // A spread goal finishes as late as its window allows (the equal-cost tie goes to the later slot); every other goal keeps the earliest.
        var regularCells = ctx.Cells(w, anyZone: false, valueTest: !w.Hard);
        var regular = (w.Request.Spread ? regularCells.OrderBy(c => PlanContext.Key(c.Cost)).ThenByDescending(c => c.Slot) : regularCells.OrderBy(c => PlanContext.Key(c.Cost)).ThenBy(c => c.Slot))
            .ThenBy(c => c.Source, EnergySource.Ordering).ToList();
        ledger.Fill(ctx, w.Device, regular, w.Need, w.Allocs, urgent: false);
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
}
