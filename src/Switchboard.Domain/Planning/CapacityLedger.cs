using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Units;

namespace Switchboard.Domain.Planning;

/// <summary>One placement: <paramref name="Kwh"/> of <paramref name="Source"/> in a slot at a cost per kWh.</summary>
/// <param name="CapW">The ceiling the entry carries for this energy when a goal spread it over its window (<see cref="Switchboard.Domain.Goals.GoalRequest.Spread"/>); null: the device's ceiling.</param>
internal readonly record struct Alloc(int Slot, EnergySource Source, double Kwh, PricePerKwh Cost, bool Urgent, double? CapW = null);

/// <summary>A place energy may go: a slot, the source it would come from, its cost per kWh and the hours of the slot a goal can use.</summary>
internal readonly record struct Cell(int Slot, EnergySource Source, PricePerKwh Cost, TimeSpan Available);

/// <summary>
/// What is left of every limit per slot while goals are placed: the surplus, each constraint's static capacity and each device's own power, all in kWh. A
/// placement takes from all three, a release gives back, and a copy can be kept and put back (the refinement tries an order and undoes it).
/// </summary>
internal sealed class CapacityLedger
{
    private double[] _pv;
    private Dictionary<ConstraintKey, double[]> _cap;
    private Dictionary<DeviceName, double[]> _dev;

    private CapacityLedger(double[] pv, Dictionary<ConstraintKey, double[]> cap, Dictionary<DeviceName, double[]> dev)
    {
        _pv = pv;
        _cap = cap;
        _dev = dev;
    }

    /// <summary>Every limit untouched: the whole static capacity and the whole surplus of each slot.</summary>
    public static CapacityLedger Fresh(PlanContext ctx)
    {
        var cap = new Dictionary<ConstraintKey, double[]>();
        foreach (var (key, watts) in ctx.Input.CapacityW.OrderBy(p => p.Key.ToString(), StringComparer.Ordinal))
        {
            cap[key] = [.. ctx.Durations.Select(d => (new Watts(Math.Max(0, watts)).ToKilowatts() * d).Value)];
        }

        var dev = ctx.Devices.ToDictionary(d => d.Id, d => (double[])[.. ctx.Durations.Select(h => (new Watts(Math.Max(0, d.MaxW)).ToKilowatts() * h).Value)]);
        return new CapacityLedger([.. ctx.Slots.Select(s => s.SurplusKwh)], cap, dev);
    }

    public CapacityLedger Clone() =>
        new((double[])_pv.Clone(), _cap.ToDictionary(p => p.Key, p => (double[])p.Value.Clone()), _dev.ToDictionary(p => p.Key, p => (double[])p.Value.Clone()));

    /// <summary>Puts a copy made by <see cref="Clone"/> back in place of what the ledger holds now; the copy is the ledger's from here on.</summary>
    public void Restore(CapacityLedger saved)
    {
        _pv = saved._pv;
        _cap = saved._cap;
        _dev = saved._dev;
    }

    /// <summary>The most the device can still take in the cell (kWh): its own power, every constraint it loads and, for surplus, what the slot has left.</summary>
    public double Room(PlanContext ctx, PlanDevice device, Cell cell)
    {
        if (ctx.CannotRun(device))
        {
            return 0;
        }

        var t = cell.Slot;
        var capacity = Math.Min(_dev[device.Id][t], (new Watts(device.MaxW) * cell.Available).Value);
        foreach (var key in device.Keys)
        {
            if (key.Share <= 0)
            {
                continue;
            }

            capacity = Math.Min(capacity, _cap.TryGetValue(key.Key, out var left) ? left[t] / key.Share : 0);
        }

        if (cell.Source is EnergySource.Surplus)
        {
            capacity = Math.Min(capacity, _pv[t]);
        }

        return Math.Max(0, capacity);
    }

    /// <summary>
    /// Fills the cells in the order given, each up to its room, until <paramref name="need"/> is placed. A device on runs at least its minimum for at least its
    /// minimum run: a cell that cannot hold that is skipped. The one exception is the last residual of a goal, which dispatch's min_on rounds up on its own.
    /// </summary>
    public void Fill(PlanContext ctx, PlanDevice device, List<Cell> cells, double need, List<Alloc> into, bool urgent)
    {
        var minFill = (new Watts(device.MinW) * device.MinRun).Value;
        foreach (var cell in cells)
        {
            if (need <= Planner.Tolerance)
            {
                break;
            }

            ctx.Cancel.ThrowIfCancellationRequested();
            var take = Math.Min(need, Room(ctx, device, cell));
            if (take <= Planner.Tolerance)
            {
                continue;
            }

            if (take < minFill - Planner.Tolerance && take < need - Planner.Tolerance)
            {
                continue;
            }

            Commit(device, cell, take, into, urgent);
            need -= take;
        }
    }

    /// <summary>Takes <paramref name="kwh"/> of the cell from every limit and adds it to <paramref name="into"/> (merged with an equal placement there).</summary>
    public void Commit(PlanDevice device, Cell cell, double kwh, List<Alloc> into, bool urgent, double? capW = null)
    {
        var t = cell.Slot;
        _dev[device.Id][t] -= kwh;
        foreach (var key in device.Keys)
        {
            if (_cap.TryGetValue(key.Key, out var left))
            {
                left[t] -= kwh * key.Share;
            }
        }

        if (cell.Source is EnergySource.Surplus)
        {
            _pv[t] -= kwh;
        }

        var index = into.FindIndex(a => a.Slot == t && a.Source == cell.Source && a.Urgent == urgent && a.CapW == capW);
        if (index >= 0)
        {
            into[index] = into[index] with { Kwh = into[index].Kwh + kwh };
        }
        else
        {
            into.Add(new Alloc(t, cell.Source, kwh, cell.Cost, urgent, capW));
        }
    }

    /// <summary>Gives back the capacity of <paramref name="allocs"/> (all of a goal's or some of them); the goal's own list is the caller's.</summary>
    public void Release(PlanDevice device, IEnumerable<Alloc> allocs)
    {
        foreach (var a in allocs)
        {
            _dev[device.Id][a.Slot] += a.Kwh;
            foreach (var key in device.Keys)
            {
                if (_cap.TryGetValue(key.Key, out var left))
                {
                    left[a.Slot] += a.Kwh * key.Share;
                }
            }

            if (a.Source is EnergySource.Surplus)
            {
                _pv[a.Slot] += a.Kwh;
            }
        }
    }
}
