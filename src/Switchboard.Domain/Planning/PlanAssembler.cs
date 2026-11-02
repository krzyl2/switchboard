using System.Collections.Immutable;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Units;

namespace Switchboard.Domain.Planning;

/// <summary>Turns the goals as placed into the plan's three outputs: its cost with the two references, a device plan per device (goals and one entry per slot) and the notes.</summary>
internal sealed class PlanAssembler(PlanContext ctx)
{
    public PlanSnapshot Assemble(List<GoalWork> works, List<GoalWork> ordered)
    {
        var planned = ordered.Sum(w => w.Cost);
        var cost = new PlanCost(planned, Reference(ordered, ownSources: true), Reference(ordered, ownSources: false));
        return new PlanSnapshot(ctx.Input.Now, ctx.Input.ConfigRevision, ctx.Input.Inputs, Devices(works), cost, Notes(works));
    }

    /// <summary>The same goals delivered as soon as possible, on what the devices' own sources allow (v2 on its own) or on any energy, from fresh capacity.</summary>
    private double Reference(List<GoalWork> ordered, bool ownSources)
    {
        var ledger = CapacityLedger.Fresh(ctx);
        var total = 0.0;
        foreach (var w in ordered)
        {
            var sources = ownSources ? w.Device.Sources ?? SourceSet.Any : SourceSet.Any;
            var cells = ctx.Cells(w, anyZone: false, valueTest: false, sources).OrderBy(c => c.Slot).ThenBy(c => c.Source is EnergySource.Surplus ? 0 : 1).ThenBy(c => PlanContext.Key(c.Cost)).ThenBy(c => c.Source, EnergySource.Ordering).ToList();
            var into = new List<Alloc>();
            ledger.Fill(ctx, w.Device, cells, w.Need, into, urgent: false);
            total += into.Sum(a => (a.Cost * new KilowattHours(a.Kwh)).Amount);
        }

        return total;
    }

    private ImmutableArray<DevicePlan> Devices(List<GoalWork> works)
    {
        var result = ImmutableArray.CreateBuilder<DevicePlan>();
        foreach (var device in ctx.Devices.GroupBy(d => d.Id).Select(g => g.First()))
        {
            var mine = works.Where(w => w.Device.Id == device.Id).OrderBy(w => w.Request.Deadline is null).ThenBy(w => w.Request.Deadline).ThenByDescending(w => w.Hard).ThenBy(w => w.Request.Revision).ToList();
            result.Add(new DevicePlan(device.Id, [.. mine.Select(w => Goal(w, device))], Entries(device, mine)));
        }

        return result.ToImmutable();
    }

    private PlanGoal Goal(GoalWork w, PlanDevice device)
    {
        var status = w.Met ? GoalStatus.Met
            : w.Expired ? GoalStatus.Expired
            : w.Missed ? GoalStatus.Missed
            : w.Infeasible ? GoalStatus.Infeasible
            : w.Planned && w.Hard && (w.UrgentLatest || w.Escalated || (w.Shortfall > Planner.Tolerance && !w.BeyondHorizon)) ? GoalStatus.AtRisk
            : GoalStatus.OnTrack;
        DateTimeOffset? completion = null;
        if (w.Planned && w.Allocs.Count > 0 && w.Shortfall <= Planner.Tolerance)
        {
            var last = w.Allocs.Max(a => a.Slot);
            var kwhThere = w.Allocs.Where(a => a.Slot == last).Sum(a => a.Kwh);
            var atLast = w.Allocs.Where(a => a.Slot == last).ToList();
            var ceiling = new Watts(atLast.All(a => a.CapW is not null) ? Math.Min(ctx.CeilingW(device), atLast.Max(a => a.CapW!.Value)) : ctx.CeilingW(device)).ToKilowatts();
            var end = ceiling > Kilowatts.Zero ? ctx.Slots[last].Slot.Start + (new KilowattHours(kwhThere) / ceiling) : ctx.Slots[last].Slot.End;
            completion = end < ctx.Slots[last].Slot.End ? end : ctx.Slots[last].Slot.End;
        }

        var urgent = w.Planned && (w.Critical || w.Escalated);
        return new PlanGoal(
            Ref(w, device),
            w.Request.Kind,
            w.Hard,
            status,
            w.Request.Deadline,
            w.Request.RemainingKwh,
            w.Planned ? w.Placed : 0,
            w.Shortfall,
            completion,
            w.LatestGridStart,
            urgent,
            w.Request.OnMet);
    }

