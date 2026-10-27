using System.Collections.Immutable;
using System.Globalization;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Goals;

namespace Switchboard.Domain.Planning;

/// <summary>
/// The pure planner (15-PLANNER, "The planning algorithm"): <see cref="Plan"/> turns a <see cref="PlanInput"/> into a <see cref="PlanSnapshot"/>.
/// No clock, no I/O, no entity, no randomness, no dependency on the order the devices or goals arrive in: every tie is broken by device name, slot
/// start and class order, so the same input is the same plan to the byte.
/// <para>
/// A kWh a goal device draws costs the export value while it fits the slot's surplus (class <c>pv</c>) and the import price of the slot beyond it (class
/// <c>zone</c>). Goals are placed earliest-deadline-first (urgent, then hard, then soft), each on its cheapest cells with a tie going to the earliest
/// slot, within the guard's static limit per constraint, the device's own power and the surplus; a short refinement gives each goal in turn first call on
/// the capacity and keeps the result only if it is better. A hard goal that its sources cannot meet adds every zone (<see cref="PlanWant.Urgent"/>), and
/// one at its latest safe start runs from now, so a failed forecast cannot cost a deadline. Storage is not planned here (slice 6).
/// </para>
/// </summary>
public static class Planner
{
    /// <summary>Numeric noise of energy arithmetic in kWh, not a control parameter.</summary>
    public const double Tolerance = 1e-9;

    public static PlanSnapshot Plan(PlanInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new PlanRun(input, cancellationToken).Run();
    }

    /// <summary>The most a device can draw at once: its maximum, and what the static limits leave on every constraint it loads (watts).</summary>
    internal static double CeilingW(PlanDevice device, ImmutableDictionary<ConstraintKey, double> capacityW)
    {
        var ceiling = device.MaxW;
        foreach (var key in device.Keys)
        {
            if (key.Share <= 0)
            {
                continue;
            }

            ceiling = Math.Min(ceiling, capacityW.TryGetValue(key.Key, out var limit) ? limit / key.Share : 0);
        }

        return Math.Max(0, ceiling);
    }

