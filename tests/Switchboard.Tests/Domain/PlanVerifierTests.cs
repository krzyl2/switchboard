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
/// The independent check before a plan is published (15-PLANNER, "When the plan is missing, stale or wrong"). Why it matters: it is the planner's own safety net,
/// the thing that keeps a bug in the fill, a stale cache or a corrupted merge from ever reaching a consumer. Each test takes a valid plan, breaks exactly one
/// thing by hand and requires the verifier to name it; a verifier that passed a broken plan would be worse than none, because the plan would look checked.
/// </summary>
public sealed class PlanVerifierTests
{
    private static readonly DateTimeOffset Midnight = Local(2026, 10, 26, 0);
    private static readonly DeviceName DeviceA = DeviceName.Parse("device_a");
    private static readonly DeviceName DeviceB = DeviceName.Parse("device_b");

    private static PlanDevice Dev(DeviceName name, ConstraintKey key, SourceSet? sources = null, bool canStart = true) =>
        new(name, PlanDeviceKind.Modulating, 0, 2000, TimeSpan.Zero, [new PlanKeyShare(key, 1)], canStart, new ValueSpec(10, ValueFollows.Static, 1), sources, 1);

    /// <summary>Two devices on L1 (3 kW limit), A with a hard goal of 4 kWh by slot 5 on any energy, B without a goal; surplus 1.5 kWh in slot 3, grid at 1 per kWh.</summary>
    private static (PlanInput Input, PlanSnapshot Plan) Valid(SourceSet? sourcesA = null, bool aCanStart = true)
    {
        var inputs = Custom(Midnight, 8, i => ("z", 1.0, 0.1, i == 3 ? 1.5 : 0));
        var input = Input(Midnight, inputs, [Dev(DeviceA, L1, sourcesA, aCanStart), Dev(DeviceB, L1)], [Goal(DeviceA, 4, Midnight.AddHours(5), sources: sourcesA)], Capacity(3000));
        return (input, Solve(input));
    }

    private static PlanSnapshot Edit(PlanSnapshot plan, DeviceName device, Func<ImmutableArray<PlanEntry>, ImmutableArray<PlanEntry>> entries, Func<ImmutableArray<PlanGoal>, ImmutableArray<PlanGoal>>? goals = null) =>
        plan.WithDevices([.. plan.Devices.Select(d => d.Device == device ? d with { Entries = entries(d.Entries), Goals = goals is null ? d.Goals : goals(d.Goals) } : d)]);

    private static PlanEntry Run(PlanSnapshot plan, DeviceName device) => plan.Device(device)!.Entries.First(e => e.Want == PlanWant.Run);

    private static ImmutableArray<PlanEntry> Replace(ImmutableArray<PlanEntry> entries, PlanEntry old, PlanEntry replacement) => [.. entries.Select(e => e == old ? replacement : e)];

    private static void AssertFound(PlanInput input, PlanSnapshot broken, PlanViolationCode code)
    {
        var found = PlanVerifier.Verify(input, broken);
        Assert.Contains(code, found.Select(v => v.Code));
    }

    [Fact]
    public void A_plan_the_planner_made_has_no_violation()
    {
        // Why: the baseline of every test below: the planner's own output for this instance is clean, so each violation found afterwards is the hand-made one.
        var (input, plan) = Valid();

        Assert.Empty(PlanVerifier.Verify(input, plan));
        Assert.Equal(4, plan.Device(DeviceA)!.Entries.Sum(e => e.EnergyKwh), 9);
    }

    [Fact]
    public void A_negative_or_non_finite_number_is_found()
    {
        // Why: a NaN or a negative energy would flow into every sum downstream and turn "within the limit" into a comparison that is always false.
        var (input, plan) = Valid();
        var run = Run(plan, DeviceA);

        AssertFound(input, Edit(plan, DeviceA, e => Replace(e, run, run with { EnergyKwh = double.NaN })), PlanViolationCode.InvalidNumber);
        AssertFound(input, Edit(plan, DeviceA, e => Replace(e, run, run with { EnergyKwh = -1 })), PlanViolationCode.InvalidNumber);
        AssertFound(input, Edit(plan, DeviceA, e => Replace(e, run, run with { MaxPowerW = double.PositiveInfinity })), PlanViolationCode.InvalidNumber);
    }

    [Fact]
    public void An_entry_outside_the_slots_a_repeated_slot_or_a_missing_slot_is_found()
    {
        // Why: no entry may describe time the plan was not made for (before now, past the horizon, half a slot), and every slot has exactly one entry per device.
        var (input, plan) = Valid();
        var run = Run(plan, DeviceA);

        AssertFound(input, Edit(plan, DeviceA, e => Replace(e, run, run with { Start = run.Start.AddMinutes(30) })), PlanViolationCode.OutsideHorizon);
        AssertFound(input, Edit(plan, DeviceA, e => Replace(e, run, run with { Start = Midnight.AddHours(-2), End = Midnight.AddHours(-1) })), PlanViolationCode.OutsideHorizon);
        AssertFound(input, Edit(plan, DeviceA, e => [.. e, e[0]]), PlanViolationCode.OutsideHorizon);
        AssertFound(input, Edit(plan, DeviceA, e => e.RemoveAt(0)), PlanViolationCode.OutsideHorizon);
    }