    private ImmutableArray<PlanEntry> Entries(PlanDevice device, List<GoalWork> mine)
    {
        var entries = ImmutableArray.CreateBuilder<PlanEntry>(ctx.Count);
        var planned = device.CanStart && device.Kind != PlanDeviceKind.Storage ? mine.Where(w => w.Planned).ToList() : [];
        var ceilingW = ctx.CeilingW(device);
        var stopMet = mine.Where(w => w.Request.OnMet == GoalOnMet.Stop && (w.Met || (w.Planned && w.Shortfall <= Planner.Tolerance))).ToList();
        var holding = planned.Where(w => w.Request.OnMet == GoalOnMet.Stop && w.Request.QuietShortfall && w.Shortfall > Planner.Tolerance).ToList();
        var lastAlloc = planned.SelectMany(w => w.Allocs).Select(a => (int?)a.Slot).DefaultIfEmpty(null).Max();
        for (var t = 0; t < ctx.Count; t++)
        {
            var slot = ctx.Slots[t].Slot;
            var inSlot = planned.SelectMany(w => w.Allocs.Where(a => a.Slot == t).Select(a => (Work: w, Alloc: a))).ToList();
            var energy = inSlot.Sum(x => x.Alloc.Kwh);
            if (energy > Planner.Tolerance)
            {
                var urgent = inSlot.Any(x => x.Alloc.Urgent);

                // One class per entry: any grid energy in the slot makes it a zone entry (dispatch has to open that zone, whatever the surplus share), the dearest zone
                // if there are several; only an entry that is wholly surplus is pv.
                var grid = inSlot.Where(x => x.Alloc.Source is not EnergySource.Surplus).OrderByDescending(x => PlanContext.Key(x.Alloc.Cost)).ThenBy(x => x.Alloc.Source, EnergySource.Ordering).Select(x => x.Alloc).ToList();
                var entryClass = grid.Count > 0 ? grid[0].Source : EnergySource.Surplus.Instance;
                var goal = inSlot.GroupBy(x => x.Work).OrderByDescending(g => g.Sum(x => x.Alloc.Kwh)).ThenBy(g => g.Key.Request.Revision).First().Key;
                entries.Add(new PlanEntry(slot.Start, slot.End, urgent ? PlanWant.Urgent : PlanWant.Run, entryClass, energy, inSlot.All(x => x.Alloc.CapW is not null) ? Math.Min(ceilingW, inSlot.Max(x => x.Alloc.CapW!.Value)) : ceilingW, Ref(goal, device)));
                continue;
            }

            if (lastAlloc is { } last && t < last && planned.FirstOrDefault(w => w.Allocs.Any(a => a.Slot > t)) is { } later)
            {
                entries.Add(new PlanEntry(slot.Start, slot.End, PlanWant.Defer, null, 0, 0, Ref(later, device)));
                continue;
            }

            if (stopMet.FirstOrDefault(w => slot.Start < (w.Request.Deadline ?? ctx.Input.Inputs.Grid.To)) is { } done)
            {
                entries.Add(new PlanEntry(slot.Start, slot.End, PlanWant.Done, null, 0, 0, Ref(done, device)));
                continue;
            }

            // A stop goal that is quiet about its shortfall (a PV-only cap) and is not yet met holds the device until its deadline: the slots with no surplus
            // in the forecast must not fall back to the v2 opportunist, which would charge it from the grid up to full.
            if (holding.FirstOrDefault(w => slot.Start < (w.Request.Deadline ?? ctx.Input.Inputs.Grid.To)) is { } hold)
            {
                entries.Add(new PlanEntry(slot.Start, slot.End, PlanWant.Defer, null, 0, 0, Ref(hold, device)));
                continue;
            }

            entries.Add(new PlanEntry(slot.Start, slot.End, PlanWant.None, null, 0, 0, null));
        }

        return entries.ToImmutable();
    }

    private static PlanGoalRef Ref(GoalWork w, PlanDevice device) => new(Planner.GoalId(w.Request), w.Request.Revision, device.SpecIdentity);

    private ImmutableArray<PlanNote> Notes(List<GoalWork> works)
    {
        var notes = new HashSet<PlanNote>(ctx.Input.Inputs.Notes);
        if (works.Any(w => w.Planned && w.Shortfall > Planner.Tolerance && !w.BeyondHorizon && !w.Request.QuietShortfall))
        {
            notes.Add(PlanNote.GoalShortfall);
        }

        if (works.Any(w => w.Planned && w.Infeasible))
        {
            notes.Add(PlanNote.GoalInfeasible);
        }

        if (works.Any(w => w.Planned && (w.UrgentLatest || w.Escalated)))
        {
            notes.Add(PlanNote.GoalUrgent);
        }

        if (works.Any(w => w.Missed))
        {
            notes.Add(PlanNote.GoalMissed);
        }

        return [.. notes.Order()];
    }
}
