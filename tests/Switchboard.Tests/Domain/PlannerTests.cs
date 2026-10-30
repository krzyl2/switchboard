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
/// The pure planner's behaviour on small hand-made instances (15-PLANNER, "The planning algorithm"). Each test states why the case matters: the planner decides
/// when energy is wanted, so a wrong class, a missed deadline or a limit overrun here is a wrong decision everywhere downstream.
/// </summary>
public sealed class PlannerTests
{
    private static readonly DateTimeOffset Midnight = Local(2026, 10, 26, 0);
    private static readonly DeviceName DeviceA = DeviceName.Parse("device_a");
    private static readonly DeviceName DeviceB = DeviceName.Parse("device_b");

    private static PlanDevice Dev(DeviceName name, ConstraintKey key, double maxW = 2000, double minW = 0, TimeSpan? minRun = null, SourceSet? sources = null, bool canStart = true, double value = 10, ImmutableArray<PlanKeyShare>? keys = null) =>
        new(name, PlanDeviceKind.Modulating, minW, maxW, minRun ?? TimeSpan.Zero, keys ?? [new PlanKeyShare(key, 1)], canStart, new ValueSpec(value, ValueFollows.Static, 1), sources, 1);

    private static PlanInput Make(PlanInputs inputs, IEnumerable<PlanDevice> devices, IEnumerable<GoalRequest> goals, ImmutableDictionary<ConstraintKey, double>? capacity = null, PlannerParameters? parameters = null, double minMargin = 0.05) =>
        Input(Midnight, inputs, devices, goals, capacity ?? Capacity(4000), parameters, minMargin);

    private static PlanInputs Flat(int slots, double import = 1, double export = 0.5, double surplus = 0, string? zone = "z", DateTimeOffset? now = null) =>
        Custom(now ?? Midnight, slots, _ => (zone, import, export, surplus));

    private static IReadOnlyList<PlanEntry> Runs(PlanSnapshot plan, DeviceName device) =>
        [.. plan.Device(device)!.Entries.Where(e => e.Want is PlanWant.Run or PlanWant.Urgent)];

    [Fact]
    public void A_hard_goal_that_its_sources_cannot_meet_escalates_to_a_zone_and_is_urgent()
    {
        // Why: a hard deadline outranks the device's own sources (15-PLANNER step 5): pv-only with no PV would leave the trip unmet, so the plan adds the grid zone,
        // marks the entries urgent (dispatch will treat them as such) and says the goal is at risk. Nothing is hidden: status at risk, note goal_urgent.
        var inputs = Flat(10);
        var goal = Goal(DeviceA, 4, Midnight.AddHours(6));
        var input = Make(inputs, [Dev(DeviceA, L1, sources: SourceSet.PvOnly)], [goal]);

        var plan = Solve(input);

        var runs = Runs(plan, DeviceA);
        Assert.All(runs, e => Assert.Equal(PlanWant.Urgent, e.Want));
        Assert.All(runs, e => Assert.Equal("zone:z", e.Class));
        Assert.Equal(4, runs.Sum(e => e.EnergyKwh), 9);
        var planned = plan.Device(DeviceA)!.Goals.Single();
        Assert.Equal(GoalStatus.AtRisk, planned.Status);
        Assert.True(planned.Urgent);
        Assert.Contains(PlanNote.GoalUrgent, plan.Notes);
        Assert.Empty(PlanVerifier.Verify(input, plan));
    }

    [Fact]
    public void A_hard_goal_that_may_not_escalate_reports_the_shortfall_and_never_leaves_its_sources()
    {
        // Why: escalate: false is the user's word that the grid is not to be bought for this goal; the plan then reports the shortfall instead of stepping outside the
        // sources. The shortfall is shown (status at risk, note goal_shortfall) and nothing is placed on a zone the goal did not list.
        var input = Make(Flat(10), [Dev(DeviceA, L1, sources: SourceSet.PvOnly)], [Goal(DeviceA, 4, Midnight.AddHours(6), escalate: false)]);

        var plan = Solve(input);

        var goal = plan.Device(DeviceA)!.Goals.Single();
        Assert.Equal(4, goal.ShortfallKwh, 9);
        Assert.Equal(0, goal.PlannedKwh, 9);
        Assert.Empty(Runs(plan, DeviceA));
        Assert.Equal(GoalStatus.AtRisk, goal.Status);
        Assert.Contains(PlanNote.GoalShortfall, plan.Notes);
        Assert.Empty(PlanVerifier.Verify(input, plan));
    }