    /// <summary>The goal's identity in a plan: the device and the deadline it is due at.</summary>
    public static string GoalId(GoalRequest request) =>
        request.Device.Value + "@" + (request.Deadline is { } deadline ? deadline.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) : "horizon");

    private readonly record struct Alloc(int Slot, bool Pv, string? Zone, double Kwh, double Cost, bool Urgent);

    private readonly record struct Cell(int Slot, bool Pv, string? Zone, double Cost, double AvailHours);

    private sealed class Work(GoalRequest request, PlanDevice device)
    {
        public GoalRequest Request { get; } = request;

        public PlanDevice Device { get; } = device;

        public bool Hard => Request.Hard;

        public double Need => Request.RemainingKwh;

        public bool Met { get; set; }

        public bool Missed { get; set; }

        public bool Expired { get; set; }

        /// <summary>Placed at all: a live goal of a device the plan may start.</summary>
        public bool Planned { get; set; }

        public bool Infeasible { get; set; }

        public bool UrgentLatest { get; set; }

        public bool Escalate { get; set; }

        /// <summary>The deadline is past the plan's horizon: what the horizon cannot hold is not a shortfall yet (a later plan will see the rest of the time).</summary>
        public bool BeyondHorizon { get; set; }

        public bool Escalated { get; set; }

        public DateTimeOffset Cutoff { get; set; }

        public DateTimeOffset? LatestGridStart { get; set; }

        public SourceSet Sources { get; set; } = SourceSet.Any;

        public double VEff { get; set; }

        public List<Alloc> Allocs { get; set; } = [];

        public bool Critical => Missed || Infeasible || UrgentLatest;

        public double Placed => Allocs.Sum(a => a.Kwh);

        public double Shortfall => Planned ? Math.Max(0, Need - Placed) : 0;

        public double Cost => Allocs.Sum(a => a.Kwh * a.Cost);
    }

    private sealed class State(double[] pv, Dictionary<ConstraintKey, double[]> cap, Dictionary<DeviceName, double[]> dev)
    {
        public double[] Pv { get; } = pv;

        public Dictionary<ConstraintKey, double[]> Cap { get; } = cap;

        public Dictionary<DeviceName, double[]> Dev { get; } = dev;

        public State Clone() =>
            new((double[])Pv.Clone(), Cap.ToDictionary(p => p.Key, p => (double[])p.Value.Clone()), Dev.ToDictionary(p => p.Key, p => (double[])p.Value.Clone()));
    }

    private sealed class PlanRun
    {
        private readonly PlanInput _in;
        private readonly CancellationToken _cancel;
        private readonly ImmutableArray<PlanInputSlot> _slots;
        private readonly int _n;
        private readonly double[] _hours;
        private readonly PlanDevice[] _devices;
        private readonly Dictionary<DeviceName, PlanDevice> _byName;
        private State _state;

        public PlanRun(PlanInput input, CancellationToken cancel)
        {
            _in = input;
            _cancel = cancel;
            _slots = input.Inputs.Slots;
            _n = _slots.Length;
            _hours = [.. _slots.Select(s => s.Slot.Hours)];
            _devices = [.. input.Devices.OrderBy(d => d.Id)];
            _byName = _devices.GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First());
            _state = Fresh();
        }

        public PlanSnapshot Run()
        {
            var works = BuildWorks();
            _cancel.ThrowIfCancellationRequested();

            var ordered = works.Where(w => w.Planned).OrderBy(w => w, Comparer<Work>.Create(CompareUrgency)).ToList();
            foreach (var w in ordered)
            {
                _cancel.ThrowIfCancellationRequested();
                FillGoal(w);
            }

            _allWorks = ordered;
            Refine(ordered.Where(w => !w.Critical).ToList());

            var planned = ordered.Sum(w => w.Cost);
            var cost = new PlanCost(planned, Reference(ordered, ownSources: true), Reference(ordered, ownSources: false));
            return new PlanSnapshot(_in.Now, _in.ConfigRevision, _in.Inputs, Devices(works), cost, Notes(works));
        }

        private State Fresh()
        {
            var cap = new Dictionary<ConstraintKey, double[]>();
            foreach (var (key, watts) in _in.CapacityW.OrderBy(p => p.Key.ToString(), StringComparer.Ordinal))
            {
                cap[key] = [.. _hours.Select(h => Math.Max(0, watts) / 1000 * h)];
            }

            var dev = _devices.ToDictionary(d => d.Id, d => (double[])[.. _hours.Select(h => Math.Max(0, d.MaxW) / 1000 * h)]);
            return new State([.. _slots.Select(s => s.SurplusKwh)], cap, dev);
        }

        // Goals ---------------------------------------------------------------------------------------------------------------------------

        private List<Work> BuildWorks()
        {
            var works = new List<Work>();
            foreach (var request in _in.Goals.OrderBy(r => r.Device).ThenBy(r => r.Deadline is null).ThenBy(r => r.Deadline).ThenByDescending(r => r.Hard).ThenBy(r => r.Revision))
            {
                if (!_byName.TryGetValue(request.Device, out var device))
                {
                    continue;
                }

                works.Add(Describe(request, device));
            }

            return works;
        }

        private Work Describe(GoalRequest request, PlanDevice device)
        {
            var now = _in.Now;
            var horizon = _in.Inputs.Grid.To;
            var w = new Work(request, device)
            {
                Met = GoalMath.IsMet(request.RemainingKwh),
                Sources = request.Sources ?? device.Sources ?? SourceSet.Any,
                Escalate = request.Escalate,
                VEff = VEff(device, 0),
            };
            var passed = request.Deadline is { } deadline && now >= deadline;
            w.Missed = !w.Met && request.Hard && passed;
            w.Expired = !w.Met && !request.Hard && passed;
            w.Cutoff = request.Deadline is null || w.Missed
                ? horizon
                : request.UsableDeadline!.Value > now ? request.UsableDeadline.Value : request.Deadline.Value;
            w.Planned = !w.Met && !w.Expired && device.CanStart && device.Kind != PlanDeviceKind.Storage;
            if (!w.Planned)
            {
                return w;
            }

            var ceilingW = CeilingW(device, _in.CapacityW);
            w.BeyondHorizon = w.Cutoff > horizon;
            w.Infeasible = request.Hard && !w.BeyondHorizon && Deliverable(w, ceilingW) < w.Need - Tolerance;
            if (request.Hard && request.Deadline is not null && !w.Missed)
            {
                // Grid-only delivery at full power: the last start that still makes the deadline (15-PLANNER step 6).
                w.LatestGridStart = ceilingW > 0 ? w.Cutoff - TimeSpan.FromHours(w.Need / (ceilingW / 1000)) : null;
                w.UrgentLatest = ceilingW <= 0 || now >= w.LatestGridStart - _in.Parameters.HardGoal.EscalationMargin;
            }

            return w;
        }

        /// <summary>What the goal's device could draw before the cutoff with nothing else competing: a physical bound, not a plan.</summary>
        private double Deliverable(Work w, double ceilingW)
        {
            if (CannotRun(w.Device))
            {
                return 0;
            }

            var total = 0.0;
            for (var t = 0; t < _n; t++)
            {
                var avail = Overlap(t, w.Cutoff);
                if (avail <= 0)
                {
                    continue;
                }

                // With the grid the slot holds what the device can draw; without it, only its surplus.
                var reach = ceilingW / 1000 * avail;
                if (_slots[t].Import is not null)
                {
                    total += reach;
                }
                else if (_slots[t].SurplusKwh > Tolerance)
                {
                    total += Math.Min(reach, _slots[t].SurplusKwh);
                }
            }

            return total;
        }

        private int CompareUrgency(Work a, Work b)
        {
            // Earliest deadline first among hard goals, then the soft ones: an urgent or infeasible goal does not jump the queue (it would starve a feasible goal
            // with an earlier deadline of the very energy it needs); it simply fills earliest-first when its turn comes.
            var c = b.Hard.CompareTo(a.Hard);
            if (c != 0)
            {
                return c;
            }

            c = a.Cutoff.CompareTo(b.Cutoff);
            if (c != 0)
            {
                return c;
            }

            // Goals pursued past their deadline all end at the horizon: the one that was due first goes first.
            c = (a.Request.Deadline ?? DateTimeOffset.MaxValue).CompareTo(b.Request.Deadline ?? DateTimeOffset.MaxValue);
            if (c != 0)
            {
                return c;
            }

            c = b.VEff.CompareTo(a.VEff);
            if (c != 0)
            {
                return c;
            }

            c = a.Device.Id.CompareTo(b.Device.Id);
            return c != 0 ? c : a.Request.Revision.CompareTo(b.Request.Revision);
        }

        private double Overlap(int slot, DateTimeOffset cutoff)
        {
            var start = _slots[slot].Slot.Start > _in.Now ? _slots[slot].Slot.Start : _in.Now;
            var end = _slots[slot].Slot.End < cutoff ? _slots[slot].Slot.End : cutoff;
            return end > start ? (end - start).TotalHours : 0;
        }

        private double VEff(PlanDevice device, int slot)
        {
            if (_n == 0 || device.Value.Follows == ValueFollows.Static)
            {
                return device.Value.Static;
            }

            var price = device.Value.Follows == ValueFollows.ImportPrice ? _slots[slot].Import : _slots[slot].Export;
            return price is { } p ? device.Value.Factor * p.PerKwh : device.Value.Static;
        }

        // Filling -------------------------------------------------------------------------------------------------------------------------

        private void FillGoal(Work w)
        {
            if (!w.Planned)
            {
                return;
            }

            if (w.Critical)
            {
                // Urgent: from now, earliest first, whatever the plan would rather wait for (the forecast may have failed).
                var anyZone = w.Escalate || w.Missed || w.Infeasible;
                var cells = Cells(w, anyZone, valueTest: false).OrderBy(c => c.Slot).ThenBy(c => c.Pv ? 0 : 1).ThenBy(c => Key(c.Cost)).ThenBy(c => c.Zone, StringComparer.Ordinal).ToList();
                Fill(_state, w, cells, w.Need, w.Allocs, urgent: true);
                return;
            }

            var regular = Cells(w, anyZone: false, valueTest: !w.Hard).OrderBy(c => Key(c.Cost)).ThenBy(c => c.Slot).ThenBy(c => c.Pv ? 0 : 1).ThenBy(c => c.Zone, StringComparer.Ordinal).ToList();
            Fill(_state, w, regular, w.Need, w.Allocs, urgent: false);
            if (w.Hard && w.Escalate && !w.BeyondHorizon && w.Shortfall > Tolerance)
            {
                var extended = Cells(w, anyZone: true, valueTest: false).OrderBy(c => Key(c.Cost)).ThenBy(c => c.Slot).ThenBy(c => c.Pv ? 0 : 1).ThenBy(c => c.Zone, StringComparer.Ordinal).ToList();
                var before = w.Placed;
                Fill(_state, w, extended, w.Shortfall, w.Allocs, urgent: true);
                w.Escalated = w.Placed > before + Tolerance;
            }
        }

        private static long Key(double cost) => (long)Math.Round(cost * 1e9, MidpointRounding.AwayFromZero);

        private List<Cell> Cells(Work w, bool anyZone, bool valueTest, SourceSet? sourcesOverride = null)
        {
            var sources = sourcesOverride ?? w.Sources;
            var cells = new List<Cell>();
            for (var t = 0; t < _n; t++)
            {
                var avail = Overlap(t, w.Cutoff);
                if (avail <= 0)
                {
                    continue;
                }

                var slot = _slots[t];
                if (sources.Pv && slot.SurplusKwh > Tolerance)
                {
                    var cost = slot.Export?.PerKwh ?? 0;
                    if (!valueTest || slot.Export is null || WorthIt(w, t, cost))
                    {
                        cells.Add(new Cell(t, true, null, cost, avail));
                    }
                }

                if (slot.Import is { } import && (anyZone || sources.AllZones || (slot.Zone is not null && sources.Zones.Contains(slot.Zone))) && (!valueTest || WorthIt(w, t, import.PerKwh)))
                {
                    cells.Add(new Cell(t, false, slot.Zone, import.PerKwh, avail));
                }
            }

            return cells;
        }

        private bool WorthIt(Work w, int slot, double cost) => VEff(w.Device, slot) - cost >= _in.MinMargin - 1e-9;

        /// <summary>A device whose minimum power is above what the static limits leave it can never run: running at its minimum would exceed a limit.</summary>
        private bool CannotRun(PlanDevice device) => device.MinW > 0 && CeilingW(device, _in.CapacityW) + 1e-6 < device.MinW;

        private double Capacity(State state, Work w, Cell cell)
        {
            if (CannotRun(w.Device))
            {
                return 0;
            }

            var t = cell.Slot;
            var capacity = Math.Min(state.Dev[w.Device.Id][t], cell.AvailHours * w.Device.MaxW / 1000);
            foreach (var key in w.Device.Keys)
            {
                if (key.Share <= 0)
                {
                    continue;
                }

                capacity = Math.Min(capacity, state.Cap.TryGetValue(key.Key, out var left) ? left[t] / key.Share : 0);
            }

            if (cell.Pv)
            {
                capacity = Math.Min(capacity, state.Pv[t]);
            }

            return Math.Max(0, capacity);
        }

        private void Fill(State state, Work w, List<Cell> cells, double need, List<Alloc> into, bool urgent)
        {
            var minFill = w.Device.MinW * w.Device.MinRun.TotalHours / 1000;
            foreach (var cell in cells)
            {
                if (need <= Tolerance)
                {
                    break;
                }

                _cancel.ThrowIfCancellationRequested();
                var take = Math.Min(need, Capacity(state, w, cell));
                if (take <= Tolerance)
                {
                    continue;
                }

                // A device on runs at least its minimum for at least its minimum run: a cell that cannot hold that is skipped. The one exception is
                // the last residual of a goal, which dispatch's min_on rounds up on its own.
                if (take < minFill - Tolerance && take < need - Tolerance)
                {
                    continue;
                }

                Commit(state, w, cell, take, into, urgent);
                need -= take;
            }
        }

        private static void Commit(State state, Work w, Cell cell, double kwh, List<Alloc> into, bool urgent)
        {
            var t = cell.Slot;
            state.Dev[w.Device.Id][t] -= kwh;
            foreach (var key in w.Device.Keys)
            {
                if (state.Cap.TryGetValue(key.Key, out var left))
                {
                    left[t] -= kwh * key.Share;
                }
            }

            if (cell.Pv)
            {
                state.Pv[t] -= kwh;
            }

            var index = into.FindIndex(a => a.Slot == t && a.Pv == cell.Pv && a.Zone == cell.Zone && a.Urgent == urgent);
            if (index >= 0)
            {
                into[index] = into[index] with { Kwh = into[index].Kwh + kwh };
            }
            else
            {
                into.Add(new Alloc(t, cell.Pv, cell.Zone, kwh, cell.Cost, urgent));
            }
        }

        private void Release(Work w)
        {
            foreach (var a in w.Allocs)
            {
                _state.Dev[w.Device.Id][a.Slot] += a.Kwh;
                foreach (var key in w.Device.Keys)
                {
                    if (_state.Cap.TryGetValue(key.Key, out var left))
                    {
                        left[a.Slot] += a.Kwh * key.Share;
                    }
                }

                if (a.Pv)
                {
                    _state.Pv[a.Slot] += a.Kwh;
                }
            }

            w.Allocs = [];
            w.Escalated = false;
        }

        // Refinement ----------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Tries other orders of the goals: each goal in turn is moved to another place in the current order (to the front only, when there are many goals), the
        /// goals are released and refilled in that order, and the arrangement is kept only if less need is left unmet or, with the same need met, the cost is
        /// lower. It repairs what the earliest-deadline order gets wrong, a goal that took a cell another needed (the design's "refine": a goal released and
        /// refilled with first call on the capacity is the move-to-front case), never raises the cost, and stops after <c>planner.refine_passes</c> passes or
        /// when a pass finds nothing. A pass is at most n squared fills of tens of cells: milliseconds.
        /// </summary>
        private void Refine(List<Work> works)
        {
            if (works.Count < 2 || _in.Parameters.RefinePasses <= 0)
            {
                return;
            }

            var order = new List<Work>(works);
            var full = order.Count <= MaxReorderedGoals;
            var best = Score();
            if (order.Count <= MaxPermutedGoals)
            {
                // A handful of goals (a car and a battery, two trips): every order is tried, so what the order can repair, it repairs.
                var kept = order;
                foreach (var trial in Permutations(order))
                {
                    _cancel.ThrowIfCancellationRequested();
                    var saved = Save(kept);
                    foreach (var w in kept)
                    {
                        Release(w);
                    }

                    foreach (var w in trial)
                    {
                        FillGoal(w);
                    }

                    var score = Score();
                    if (score.Shortfall < best.Shortfall - Tolerance || (Math.Abs(score.Shortfall - best.Shortfall) <= Tolerance && score.Cost < best.Cost - 1e-9))
                    {
                        best = score;
                        kept = trial;
                    }
                    else
                    {
                        Restore(saved, kept);
                    }
                }

                order = kept;
            }

            for (var pass = 0; pass < _in.Parameters.RefinePasses; pass++)
            {
                var improved = false;
                for (var i = 0; i < order.Count; i++)
                {
                    for (var j = 0; j < order.Count; j++)
                    {
                        if (j == i || (!full && j != 0))
                        {
                            continue;
                        }

                        _cancel.ThrowIfCancellationRequested();
                        var trial = new List<Work>(order);
                        var moved = trial[i];
                        trial.RemoveAt(i);
                        trial.Insert(j, moved);
                        var saved = Save(order);
                        foreach (var w in order)
                        {
                            Release(w);
                        }

                        foreach (var w in trial)
                        {
                            FillGoal(w);
                        }

                        var score = Score();
                        if (score.Shortfall < best.Shortfall - Tolerance || (Math.Abs(score.Shortfall - best.Shortfall) <= Tolerance && score.Cost < best.Cost - 1e-9))
                        {
                            best = score;
                            order = trial;
                            improved = true;
                        }
                        else
                        {
                            Restore(saved, order);
                        }
                    }
                }

                if (!improved)
                {
                    break;
                }
            }
        }

        /// <summary>Up to this many goals every order is tried (4! = 24 fills); above it the single moves below.</summary>
        private const int MaxPermutedGoals = 4;

        private static IEnumerable<List<Work>> Permutations(List<Work> items)
        {
            // Lexicographic by position in the current order, so the result is a function of the order and not of anything else.
            var indexes = Enumerable.Range(0, items.Count).ToArray();
            IEnumerable<int[]> Recurse(int[] prefix, List<int> rest)
            {
                if (rest.Count == 0)
                {
                    yield return prefix;
                    yield break;
                }

                foreach (var index in rest.ToList())
                {
                    var next = rest.Where(i => i != index).ToList();
                    foreach (var permutation in Recurse([.. prefix, index], next))
                    {
                        yield return permutation;
                    }
                }
            }

            foreach (var permutation in Recurse([], [.. indexes]))
            {
                yield return [.. permutation.Select(i => items[i])];
            }
        }

        /// <summary>Above this many goals only "first call" moves are tried: the work grows with the square of the goal count, and no installation has this many.</summary>
        private const int MaxReorderedGoals = 8;

        private (double Shortfall, double Cost) Score() => (_allWorks.Sum(w => w.Shortfall), _allWorks.Sum(w => w.Cost));

        private List<Work> _allWorks = [];

        private (State State, List<Alloc>[] Allocs, bool[] Escalated) Save(List<Work> works) =>
            (_state.Clone(), [.. works.Select(w => new List<Alloc>(w.Allocs))], [.. works.Select(w => w.Escalated)]);

        private void Restore((State State, List<Alloc>[] Allocs, bool[] Escalated) saved, List<Work> works)
        {
            _state = saved.State;
            for (var i = 0; i < works.Count; i++)
            {
                works[i].Allocs = saved.Allocs[i];
                works[i].Escalated = saved.Escalated[i];
            }
        }

        // References ----------------------------------------------------------------------------------------------------------------------

        /// <summary>The same goals delivered as soon as possible, on what the devices' own sources allow (v2 on its own) or on any energy, from fresh capacity.</summary>
        private double Reference(List<Work> ordered, bool ownSources)
        {
            var state = Fresh();
            var total = 0.0;
            foreach (var w in ordered)
            {
                var sources = ownSources ? w.Device.Sources ?? SourceSet.Any : SourceSet.Any;
                var cells = Cells(w, anyZone: false, valueTest: false, sources).OrderBy(c => c.Slot).ThenBy(c => c.Pv ? 0 : 1).ThenBy(c => Key(c.Cost)).ThenBy(c => c.Zone, StringComparer.Ordinal).ToList();
                var into = new List<Alloc>();
                Fill(state, w, cells, w.Need, into, urgent: false);
                total += into.Sum(a => a.Kwh * a.Cost);
            }

            return total;
        }

        // Result --------------------------------------------------------------------------------------------------------------------------

        private ImmutableArray<DevicePlan> Devices(List<Work> works)
        {
            var result = ImmutableArray.CreateBuilder<DevicePlan>();
            foreach (var device in _devices.GroupBy(d => d.Id).Select(g => g.First()))
            {
                var mine = works.Where(w => w.Device.Id == device.Id).OrderBy(w => w.Request.Deadline is null).ThenBy(w => w.Request.Deadline).ThenByDescending(w => w.Hard).ThenBy(w => w.Request.Revision).ToList();
                result.Add(new DevicePlan(device.Id, [.. mine.Select(w => Goal(w, device))], Entries(device, mine)));
            }

            return result.ToImmutable();
        }

        private PlanGoal Goal(Work w, PlanDevice device)
        {
            var status = w.Met ? GoalStatus.Met
                : w.Expired ? GoalStatus.Expired
                : w.Missed ? GoalStatus.Missed
                : w.Infeasible ? GoalStatus.Infeasible
                : w.Planned && w.Hard && (w.UrgentLatest || w.Escalated || (w.Shortfall > Tolerance && !w.BeyondHorizon)) ? GoalStatus.AtRisk
                : GoalStatus.OnTrack;
            DateTimeOffset? completion = null;
            if (w.Planned && w.Allocs.Count > 0 && w.Shortfall <= Tolerance)
            {
                var last = w.Allocs.Max(a => a.Slot);
                var kwhThere = w.Allocs.Where(a => a.Slot == last).Sum(a => a.Kwh);
                var ceiling = CeilingW(device, _in.CapacityW) / 1000;
                var end = ceiling > 0 ? _slots[last].Slot.Start + TimeSpan.FromHours(kwhThere / ceiling) : _slots[last].Slot.End;
                completion = end < _slots[last].Slot.End ? end : _slots[last].Slot.End;
            }

            var urgent = w.Planned && (w.Critical || w.Escalated);
            return new PlanGoal(
                new PlanGoalRef(GoalId(w.Request), w.Request.Revision, device.SpecIdentity),
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

        private ImmutableArray<PlanEntry> Entries(PlanDevice device, List<Work> mine)
        {
            var entries = ImmutableArray.CreateBuilder<PlanEntry>(_n);
            var planned = device.CanStart && device.Kind != PlanDeviceKind.Storage ? mine.Where(w => w.Planned).ToList() : [];
            var ceilingW = CeilingW(device, _in.CapacityW);
            var stopMet = mine.Where(w => w.Request.OnMet == GoalOnMet.Stop && (w.Met || (w.Planned && w.Shortfall <= Tolerance))).ToList();
            var lastAlloc = planned.SelectMany(w => w.Allocs).Select(a => (int?)a.Slot).DefaultIfEmpty(null).Max();
            for (var t = 0; t < _n; t++)
            {
                var slot = _slots[t].Slot;
                var inSlot = planned.SelectMany(w => w.Allocs.Where(a => a.Slot == t).Select(a => (Work: w, Alloc: a))).ToList();
                var energy = inSlot.Sum(x => x.Alloc.Kwh);
                if (energy > Tolerance)
                {
                    var urgent = inSlot.Any(x => x.Alloc.Urgent);

                    // One class per entry: any grid energy in the slot makes it a zone entry (dispatch has to open that zone, whatever the surplus share), the dearest zone
                    // if there are several; only an entry that is wholly surplus is pv.
                    var grid = inSlot.Where(x => !x.Alloc.Pv).OrderByDescending(x => Key(x.Alloc.Cost)).ThenBy(x => x.Alloc.Zone, StringComparer.Ordinal).Select(x => x.Alloc).ToList();
                    var entryClass = grid.Count > 0 ? PlanClasses.Zone(grid[0].Zone) : PlanClasses.Pv;
                    var goal = inSlot.GroupBy(x => x.Work).OrderByDescending(g => g.Sum(x => x.Alloc.Kwh)).ThenBy(g => g.Key.Request.Revision).First().Key;
                    entries.Add(new PlanEntry(slot.Start, slot.End, urgent ? PlanWant.Urgent : PlanWant.Run, entryClass, energy, ceilingW, Ref(goal, device)));
                    continue;
                }

                if (lastAlloc is { } last && t < last && planned.FirstOrDefault(w => w.Allocs.Any(a => a.Slot > t)) is { } later)
                {
                    entries.Add(new PlanEntry(slot.Start, slot.End, PlanWant.Defer, null, 0, 0, Ref(later, device)));
                    continue;
                }

                if (stopMet.FirstOrDefault(w => slot.Start < (w.Request.Deadline ?? _in.Inputs.Grid.To)) is { } done)
                {
                    entries.Add(new PlanEntry(slot.Start, slot.End, PlanWant.Done, null, 0, 0, Ref(done, device)));
                    continue;
                }

                entries.Add(new PlanEntry(slot.Start, slot.End, PlanWant.None, null, 0, 0, null));
            }

            return entries.ToImmutable();
        }

        private static PlanGoalRef Ref(Work w, PlanDevice device) => new(GoalId(w.Request), w.Request.Revision, device.SpecIdentity);

        private ImmutableArray<PlanNote> Notes(List<Work> works)
        {
            var notes = new HashSet<PlanNote>(_in.Inputs.Notes);
            if (works.Any(w => w.Planned && w.Shortfall > Tolerance && !w.BeyondHorizon))
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
}