    [Fact]
    public void A_device_the_plan_was_not_asked_for_or_one_that_is_missing_is_found()
    {
        // Why: the plan must be about exactly the devices of its input; an extra device would be a command-shaped surprise, a missing one a hole.
        var (input, plan) = Valid();

        AssertFound(input, plan.WithDevices([.. plan.Devices, plan.Devices[0] with { Device = DeviceName.Parse("stranger") }]), PlanViolationCode.UnknownDevice);
        AssertFound(input, plan.WithDevices([plan.Devices[0]]), PlanViolationCode.UnknownDevice);
    }

    [Fact]
    public void Energy_above_the_static_limit_of_a_phase_is_found_even_when_each_device_is_within_its_own_maximum()
    {
        // Why: the headline check. Two 2 kW devices on a 3 kW phase: 2 kWh each in one hour is within each device's maximum and 1 kWh over the phase. The plan must
        // never ask for what the guard's envelope could never allow.
        var (input, plan) = Valid();
        var slot = plan.Slots[0].Slot;
        PlanEntry Run2(DateTimeOffset start, DateTimeOffset end) => new(start, end, PlanWant.Run, "zone:z", 2, 2000, plan.Device(DeviceA)!.Goals[0].Ref is { } g ? g : null);
        var broken = plan
            .WithDevices([.. plan.Devices.Select(d => d.Device == DeviceA
                ? d with { Entries = Replace(d.Entries, d.Entries[0], Run2(slot.Start, slot.End)) }
                : d with { Entries = Replace(d.Entries, d.Entries[0], Run2(slot.Start, slot.End) with { Goal = null }) })]);

        var found = PlanVerifier.Verify(input, broken);

        Assert.Contains(found, v => v is { Code: PlanViolationCode.CapacityExceeded, Device: null } && v.Slot == slot.Start);
    }

    [Fact]
    public void A_group_limit_is_checked_like_a_phase_limit()
    {
        // Why: a device behind a downstream breaker is held to the group's phase as well; 1 kW group limit, 1.5 kWh planned in the hour.
        var group = new ConstraintKey(Switchboard.Domain.Connection.GroupName.Parse("shed"), Switchboard.Domain.Signals.Phase.L1);
        var inputs = Custom(Midnight, 4, _ => ("z", 1.0, 0.1, 0));
        var device = Dev(DeviceA, L1) with { Keys = [new PlanKeyShare(L1, 1), new PlanKeyShare(group, 1)] };
        var input = Input(Midnight, inputs, [device], [Goal(DeviceA, 1.5, Midnight.AddHours(4))], Capacity(3000).Add(group, 1000));
        var plan = Solve(input);
        Assert.Empty(PlanVerifier.Verify(input, plan));

        var run = Run(plan, DeviceA);
        var broken = Edit(plan, DeviceA, e => Replace(e, run, run with { EnergyKwh = 1.5, MaxPowerW = 2000 }), g => [.. g.Select(x => x with { PlannedKwh = 1.5, ShortfallKwh = 0 })]);
        var broken2 = Edit(broken, DeviceA, e => e);

        AssertFound(input, broken2, PlanViolationCode.CapacityExceeded);
    }

    [Fact]
    public void An_entry_above_the_device_s_own_maximum_is_found()
    {
        // Why: a ceiling or a slot energy above what the device can draw would let dispatch be told to ask for more than the hardware has.
        var (input, plan) = Valid();
        var run = Run(plan, DeviceA);

        AssertFound(input, Edit(plan, DeviceA, e => Replace(e, run, run with { MaxPowerW = 5000 })), PlanViolationCode.DevicePowerExceeded);
        AssertFound(input, Edit(plan, DeviceA, e => Replace(e, run, run with { EnergyKwh = 3 })), PlanViolationCode.DevicePowerExceeded);
    }

    [Fact]
    public void More_energy_than_the_goals_need_is_found()
    {
        // Why: the plan may never ask a device for more energy than its goals need: the budget is the goal, not a licence.
        var (input, plan) = Valid();
        var entries = plan.Device(DeviceA)!.Entries;
        var spare = entries.First(e => e.Want != PlanWant.Run);
        var broken = Edit(plan, DeviceA, e => Replace(e, spare, spare with { Want = PlanWant.Run, Class = "zone:z", EnergyKwh = 1, MaxPowerW = 2000 }));

        AssertFound(input, broken, PlanViolationCode.EnergyExceedsNeed);
    }