    [Fact]
    public void A_hard_goal_that_cannot_be_met_even_at_full_power_is_infeasible_and_gets_everything_from_now()
    {
        // Why: 9.9 kWh in two hours at 2 kW is 4 kWh at best. The plan must not pretend: it schedules the most it can from now, urgent, says infeasible and shows the
        // 5.9 kWh shortfall, so dispatch starts at once and the user hears about it.
        var input = Make(Flat(10), [Dev(DeviceA, L1)], [Goal(DeviceA, 9.9, Midnight.AddHours(2))]);

        var plan = Solve(input);

        var goal = plan.Device(DeviceA)!.Goals.Single();
        Assert.Equal(GoalStatus.Infeasible, goal.Status);
        Assert.Equal(4, goal.PlannedKwh, 9);
        Assert.Equal(5.9, goal.ShortfallKwh, 9);
        Assert.Equal([Midnight, Midnight.AddHours(1)], Runs(plan, DeviceA).Select(e => e.Start).ToArray());
        Assert.All(Runs(plan, DeviceA), e => Assert.Equal(PlanWant.Urgent, e.Want));
        Assert.Contains(PlanNote.GoalInfeasible, plan.Notes);
        Assert.Empty(PlanVerifier.Verify(input, plan));
    }

    [Fact]
    public void A_hard_goal_past_its_deadline_is_missed_and_still_pursued_as_urgent()
    {
        // Why: 15-PLANNER: a missed hard goal is shown and still pursued until it is met or removed; the deadline has gone, so the plan runs from now on any energy
        // and the status is missed (never quietly dropped).
        var deadline = Midnight.AddHours(-1);
        var input = Make(Flat(10), [Dev(DeviceA, L1)], [Goal(DeviceA, 3, deadline)]);

        var plan = Solve(input);

        var goal = plan.Device(DeviceA)!.Goals.Single();
        Assert.Equal(GoalStatus.Missed, goal.Status);
        Assert.Equal(3, goal.PlannedKwh, 9);
        Assert.Equal(PlanWant.Urgent, Runs(plan, DeviceA)[0].Want);
        Assert.Equal(Midnight, Runs(plan, DeviceA)[0].Start);
        Assert.Contains(PlanNote.GoalMissed, plan.Notes);
    }

    [Fact]
    public void A_soft_goal_past_its_deadline_lapses_and_plans_nothing()
    {
        // Why: a soft goal is not pursued after its deadline (it lapses): status expired, no entry asks for energy, the device is a v2 opportunist again.
        var input = Make(Flat(10), [Dev(DeviceA, L1)], [Goal(DeviceA, 3, Midnight.AddHours(-1), hard: false)]);

        var plan = Solve(input);

        Assert.Equal(GoalStatus.Expired, plan.Device(DeviceA)!.Goals.Single().Status);
        Assert.Empty(Runs(plan, DeviceA));
        Assert.All(plan.Device(DeviceA)!.Entries, e => Assert.Equal(PlanWant.None, e.Want));
    }

