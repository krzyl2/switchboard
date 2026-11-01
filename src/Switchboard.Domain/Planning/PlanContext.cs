using System.Collections.Immutable;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Economics;

namespace Switchboard.Domain.Planning;

/// <summary>
/// What one planning run reads and never changes: the input, its slot grid and hours, the devices in name order, the cancellation token, and the questions
/// about them that every part of the planner asks (how much of a slot lies before a cutoff, what a device is worth in a slot, what it can draw, which cells a
/// goal may use). The mutable state of a run is the <see cref="CapacityLedger"/> and the <see cref="GoalWork"/> list, never this.
/// </summary>
internal sealed class PlanContext
{
    public PlanContext(PlanInput input, CancellationToken cancel)
    {
        Input = input;
        Cancel = cancel;
        Slots = input.Inputs.Slots;
        Count = Slots.Length;
        Hours = [.. Slots.Select(s => s.Slot.Hours)];
        Devices = [.. input.Devices.OrderBy(d => d.Id)];
        ByName = Devices.GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First());
    }

    public PlanInput Input { get; }

    public CancellationToken Cancel { get; }

    public ImmutableArray<PlanInputSlot> Slots { get; }

    /// <summary>The number of slots.</summary>
    public int Count { get; }

    public double[] Hours { get; }

    /// <summary>The devices in name order (the order of every tie and of the plan's device list).</summary>
    public PlanDevice[] Devices { get; }

    public Dictionary<DeviceName, PlanDevice> ByName { get; }

    /// <summary>A cost as a whole number of nano-units, so equal costs compare equal whatever their arithmetic.</summary>
    public static long Key(double cost) => (long)Math.Round(cost * 1e9, MidpointRounding.AwayFromZero);

    /// <summary>Hours of the slot that lie between now (or the slot's start) and <paramref name="cutoff"/>.</summary>
    public double Overlap(int slot, DateTimeOffset cutoff)
    {
        var start = Slots[slot].Slot.Start > Input.Now ? Slots[slot].Slot.Start : Input.Now;
        var end = Slots[slot].Slot.End < cutoff ? Slots[slot].Slot.End : cutoff;
        return end > start ? (end - start).TotalHours : 0;
    }

    public double EffectiveValue(PlanDevice device, int slot)
    {
        return Count == 0
            ? device.Value.Static
            : device.Value.PerKwhAt(Slots[slot].Import?.PerKwh, Slots[slot].Export?.PerKwh);
    }

    public bool WorthIt(PlanDevice device, int slot, double cost) => ValueTest.Worth(EffectiveValue(device, slot), cost, Input.MinMargin);

    /// <summary>The most the device can draw at once under the static limits (watts).</summary>
    public double CeilingW(PlanDevice device) => Planner.CeilingW(device, Input.CapacityW);

    /// <summary>A device whose minimum power is above what the static limits leave it can never run: running at its minimum would exceed a limit.</summary>
    public bool CannotRun(PlanDevice device) => device.MinW > 0 && CeilingW(device) + 1e-6 < device.MinW;

    /// <summary>What the device could draw before the cutoff with nothing else competing: a physical bound, not a plan.</summary>
    public double Deliverable(PlanDevice device, DateTimeOffset cutoff, double ceilingW)
    {
        if (CannotRun(device))
        {
            return 0;
        }

        var total = 0.0;
        for (var t = 0; t < Count; t++)
        {
            var avail = Overlap(t, cutoff);
            if (avail <= 0)
            {
                continue;
            }

            // With the grid the slot holds what the device can draw; without it, only its surplus.
            var reach = ceilingW / 1000 * avail;
            if (Slots[t].Import is not null)
            {
                total += reach;
            }
            else if (Slots[t].SurplusKwh > Planner.Tolerance)
            {
                total += Math.Min(reach, Slots[t].SurplusKwh);
            }
        }

        return total;
    }

    /// <summary>The cells a goal may fill: per slot before its cutoff the surplus, and the grid where its sources (or <paramref name="anyZone"/>) allow it.</summary>
    public List<Cell> Cells(GoalWork w, bool anyZone, bool valueTest, SourceSet? sourcesOverride = null)
    {
        var sources = sourcesOverride ?? w.Sources;
        var cells = new List<Cell>();
        for (var t = 0; t < Count; t++)
        {
            var avail = Overlap(t, w.Cutoff);
            if (avail <= 0)
            {
                continue;
            }

            var slot = Slots[t];
            if (slot.SurplusKwh > Planner.Tolerance)
            {
                var cost = slot.Export?.PerKwh ?? 0;
                if (!valueTest || slot.Export is null || WorthIt(w.Device, t, cost))
                {
                    cells.Add(new Cell(t, EnergySource.Surplus.Instance, cost, avail));
                }
            }

            if (slot.Import is { } import && (anyZone || sources.AllZones || (ZoneId.Of(slot.Zone) is { } slotZone && sources.Zones.Contains(slotZone))) && (!valueTest || WorthIt(w.Device, t, import.PerKwh)))
            {
                cells.Add(new Cell(t, new EnergySource.Grid(ZoneId.Of(slot.Zone)), import.PerKwh, avail));
            }
        }

        return cells;
    }
}