    [Fact]
    public void Planning_more_surplus_than_the_slot_has_is_found()
    {
        // Why: the surplus of a slot is one pool (1.5 kWh in slot 3); an entry that is wholly surplus and bigger than that counts energy that does not exist.
        var (input, plan) = Valid();
        var slot = plan.Slots[3].Slot;
        var target = plan.Device(DeviceA)!.Entries.Single(e => e.Start == slot.Start);
        var broken = Edit(plan, DeviceA, e => Replace(e, target, target with { Want = PlanWant.Run, Class = PlanClasses.Pv, EnergyKwh = 2, MaxPowerW = 2000 }));

        AssertFound(input, broken, PlanViolationCode.SurplusExceeded);
    }

    [Fact]
    public void A_run_entry_for_a_device_without_a_goal_or_that_may_not_start_is_found()
    {
        // Why: the plan never invents a want, and it never asks for a device that dispatch may not start (restore only). Both would be the plan commanding by another name.
        var (input, plan) = Valid();
        var none = plan.Device(DeviceB)!.Entries[0];
        AssertFound(input, Edit(plan, DeviceB, e => Replace(e, none, none with { Want = PlanWant.Run, Class = "zone:z", EnergyKwh = 1, MaxPowerW = 2000 })), PlanViolationCode.NotPlannable);

        var (inputR, planR) = Valid(aCanStart: false);
        var idle = planR.Device(DeviceA)!.Entries[0];
        AssertFound(inputR, Edit(planR, DeviceA, e => Replace(e, idle, idle with { Want = PlanWant.Run, Class = "zone:z", EnergyKwh = 1, MaxPowerW = 2000 })), PlanViolationCode.NotPlannable);

        var defer = plan.Device(DeviceA)!.Entries.First(e => e.Want == PlanWant.Defer);
        AssertFound(input, Edit(plan, DeviceA, e => Replace(e, defer, defer with { EnergyKwh = 1 })), PlanViolationCode.NotPlannable);
    }

    [Fact]
    public void A_class_outside_the_goal_s_sources_is_found_unless_the_entry_is_urgent_for_a_hard_goal()
    {
        // Why: a goal that lists pv only may not be planned on a zone by an ordinary run entry; only an urgent entry of a hard goal is the escalation the design
        // allows. A zone class sneaking in would let the plan buy energy the user never allowed.
        var (input, plan) = Valid(sourcesA: SourceSet.PvOnly);
        var urgent = Run(plan, DeviceA);
        Assert.Equal(PlanWant.Urgent, plan.Device(DeviceA)!.Entries.First(e => e.EnergyKwh > 0).Want);
        Assert.Empty(PlanVerifier.Verify(input, plan));

        var broken = Edit(plan, DeviceA, e => [.. e.Select(x => x.Want == PlanWant.Urgent ? x with { Want = PlanWant.Run } : x)]);

        AssertFound(input, broken, PlanViolationCode.ClassOutsideSources);
        Assert.NotNull(urgent);
    }

    [Fact]
    public void A_hard_goal_that_can_be_met_but_has_no_energy_is_found()
    {
        // Why: the planner must never leave a feasible hard goal without an entry: with every entry blanked and the goal's own numbers kept, the plan neither
        // delivers nor admits a shortfall, and the verifier says the hard goal is unmet.
        var (input, plan) = Valid();
        var blank = Edit(plan, DeviceA, e => [.. e.Select(x => x with { Want = PlanWant.None, Class = null, EnergyKwh = 0, MaxPowerW = 0 })]);
        AssertFound(input, blank, PlanViolationCode.HardGoalUnmet);

        var lie = Edit(plan, DeviceA, e => [.. e.Select(x => x with { Want = PlanWant.None, Class = null, EnergyKwh = 0, MaxPowerW = 0 })], g => [.. g.Select(x => x with { PlannedKwh = 0, ShortfallKwh = 0 })]);
        AssertFound(input, lie, PlanViolationCode.HardGoalUnmet);
    }

    [Fact]
    public void Calling_a_feasible_goal_infeasible_is_found()
    {
        // Why: infeasible escalates to urgent at once; a planner (or a corrupted merge) that cried infeasible about a goal the device could deliver alone would
        // start the car in the dearest zone for nothing.
        var (input, plan) = Valid();
        var broken = Edit(plan, DeviceA, e => e, g => [.. g.Select(x => x with { Status = GoalStatus.Infeasible })]);

        AssertFound(input, broken, PlanViolationCode.InfeasibleClaimed);
    }

    [Fact]
    public void An_honest_infeasible_plan_passes()
    {
        // Why: the other side of the check above. A goal that truly cannot be met (9.9 kWh in two hours at 2 kW) is reported infeasible with what can be delivered,
        // and the verifier accepts that; it must not refuse the planner's honest answer.
        var inputs = Custom(Midnight, 8, _ => ("z", 1.0, 0.1, 0));
        var input = Input(Midnight, inputs, [Dev(DeviceA, L1)], [Goal(DeviceA, 9.9, Midnight.AddHours(2))], Capacity(3000));

        var plan = Solve(input);

        Assert.Equal(GoalStatus.Infeasible, plan.Device(DeviceA)!.Goals.Single().Status);
        Assert.Empty(PlanVerifier.Verify(input, plan));
    }
}