    [Fact]
    public void A_soft_goal_uses_only_the_classes_that_are_worth_their_price()
    {
        // Why: a soft goal is value-tested (v_eff - cost >= min_margin). Value 0.60: surplus at export 0.35 is worth it, the grid at 1.00 is not, so the plan takes
        // the 1.5 kWh of surplus, leaves 2.5 kWh unplaced and never buys the rest.
        var inputs = Custom(Midnight, 8, i => ("z", 1.0, 0.35, i is 2 or 3 ? 0.75 : 0));
        var input = Make(inputs, [Dev(DeviceA, L1, value: 0.60)], [Goal(DeviceA, 4, Midnight.AddHours(8), hard: false)]);

        var plan = Solve(input);

        var goal = plan.Device(DeviceA)!.Goals.Single();
        Assert.Equal(1.5, goal.PlannedKwh, 9);
        Assert.Equal(2.5, goal.ShortfallKwh, 9);
        Assert.All(Runs(plan, DeviceA), e => Assert.Equal(PlanClasses.Pv, e.Class));
        Assert.Equal(GoalStatus.OnTrack, goal.Status);
        Assert.Contains(PlanNote.GoalShortfall, plan.Notes);
    }

    [Fact]
    public void Cheaper_energy_comes_first_and_equal_cost_takes_the_earliest_slot()
    {
        // Why: the fill is by ascending cost with the tie going to the earliest slot (robust against an under-delivery). Slots 4 and 5 are cheap (0.2): the goal needs
        // 3 kWh at 2 kW, so it takes slot 4 fully and slot 5 for the remaining 1 kWh, not the dearer early slots.
        var inputs = Custom(Midnight, 8, i => ("z", i is 4 or 5 ? 0.2 : 1.0, 0.1, 0));
        var input = Make(inputs, [Dev(DeviceA, L1)], [Goal(DeviceA, 3, Midnight.AddHours(8))]);

        var plan = Solve(input);

        var runs = Runs(plan, DeviceA);
        Assert.Equal([Midnight.AddHours(4), Midnight.AddHours(5)], runs.Select(e => e.Start).ToArray());
        Assert.Equal([2.0, 1.0], runs.Select(e => e.EnergyKwh).ToArray());
        Assert.Equal(0.6, plan.Cost.Planned, 9);
    }

    [Fact]
    public void A_goal_never_plans_beyond_its_deadline_minus_the_margin()
    {
        // Why: the plan aims to finish the margin before the deadline (the car ready, not just charged by the minute). With margin 1 h and deadline at slot 6 nothing
        // is placed at or after slot 5, even though slot 5 is the cheapest.
        var inputs = Custom(Midnight, 8, i => ("z", i == 5 ? 0.1 : 1.0, 0.1, 0));
        var goal = Goal(DeviceA, 2, Midnight.AddHours(6), margin: TimeSpan.FromHours(1));
        var input = Make(inputs, [Dev(DeviceA, L1)], [goal]);

        var plan = Solve(input);

        Assert.All(Runs(plan, DeviceA), e => Assert.True(e.End <= Midnight.AddHours(5)));
        Assert.Equal(2, plan.Device(DeviceA)!.Goals.Single().PlannedKwh, 9);
    }

    [Fact]
    public void Two_devices_share_one_phase_and_the_plan_stays_inside_its_limit()
    {
        // Why: the static limit per phase is shared by every device on it: two 2 kW devices on one 3 kW phase can never both run flat out; the plan splits the
        // hours so that no slot exceeds 3 kWh on L1.
        var devices = new[] { Dev(DeviceA, L1), Dev(DeviceB, L1) };
        var goals = new[] { Goal(DeviceA, 4, Midnight.AddHours(4)), Goal(DeviceB, 4, Midnight.AddHours(4)) };
        var input = Make(Flat(6), devices, goals, Capacity(3000));

        var plan = Solve(input);

        foreach (var slot in plan.Slots)
        {
            var total = new[] { DeviceA, DeviceB }.Sum(d => plan.Device(d)!.Entries.Single(e => e.Start == slot.Slot.Start).EnergyKwh);
            Assert.True(total <= 3 + 1e-9, $"{total} kWh on L1 at {slot.Slot.Start:t}");
        }

        Assert.Equal(4, plan.Device(DeviceA)!.Goals.Single().PlannedKwh, 9);
        Assert.Equal(4, plan.Device(DeviceB)!.Goals.Single().PlannedKwh, 9);
        Assert.Empty(PlanVerifier.Verify(input, plan));
    }

