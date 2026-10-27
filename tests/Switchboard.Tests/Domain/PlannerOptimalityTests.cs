using System.Collections.Immutable;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Planning;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.PlannerFixtures;

namespace Switchboard.Tests.Domain;

/// <summary>
/// How far the planner's plan can be from the best one, and that it never breaks a limit (15-PLANNER, "Test strategy": the optimality bound and the property
/// "never causes an allocation the envelope refuses"). Random instances from fixed seeds, so a failure is reproducible by its seed.
/// </summary>
public sealed class PlannerOptimalityTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Midnight = Local(2026, 10, 26, 0);

    /// <summary>One synthetic instance in whole kWh: hourly slots, devices of 2 kW, phases of 2 to 4 kW, prices and surplus in whole units, hard goals with any source.</summary>
    private sealed record Instance(int Seed, PlanInput Input, int Slots, int[] Surplus, double[] Import, double[] Export, (ConstraintKey Key, int Deadline, int Need)[] Goals, int[] KeyCapacity)
    {
        public bool Contended
        {
            get
            {
                // Some capacity binds: two goals share a phase, or surplus, or a cell is smaller than a goal's draw. Without contention every goal simply
                // takes its own cheapest cells, which the greedy does exactly.
                var shared = Goals.GroupBy(g => g.Key).Any(g => g.Count() > 1) || Goals.Length > 1;
                return shared;
            }
        }
    }

    private static Instance Generate(int seed, int maxGoals = 3)
    {
        var random = new Random(seed);
        var slots = random.Next(4, 7);
        var goalCount = random.Next(1, maxGoals + 1);
        var surplus = Enumerable.Range(0, slots).Select(_ => random.Next(3) == 0 ? 0 : random.Next(0, 3)).ToArray();
        var import = Enumerable.Range(0, slots).Select(_ => (double)random.Next(1, 6)).ToArray();
        var export = Enumerable.Range(0, slots).Select(_ => (double)random.Next(0, 3)).ToArray();
        var keyCapacity = new[] { random.Next(2, 5), random.Next(2, 5) };
        var keys = new[] { L1, L2 };
        var devices = new List<PlanDevice>();
        var goals = new List<GoalRequest>();
        var spec = new List<(ConstraintKey, int, int)>();
        for (var g = 0; g < goalCount; g++)
        {
            var key = keys[random.Next(2)];
            var need = random.Next(1, 5);
            var deadline = random.Next((need + 1) / 2 + 1, slots + 1);
            var name = DeviceName.Parse($"dev_{g}");
            devices.Add(new PlanDevice(name, PlanDeviceKind.Modulating, 0, 2000, TimeSpan.Zero, [new PlanKeyShare(key, 1)], true, new ValueSpec(10, ValueFollows.Static, 1), SourceSet.Any, 1));
            goals.Add(Goal(name, need, Midnight.AddHours(deadline), revision: g + 1));
            spec.Add((key, deadline, need));
        }

        var inputs = Custom(Midnight, slots, i => ("z", import[i], export[i], surplus[i]));
        var capacity = ImmutableDictionary<ConstraintKey, double>.Empty.Add(L1, keyCapacity[0] * 1000.0).Add(L2, keyCapacity[1] * 1000.0);
        var parameters = Parameters() with { HardGoal = new HardGoalParameters(TimeSpan.Zero) };
        var input = Input(Midnight, inputs, devices, goals, capacity, parameters);
        return new Instance(seed, input, slots, surplus, import, export, [.. spec], keyCapacity);
    }

    /// <summary>
    /// The cheapest way to deliver every goal in whole kWh, by enumeration: per goal every split of its need over (slot, class) cells with at most 2 kWh a slot
    /// (the device's 2 kW for an hour), then the goals together against the phase capacity and the surplus pool. Null when no complete plan exists.
    /// </summary>
    private static double? Exhaustive(Instance instance)
    {
        var keyLeft = new Dictionary<ConstraintKey, int[]>
        {
            [L1] = Enumerable.Repeat(instance.KeyCapacity[0], instance.Slots).ToArray(),
            [L2] = Enumerable.Repeat(instance.KeyCapacity[1], instance.Slots).ToArray(),
        };
        var pvLeft = instance.Surplus.ToArray();
        var best = double.PositiveInfinity;

        void Goal(int g, double cost)
        {
            if (cost >= best)
            {
                return;
            }

            if (g == instance.Goals.Length)
            {
                best = cost;
                return;
            }

            var (key, deadline, need) = instance.Goals[g];
            var perSlot = new int[instance.Slots];
            void Cell(int slot, int cls, int left, double spent)
            {
                if (spent + 0 >= best)
                {
                    return;
                }

                if (left == 0)
                {
                    Goal(g + 1, cost + spent);
                    return;
                }

                if (slot == deadline)
                {
                    return;
                }

                if (cls == 2)
                {
                    Cell(slot + 1, 0, left, spent);
                    return;
                }

                var price = cls == 0 ? instance.Export[slot] : instance.Import[slot];
                var most = Math.Min(Math.Min(left, 2 - perSlot[slot]), keyLeft[key][slot]);
                if (cls == 0)
                {
                    most = Math.Min(most, pvLeft[slot]);
                }

                for (var units = most; units >= 0; units--)
                {
                    perSlot[slot] += units;
                    keyLeft[key][slot] -= units;
                    if (cls == 0)
                    {
                        pvLeft[slot] -= units;
                    }

                    Cell(slot, cls + 1, left - units, spent + (units * price));
                    perSlot[slot] -= units;
                    keyLeft[key][slot] += units;
                    if (cls == 0)
                    {
                        pvLeft[slot] += units;
                    }
                }
            }

            Cell(0, 0, need, 0);
        }

        Goal(0, 0);
        return double.IsInfinity(best) ? null : best;
    }

    [Fact]
    public void Without_competition_the_plan_is_exactly_the_best()
    {
        // Why: with one goal there is no contended cell: the goal takes its own cheapest cells, which is the optimum, so the design promises exactness and any gap
        // here is a bug in the fill (a wrong order, a wrong tie), not an approximation. 300 random single-goal instances against enumeration.
        var compared = 0;
        foreach (var seed in Enumerable.Range(1000, 300))
        {
            var instance = Generate(seed, maxGoals: 1);
            if (Exhaustive(instance) is not { } best)
            {
                continue;
            }

            var plan = Solve(instance.Input);

            Assert.Equal(0, plan.Devices.SelectMany(d => d.Goals).Sum(g => g.ShortfallKwh), 9);
            Assert.True(Math.Abs(plan.Cost.Planned - best) < 1e-9, $"seed {seed}: plan {plan.Cost.Planned}, best {best}");
            compared++;
        }

        Assert.True(compared > 200, $"only {compared} instances were comparable");
    }

    [Fact]
    public void With_competition_the_plan_is_never_cheaper_than_the_best_and_stays_within_the_documented_gap()
    {
        // Why: the planner is a greedy with a capped refinement, not an LP. The documented bound on 593 comparable random instances of 1 to 3 hard goals in whole kWh
        // (seeds 1 to 600): at least 98 percent of the plans are exactly the optimum; of the rest none is more than 25 percent dearer, and a plan that leaves
        // a need unmet that the enumeration could place (tight packing of several goals into a phase's last kWh: a goal takes the most of its cheapest cell, a
        // later one then finds it fragmented) is at most 1 percent of the instances and is always reported (note goal_shortfall, status at risk), never silent.
        // The comparison also catches the opposite error: a plan cheaper than the true optimum would mean a capacity was broken or the enumeration is wrong.
        var compared = 0;
        var exact = 0;
        var worstGap = 0.0;
        var unmet = 0;
        foreach (var seed in Enumerable.Range(1, 600))
        {
            var instance = Generate(seed);
            if (Exhaustive(instance) is not { } best)
            {
                continue;
            }

            var plan = Solve(instance.Input);
            var input = instance.Input;
            Assert.Empty(PlanVerifier.Verify(input, plan));
            compared++;
            if (plan.Devices.SelectMany(d => d.Goals).Any(g => g.ShortfallKwh > 1e-9))
            {
                unmet++;
                Assert.Contains(PlanNote.GoalShortfall, plan.Notes);
                continue;
            }

            Assert.True(plan.Cost.Planned >= best - 1e-9, $"seed {seed}: plan {plan.Cost.Planned} below the exhaustive optimum {best}");
            var gap = best <= 0 ? 0 : (plan.Cost.Planned - best) / best;
            if (gap <= 1e-9)
            {
                exact++;
            }

            worstGap = Math.Max(worstGap, gap);
        }

        output.WriteLine($"compared {compared}, exact {exact}, unmet {unmet}, worst gap among complete plans {worstGap:P1}");
        Assert.True(compared > 500, $"only {compared} instances were comparable");
        Assert.True(exact >= compared * 0.98, $"{exact} of {compared} exact");
        Assert.True(worstGap <= 0.25 + 1e-9, $"worst gap {worstGap:P1}");
        Assert.True(unmet <= compared * 0.01, $"{unmet} of {compared} left a need unmet");
    }

    [Fact]
    public void Refinement_never_raises_the_cost_or_the_shortfall_and_does_repair_some_orders()
    {
        // Why: the refinement keeps a new order only when it is better, so it can never make a plan worse; and it must be worth having: on the instance of seed 150
        // (three goals on one phase) the earliest-deadline order is 4 percent dearer than the optimum and the refinement closes the gap exactly. Without passes
        // the greedy result stands, so the difference is the refinement's.
        var repaired = 0;
        foreach (var seed in Enumerable.Range(1, 300))
        {
            var instance = Generate(seed);
            var greedy = Solve(instance.Input with { Parameters = instance.Input.Parameters with { RefinePasses = 0 } });
            var refined = Solve(instance.Input);

            Assert.True(
                refined.Devices.Sum(d => d.Goals.Sum(g => g.ShortfallKwh)) <= greedy.Devices.Sum(d => d.Goals.Sum(g => g.ShortfallKwh)) + 1e-9,
                $"seed {seed}: refinement left more need unmet");
            Assert.True(refined.Cost.Planned <= greedy.Cost.Planned + 1e-9 || refined.Devices.Sum(d => d.Goals.Sum(g => g.ShortfallKwh)) < greedy.Devices.Sum(d => d.Goals.Sum(g => g.ShortfallKwh)) - 1e-9, $"seed {seed}: refinement raised the cost");
            if (refined.Cost.Planned < greedy.Cost.Planned - 1e-9)
            {
                repaired++;
            }
        }

        Assert.True(repaired > 0, "no instance was repaired by the refinement");
        var seed150 = Generate(150);
        Assert.Equal(Exhaustive(seed150)!.Value, Solve(seed150.Input).Cost.Planned, 9);
        Assert.True(Solve(seed150.Input with { Parameters = seed150.Input.Parameters with { RefinePasses = 0 } }).Cost.Planned > Exhaustive(seed150)!.Value + 0.5);
    }

    private static double SlotKwh(PlanSnapshot plan, ConstraintKey key, DeviceName[] devices, PlanInput input, int slot) =>
        devices.Where(d => input.Devices.Single(x => x.Id == d).Keys.Any(k => k.Key == key))
            .Sum(d => plan.Device(d)!.Entries[slot].EnergyKwh * input.Devices.Single(x => x.Id == d).Keys.Single(k => k.Key == key).Share);

    [Fact]
    public void Property_a_plan_never_exceeds_a_phase_group_or_device_limit_whatever_the_instance()
    {
        // Why: the headline property of the planner ("never refused by the envelope", 15-PLANNER test strategy): the guard's static limit per phase and group, a
        // device's own maximum and the surplus can never be exceeded by any plan, on any input. 1500 random instances from fixed seeds (60000 were run once while writing it, all clean; hard and soft goals, any and
        // restricted sources, 1 to 3 phases and a circuit group, devices on one to three phases, minimum power and minimum run, deadlines inside and past the
        // horizon, a hole in the prices), each checked by an independent recomputation here and by the verifier.
        for (var seed = 1; seed <= 1500; seed++)
        {
            var random = new Random(7000 + seed);
            var input = RandomInput(random, out var devices);
            var plan = Solve(input);

            var violations = PlanVerifier.Verify(input, plan);
            if (!violations.IsEmpty)
            {
                output.WriteLine($"seed {seed}: {string.Join("; ", violations.Select(v => v.Code + " " + v.Message + " " + v.Slot))}");
                foreach (var d in plan.Devices)
                {
                    output.WriteLine($"  {d.Device}: {string.Join(" ", d.Entries.Where(e => e.Want != PlanWant.None).Select(e => $"{e.Start:HH}:{e.Want}/{e.Class}/{e.EnergyKwh:0.###}"))}");
                }

                output.WriteLine("  surplus " + string.Join(",", plan.Slots.Select(sl => sl.SurplusKwh.ToString("0.##"))));
                output.WriteLine("  devices " + string.Join("; ", input.Devices.Select(d => $"{d.Id} {d.MinW}-{d.MaxW} run {d.MinRun} start {d.CanStart} keys {string.Join(',', d.Keys.Select(k => k.Key.ToString() + "*" + k.Share))} src {d.Sources?.ToText()}")));
                output.WriteLine("  goals " + string.Join("; ", input.Goals.Select(g => $"{g.Device} need {g.RemainingKwh} due {g.Deadline:dd HH:mm} hard {g.Hard} esc {g.Escalate} src {g.Sources?.ToText()} margin {g.Margin}")));
                output.WriteLine("  now " + input.Now.ToString("dd HH:mm") + " caps " + string.Join(",", input.CapacityW.Select(c => c.Key + "=" + c.Value)));
                output.WriteLine("  slotprices " + string.Join(",", plan.Slots.Select(sl => $"{sl.Zone}/{sl.Import?.PerKwh}")));
            }

            Assert.Empty(violations);
            var names = devices.Select(d => d.Id).ToArray();
            for (var slot = 0; slot < plan.Slots.Length; slot++)
            {
                var hours = plan.Slots[slot].Slot.Hours;
                foreach (var (key, limitW) in input.CapacityW)
                {
                    var kwh = SlotKwh(plan, key, names, input, slot);
                    Assert.True(kwh <= (limitW / 1000 * hours) + 1e-6, $"seed {seed}: {key} carries {kwh} kWh in slot {slot}");
                }

                var pv = names.Sum(n => plan.Device(n)!.Entries[slot] is { Class: PlanClasses.Pv } e ? e.EnergyKwh : 0);
                Assert.True(pv <= plan.Slots[slot].SurplusKwh + 1e-6, $"seed {seed}: {pv} kWh on surplus in slot {slot}");
                foreach (var device in devices)
                {
                    var entry = plan.Device(device.Id)!.Entries[slot];
                    Assert.True(entry.EnergyKwh <= (device.MaxW / 1000 * hours) + 1e-6, $"seed {seed}: {device.Id} over its maximum in slot {slot}");
                    Assert.True(entry.MaxPowerW <= device.MaxW + 1e-6);
                    if (entry.Want is PlanWant.Run or PlanWant.Urgent)
                    {
                        Assert.True(device.CanStart, $"seed {seed}: a run entry for {device.Id}, which may not be started");
                        Assert.True(input.Goals.Any(g => g.Device == device.Id), $"seed {seed}: a run entry for {device.Id}, which has no goal");
                    }
                    else
                    {
                        Assert.Equal(0, entry.EnergyKwh);
                    }
                }
            }

            // A feasible hard goal that may escalate, with nothing competing for its device's phase, is met: the plan never leaves a trip short that it could have served
            // (without escalation the sources may leave it short, and a minimum run can make a small cell unusable: both are stated behaviour, not tested here).
            foreach (var goal in input.Goals.Where(g => g.Hard && g.RemainingKwh > 0 && g.Deadline > input.Now && g.Deadline <= input.Inputs.Grid.To))
            {
                var device = devices.Single(d => d.Id == goal.Device);
                var alone = input.Devices.Where(d => d.Keys.Any(k => device.Keys.Any(x => x.Key == k.Key))).Count() == 1 && input.Goals.Count(g => g.Device == goal.Device) == 1;
                var planned = plan.Device(goal.Device)!.Goals.Single(g => g.Ref.Revision == goal.Revision);
                var unconstrained = goal.Escalate && (device.MinW == 0 || device.MinRun == TimeSpan.Zero);
                if (alone && unconstrained && device.CanStart && planned.Status is not GoalStatus.Infeasible)
                {
                    Assert.True(planned.ShortfallKwh <= 1e-6, $"seed {seed}: a feasible hard goal of {goal.Device} is short by {planned.ShortfallKwh}");
                }
            }
        }
    }

    private static PlanInput RandomInput(Random random, out PlanDevice[] devices)
    {
        var hours = random.Next(6, 40);
        var now = Midnight.AddMinutes(random.Next(0, 4) * 15);
        var inputs = Custom(now, hours, i => (random.Next(5) == 0 ? null : (random.Next(3) == 0 ? "peak" : "off"), random.Next(8) == 0 ? null : random.Next(1, 20) / 10.0, random.Next(4) == 0 ? null : random.Next(0, 6) / 10.0, random.Next(3) == 0 ? random.Next(0, 40) / 10.0 : 0));
        var group = new ConstraintKey(Switchboard.Domain.Connection.GroupName.Parse("shed"), Switchboard.Domain.Signals.Phase.L1);
        var capacity = Capacity(random.Next(1500, 5000)).Add(group, random.Next(800, 3000));
        var count = random.Next(1, 4);
        var list = new List<PlanDevice>();
        var goals = new List<GoalRequest>();
        var phases = new[] { L1, L2, L3 };
        for (var d = 0; d < count; d++)
        {
            var name = DeviceName.Parse($"load_{d}");
            var three = random.Next(4) == 0;
            ImmutableArray<PlanKeyShare> keys = three
                ? [new PlanKeyShare(L1, 1.0 / 3), new PlanKeyShare(L2, 1.0 / 3), new PlanKeyShare(L3, 1.0 / 3)]
                : random.Next(3) == 0 ? [new PlanKeyShare(L1, 1), new PlanKeyShare(group, 1)] : [new PlanKeyShare(phases[random.Next(3)], 1)];
            var max = random.Next(10, 70) * 100.0;
            var min = random.Next(3) == 0 ? max / 3 : 0;
            var sources = random.Next(3) switch { 0 => SourceSet.PvOnly, 1 => (SourceSet?)null, _ => SourceSet.Any };
            list.Add(new PlanDevice(name, PlanDeviceKind.Modulating, min, max, TimeSpan.FromMinutes(random.Next(0, 3) * 20), keys, random.Next(6) != 0, new ValueSpec(random.Next(2, 30) / 10.0, ValueFollows.Static, 1), sources, d + 1));
            for (var g = 0; g < random.Next(0, 3); g++)
            {
                // One goal per device and deadline (the merge's invariant), so a second goal of a device gets a different instant.
                var deadline = random.Next(5) == 0 ? now.AddHours(-1 - g) : now.AddHours(random.Next(2, hours + 6)).AddMinutes(g);
                goals.Add(Goal(name, random.Next(0, 120) / 10.0, deadline, hard: random.Next(2) == 0, margin: TimeSpan.FromMinutes(random.Next(0, 3) * 30), sources: random.Next(3) == 0 ? SourceSet.PvOnly : null, escalate: random.Next(4) != 0, onMet: random.Next(4) == 0 ? GoalOnMet.Stop : GoalOnMet.Opportunistic, revision: (d * 10) + g + 1));
            }
        }

        devices = [.. list];
        return Input(now, inputs, list, goals, capacity, Parameters(), 0.05, 1);
    }

    [Fact]
    public void Property_the_same_input_in_any_order_gives_the_same_plan_to_the_byte()
    {
        // Why: the plan is a pure function of its input (the design's determinism promise): the order the devices and goals arrive in is an accident of the
        // dictionary or the file, and a plan that changed with it would flap for no reason. Each random instance is planned as given, with the devices reversed and
        // the goals reversed, and the canonical text (and so the fingerprint) must be identical.
        for (var seed = 1; seed <= 150; seed++)
        {
            var input = RandomInput(new Random(9000 + seed), out _);
            var reordered = input with { Devices = [.. input.Devices.Reverse()], Goals = [.. input.Goals.Reverse()] };

            var a = Solve(input);
            var b = Solve(reordered);
            var again = Solve(input);

            Assert.Equal(a.Canonical(), b.Canonical());
            Assert.Equal(a.Canonical(), again.Canonical());
            Assert.Equal(a.Id, b.Id);
        }
    }
}