    [Fact]
    public void A_three_phase_device_loads_a_third_of_its_power_on_each_phase()
    {
        // Why: three_phase = 1/3 per phase. A 6 kW three-phase device on three 1 kW phases can only draw 3 kW, and the plan's ceiling says so; its slot energy never
        // exceeds 3 kWh.
        var keys = ImmutableArray.Create(new PlanKeyShare(L1, 1.0 / 3), new PlanKeyShare(L2, 1.0 / 3), new PlanKeyShare(L3, 1.0 / 3));
        var device = Dev(DeviceA, L1, maxW: 6000, keys: keys);
        var input = Make(Flat(6), [device], [Goal(DeviceA, 5, Midnight.AddHours(6))], Capacity(1000));

        var plan = Solve(input);

        var runs = Runs(plan, DeviceA);
        Assert.All(runs, e => Assert.True(e.EnergyKwh <= 3 + 1e-9));
        Assert.All(runs, e => Assert.Equal(3000, e.MaxPowerW, 6));
        Assert.Equal(5, runs.Sum(e => e.EnergyKwh), 9);
        Assert.Empty(PlanVerifier.Verify(input, plan));
    }

    [Fact]
    public void A_circuit_group_limit_binds_together_with_the_main_phase()
    {
        // Why: a device behind a downstream breaker is limited by the group's phase as well as the main phase (the same ConstraintKey dispatch uses). Main 4 kW,
        // group 1.5 kW: the device's ceiling is the lower, 1.5 kW.
        var group = new ConstraintKey(Switchboard.Domain.Connection.GroupName.Parse("carport"), Switchboard.Domain.Signals.Phase.L1);
        var capacity = Capacity(4000).Add(group, 1500);
        var device = Dev(DeviceA, L1, keys: [new PlanKeyShare(L1, 1), new PlanKeyShare(group, 1)]);
        var input = Make(Flat(8), [device], [Goal(DeviceA, 4, Midnight.AddHours(8))], capacity);

        var plan = Solve(input);

        Assert.All(Runs(plan, DeviceA), e => Assert.True(e.EnergyKwh <= 1.5 + 1e-9));
        Assert.All(Runs(plan, DeviceA), e => Assert.Equal(1500, e.MaxPowerW, 6));
        Assert.Empty(PlanVerifier.Verify(input, plan));
    }

    [Fact]
    public void A_cell_too_small_for_the_minimum_power_and_minimum_run_is_skipped_unless_it_is_the_last_residual()
    {
        // Why: a device that is on draws at least its minimum for at least min_run. Minimum 1.5 kW for one hour needs 1.5 kWh per run; the 0.4 kWh surplus cell cannot
        // hold that and is skipped, the grid cells are used instead. A residual smaller than the minimum (the last bit of the goal) is placed anyway: dispatch's
        // min_on rounds it up, and the goal must not stay short by 0.2 kWh.
        var inputs = Custom(Midnight, 6, i => ("z", 1.0, 0.1, i == 0 ? 0.4 : 0));
        var device = Dev(DeviceA, L1, maxW: 3000, minW: 1500, minRun: TimeSpan.FromHours(1));
        var input = Make(inputs, [device], [Goal(DeviceA, 3.2, Midnight.AddHours(6))]);

        var plan = Solve(input);

        var runs = Runs(plan, DeviceA);
        Assert.DoesNotContain(runs, e => e.Class == PlanClasses.Pv);
        Assert.Equal(3.2, runs.Sum(e => e.EnergyKwh), 9);
        Assert.Equal([3.0, 0.2], runs.Select(e => Math.Round(e.EnergyKwh, 9)).ToArray());
    }

    [Fact]
    public void A_device_that_may_not_be_started_is_never_planned_and_its_goal_is_still_listed()
    {
        // Why: start: restore_only means dispatch never starts the device, planner or not, so the plan never asks it to run (no run or urgent entry, no capacity used);
        // the goal stays visible so the UI can say why nothing is planned.
        var input = Make(Flat(6), [Dev(DeviceA, L1, canStart: false)], [Goal(DeviceA, 2, Midnight.AddHours(6))]);

        var plan = Solve(input);

        Assert.Empty(Runs(plan, DeviceA));
        Assert.Equal(0, plan.Device(DeviceA)!.Goals.Single().PlannedKwh, 9);
        Assert.Empty(PlanVerifier.Verify(input, plan));
    }

    [Fact]
    public void A_storage_device_is_not_planned_before_slice_6()
    {
        // Why: battery arbitrage comes in slice 6. Until then a storage device gets none in every slot and the plan uses no capacity for it.
        var storage = Dev(DeviceA, L1) with { Kind = PlanDeviceKind.Storage };
        var input = Make(Flat(6), [storage], [Goal(DeviceA, 2, Midnight.AddHours(6))]);

        var plan = Solve(input);

        Assert.All(plan.Device(DeviceA)!.Entries, e => Assert.Equal(PlanWant.None, e.Want));
    }

    [Fact]
    public void After_the_goal_is_planned_a_stop_device_is_done_and_an_opportunistic_one_is_none()
    {
        // Why: on_met stop means the target is also a cap: after the planned energy every slot until the deadline is done, so dispatch refuses every class; an
        // opportunistic device goes back to none (v2). Before the energy the slots are defer. The slots after the deadline are none for both (the goal is over).
        var inputs = Custom(Midnight, 8, i => ("z", i == 3 ? 0.2 : 1.0, 0.1, 0));
        var stop = Goal(DeviceA, 2, Midnight.AddHours(6), onMet: GoalOnMet.Stop);
        var free = Goal(DeviceB, 2, Midnight.AddHours(6), onMet: GoalOnMet.Opportunistic);
        var input = Make(inputs, [Dev(DeviceA, L1), Dev(DeviceB, L2)], [stop, free]);

        var plan = Solve(input);

        string Pattern(DeviceName d) => string.Concat(plan.Device(d)!.Entries.Select(e => e.Want switch { PlanWant.Run => 'R', PlanWant.Defer => 'D', PlanWant.Done => 'X', PlanWant.Urgent => 'U', _ => '-' }));
        Assert.Equal("DDDRXX--", Pattern(DeviceA));
        Assert.Equal("DDDR----", Pattern(DeviceB));
    }

    [Fact]
    public void Two_goals_of_one_device_are_cumulative_by_deadline()
    {
        // Why: two trips in one day: goal 1 (2 kWh by slot 3) and goal 2 (2 kWh by slot 8) share the device's power. The earlier goal may only use slots before its
        // deadline, and the later one takes the rest; both are met.
        var inputs = Custom(Midnight, 10, i => ("z", i is 6 or 7 ? 0.2 : 1.0, 0.1, 0));
        var early = Goal(DeviceA, 2, Midnight.AddHours(3), revision: 1);
        var late = Goal(DeviceA, 2, Midnight.AddHours(8), revision: 2);
        var input = Make(inputs, [Dev(DeviceA, L1)], [late, early]);

        var plan = Solve(input);

        var goals = plan.Device(DeviceA)!.Goals;
        Assert.Equal([Midnight.AddHours(3), Midnight.AddHours(8)], goals.Select(g => g.Deadline).ToArray());
        Assert.All(goals, g => Assert.Equal(2, g.PlannedKwh, 9));
        var runs = Runs(plan, DeviceA);
        Assert.Equal(2, runs.Where(e => e.End <= Midnight.AddHours(3)).Sum(e => e.EnergyKwh), 9);
        Assert.Equal(2, runs.Where(e => e.Start >= Midnight.AddHours(6)).Sum(e => e.EnergyKwh), 9);
        Assert.Empty(PlanVerifier.Verify(input, plan));
    }

    [Fact]
    public void Surplus_is_shared_between_devices_and_never_counted_twice()
    {
        // Why: the surplus of a slot is one pool. 1.5 kWh of surplus at slot 2 serves two devices only once: the plan's pv energy in that slot never exceeds it, the
        // rest of each need goes to the grid.
        var inputs = Custom(Midnight, 6, i => ("z", 1.0, 0.1, i == 2 ? 1.5 : 0));
        var goals = new[] { Goal(DeviceA, 1.5, Midnight.AddHours(5)), Goal(DeviceB, 1.5, Midnight.AddHours(5)) };
        var input = Make(inputs, [Dev(DeviceA, L1), Dev(DeviceB, L2)], goals);

        var plan = Solve(input);

        var pv = new[] { DeviceA, DeviceB }.SelectMany(d => plan.Device(d)!.Entries).Where(e => e.Class == PlanClasses.Pv).Sum(e => e.EnergyKwh);
        Assert.True(pv <= 1.5 + 1e-9, $"{pv} kWh planned on 1.5 kWh of surplus");
        Assert.Equal(0.1 * 1.5 + 1.0 * 1.5, plan.Cost.Planned, 9);
    }

    [Fact]
    public void A_goal_met_now_plans_nothing_and_a_stop_device_is_done_throughout()
    {
        // Why: a met goal asks for no energy. With on_met stop the device must not take more (the target is a cap): every slot until the deadline is done.
        var met = Goal(DeviceA, 0, Midnight.AddHours(5), onMet: GoalOnMet.Stop);
        var input = Make(Flat(8), [Dev(DeviceA, L1)], [met]);

        var plan = Solve(input);

        Assert.Equal(GoalStatus.Met, plan.Device(DeviceA)!.Goals.Single().Status);
        var entries = plan.Device(DeviceA)!.Entries;
        Assert.All(entries.Take(5), e => Assert.Equal(PlanWant.Done, e.Want));
        Assert.All(entries.Skip(5), e => Assert.Equal(PlanWant.None, e.Want));
    }

    [Fact]
    public void A_met_stop_cap_does_not_stop_the_device_from_being_planned_for_an_unmet_trip_on_the_same_device()
    {
        // Why: a module cap (soft, met, on_met stop, deadline at the end of the day) and a trip (hard, unmet, earlier deadline) share one charger. The cap being met
        // must not turn the trip's slots into done: the trip is planned and charged in full, and only the slots after the trip's energy, still inside the cap's window, are done.
        var inputs = Custom(Midnight, 10, i => ("z", i == 2 ? 0.2 : 1.0, 0.1, 0));
        var cap = Goal(DeviceA, 0, Midnight.AddHours(9), hard: false, onMet: GoalOnMet.Stop, revision: 1);
        var trip = Goal(DeviceA, 2, Midnight.AddHours(5), revision: 2);
        var input = Make(inputs, [Dev(DeviceA, L1)], [cap, trip]);

        var plan = Solve(input);

        var device = plan.Device(DeviceA)!;
        var tripPlan = device.Goals.Single(g => g.Ref.Revision == 2);
        Assert.Equal(2, tripPlan.PlannedKwh, 9);
        Assert.Equal(GoalStatus.Met, device.Goals.Single(g => g.Ref.Revision == 1).Status);
        var runs = Runs(plan, DeviceA);
        Assert.Equal(2, runs.Sum(e => e.EnergyKwh), 9);
        Assert.All(runs, e => Assert.True(e.End <= Midnight.AddHours(5)));
        var lastRun = runs.Max(e => e.End);
        Assert.DoesNotContain(device.Entries, e => e.Want == PlanWant.Done && e.Start < lastRun);
        Assert.All(device.Entries.Where(e => e.Start >= lastRun && e.Start < Midnight.AddHours(9)), e => Assert.Equal(PlanWant.Done, e.Want));
        Assert.Empty(PlanVerifier.Verify(input, plan));
    }

    [Fact]
    public void A_met_stop_cap_never_hides_a_trip_that_cannot_be_met_from_the_plan_notes()
    {
        // Why: the trip is hard and its sources cannot meet it (pv only, no PV, no escalation). The cap being met must not make the plan look healthy: the trip
        // stays at risk with the shortfall note, whatever the entries of the slots say.
        var cap = Goal(DeviceA, 0, Midnight.AddHours(9), hard: false, onMet: GoalOnMet.Stop, revision: 1);
        var trip = Goal(DeviceA, 2, Midnight.AddHours(5), escalate: false, revision: 2);
        var input = Make(Flat(10), [Dev(DeviceA, L1, sources: SourceSet.PvOnly)], [cap, trip]);

        var plan = Solve(input);

        var device = plan.Device(DeviceA)!;
        Assert.Equal(GoalStatus.AtRisk, device.Goals.Single(g => g.Ref.Revision == 2).Status);
        Assert.Contains(PlanNote.GoalShortfall, plan.Notes);
        Assert.Empty(PlanVerifier.Verify(input, plan));
    }

    [Fact]
    public void The_plan_follows_the_twenty_five_hour_day_and_uses_both_repeated_hours()
    {
        // Why: on 2026-10-25 the clocks go back at 03:00 and the 02:00 hour happens twice. Two cheap slots (both 02:00 hours, UTC+2 then UTC+1) must both be used and
        // each carries one hour of energy: a 24-hour assumption would drop one of them and shift every price after it.
        var now = Local(2026, 10, 24, 22);
        var grid = SlotGrid.Build(now, now.AddHours(26), TimeSpan.FromHours(1), Warsaw);
        Assert.Equal(26, grid.Count);
        var inputs = Custom(now, 26, i => ("z", grid.Slots[i].Start.Hour == 2 ? 0.1 : 1.0, 0.1, 0));
        var cheap = Enumerable.Range(0, 26).Where(i => grid.Slots[i].Start.Hour == 2).ToList();
        Assert.Equal(2, cheap.Count);
        var input = Input(now, inputs, [Dev(DeviceA, L1)], [Goal(DeviceA, 4, now.AddHours(26))], Capacity(4000));

        var plan = Solve(input);

        var runs = Runs(plan, DeviceA);
        Assert.Equal(cheap.Select(i => grid.Slots[i].Start).ToArray(), runs.Select(e => e.Start).ToArray());
        Assert.All(runs, e => Assert.Equal(2, e.EnergyKwh, 9));
        Assert.NotEqual(runs[0].Start.Offset, runs[1].Start.Offset);
        Assert.Equal(26, plan.Device(DeviceA)!.Entries.Length);
    }

    [Fact]
    public void The_twenty_three_hour_day_has_twenty_three_slots_of_capacity()
    {
        // Why: on 2026-03-29 the clocks go forward at 02:00 and the day has 23 hours. A goal that needs 2 kW for 22.9 hours must fit in the day's 23 hourly slots and
        // not be called infeasible by a 24-slot assumption in either direction (24 would plan an hour that does not exist; 22 would refuse a feasible goal).
        var now = Local(2026, 3, 29, 0);
        var end = Local(2026, 3, 30, 0);
        var inputs = Custom(now, 23, _ => ("z", 1.0, 0.1, 0));
        Assert.Equal(end, inputs.Grid.To);
        var input = Input(now, inputs, [Dev(DeviceA, L1)], [Goal(DeviceA, 45.8, end, margin: TimeSpan.Zero)], Capacity(4000));

        var plan = Solve(input);

        var goal = plan.Device(DeviceA)!.Goals.Single();
        Assert.NotEqual(GoalStatus.Infeasible, goal.Status);
        Assert.Equal(45.8, goal.PlannedKwh, 9);
        Assert.Equal(23, plan.Device(DeviceA)!.Entries.Length);
        Assert.Empty(PlanVerifier.Verify(input, plan));
    }

    [Fact]
    public void An_empty_horizon_or_no_goal_gives_a_valid_plan_with_none_everywhere()
    {
        // Why: the service plans every cycle, also on installations without goals (heaters only): the plan must be valid, empty of wants and cost nothing.
        var input = Make(Flat(6), [Dev(DeviceA, L1)], []);

        var plan = Solve(input);

        Assert.All(plan.Device(DeviceA)!.Entries, e => Assert.Equal(PlanWant.None, e.Want));
        Assert.Equal(0, plan.Cost.Planned);
        Assert.Empty(PlanVerifier.Verify(input, plan));
    }

    [Fact]
    public void A_goal_for_an_unknown_device_is_ignored()
    {
        // Why: a goal whose device left the configuration must not break a plan or invent a device.
        var input = Make(Flat(6), [Dev(DeviceA, L1)], [Goal(DeviceB, 2, Midnight.AddHours(5))]);

        var plan = Solve(input);

        Assert.Single(plan.Devices);
        Assert.Empty(plan.Device(DeviceA)!.Goals);
    }

    [Fact]
    public void Cancelling_stops_the_solver()
    {
        // Why: max_solve_time is enforced by cancelling a cooperative solver; one that never looks at its token could not be stopped. An already cancelled token
        // must end the plan before it produces anything.
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var input = Make(Flat(6), [Dev(DeviceA, L1)], [Goal(DeviceA, 2, Midnight.AddHours(5))]);

        Assert.Throws<OperationCanceledException>(() => Planner.Plan(input, cancelled.Token));
    }

    [Fact]
    public void A_hard_goal_due_beyond_the_horizon_is_not_infeasible_and_does_not_escalate_for_what_the_horizon_cannot_hold()
    {
        // Why: a trip in five days with a 10-hour horizon: the horizon holds 20 kWh of the 30 kWh, but the days after it are still ahead. Calling that infeasible or
        // escalating to the dear zone (urgent) would buy energy now for a deadline a later plan can still meet cheaply; the plan places what it can on the cheapest
        // cells inside the horizon and does not claim a shortfall it cannot know.
        var inputs = Custom(Midnight, 10, i => ("z", 1.0 + (i * 0.1), 0.1, 0));
        var input = Make(inputs, [Dev(DeviceA, L1)], [Goal(DeviceA, 30, Midnight.AddHours(100))]);

        var plan = Solve(input);

        var goal = plan.Device(DeviceA)!.Goals.Single();
        Assert.Equal(GoalStatus.OnTrack, goal.Status);
        Assert.False(goal.Urgent);
        Assert.Equal(20, goal.PlannedKwh, 9);
        Assert.DoesNotContain(PlanNote.GoalInfeasible, plan.Notes);
        Assert.DoesNotContain(PlanNote.GoalShortfall, plan.Notes);
        Assert.DoesNotContain(Runs(plan, DeviceA), e => e.Want == PlanWant.Urgent);
        Assert.Empty(PlanVerifier.Verify(input, plan));
    }

    [Fact]
    public void A_device_whose_minimum_power_is_above_what_the_limits_leave_it_is_never_planned()
    {
        // Why: running at its minimum (a duty inside the slot is still the minimum power while it is on) would exceed the static limit, which the guard's envelope
        // would refuse; the plan must not ask for it. A 2.2 kW minimum on an 0.9 kW group limit can never run, so the goal is infeasible and nothing is placed.
        var group = new ConstraintKey(Switchboard.Domain.Connection.GroupName.Parse("shed"), Switchboard.Domain.Signals.Phase.L1);
        var device = Dev(DeviceA, L1, maxW: 6500, minW: 2200, keys: [new PlanKeyShare(L1, 1), new PlanKeyShare(group, 1)]);
        var input = Make(Flat(8), [device], [Goal(DeviceA, 4, Midnight.AddHours(6))], Capacity(4000).Add(group, 900));

        var plan = Solve(input);

        Assert.Empty(Runs(plan, DeviceA));
        Assert.Equal(GoalStatus.Infeasible, plan.Device(DeviceA)!.Goals.Single().Status);
        Assert.Empty(PlanVerifier.Verify(input, plan));
    }
}
