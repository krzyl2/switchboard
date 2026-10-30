using System.Collections.Immutable;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Planning;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.PlannerFixtures;

namespace Switchboard.Tests.Domain;

/// <summary>
/// A goal that asks to be spread (EV.md 2a) finishes as late as its window allows and carries a slot ceiling below the device's: a long night is charged at 14 A
/// instead of 20 A. Why it matters: the go-e then draws the least that still makes the goal, and the plan stays calm because the power is constant over the window.
/// A goal that does not ask must stay exactly as it was (the digests of every installation without the EV module depend on it).
/// </summary>
public sealed class PlannerSpreadTests
{
    private static readonly DateTimeOffset Midnight = Local(2026, 10, 26, 0);
    private static readonly DeviceName DeviceA = DeviceName.Parse("device_a");

    /// <summary>6 to 20 A on L1 at 230 V (1380 to 4600 W), one ampere is 230 W, no minimum run.</summary>
    private static PlanDevice Amps(double maxW = 4600, double minRun = 0) =>
        new(DeviceA, PlanDeviceKind.Modulating, 1380, maxW, TimeSpan.FromMinutes(minRun), [new PlanKeyShare(L1, 1)], true, new ValueSpec(10, ValueFollows.Static, 1), null, 1, 230);

    private static PlanInputs Flat(DateTimeOffset now, int slots) => Custom(now, slots, _ => ("z", 1.0, 0.1, 0));

    private static GoalRequest Spread(double kwh, DateTimeOffset deadline, bool hard = true) => Goal(DeviceA, kwh, deadline, hard) with { Spread = true };

    private static PlanInput Make(double kwh, int slots = 8, bool spread = true, double maxW = 4600, double capacityW = 5000, DateTimeOffset? now = null)
    {
        var at = now ?? Midnight;
        var goal = Goal(DeviceA, kwh, Midnight.AddHours(8)) with { Spread = spread };
        return Input(at, Flat(at, slots), [Amps(maxW)], [goal], Capacity(capacityW));
    }

    private static List<PlanEntry> Runs(PlanSnapshot plan) => [.. plan.Device(DeviceA)!.Entries.Where(e => e.Want is PlanWant.Run or PlanWant.Urgent)];

    [Theory]
    [InlineData(12, 1610)] // 1.5 kW is 6.5 A: up to the next whole ampere, 7 A
    [InlineData(8, 1380)] // 1 kW is below the device's minimum: 6 A
    [InlineData(25.6, 3220)] // 3.2 kW is 13.9 A: 14 A instead of 20 A
    public void A_spread_goal_carries_the_lowest_ceiling_that_holds_its_energy(double kwh, double expectedCeilingW)
    {
        // Why: the numbers of the design. The ceiling never undercuts the energy (rounded up to a step) and never the device's minimum.
        var input = Make(kwh);

        var plan = Solve(input);

        var runs = Runs(plan);
        Assert.Equal(kwh, runs.Sum(e => e.EnergyKwh), 9);
        Assert.All(runs, e => Assert.Equal(expectedCeilingW, e.MaxPowerW, 6));
        Assert.All(runs, e => Assert.True(e.EnergyKwh <= (expectedCeilingW / 1000 * 1) + 1e-9));
        Assert.Empty(PlanVerifier.Verify(input, plan));
    }

    [Fact]
    public void A_spread_goal_ends_as_late_as_the_window_allows()
    {
        // Why: the battery should sit at 100 percent as short as possible: with equal prices the energy goes into the latest slots and the earliest one is the partial.
        var plan = Solve(Make(12));

        var runs = Runs(plan);
        Assert.Equal(Midnight.AddHours(7), runs[^1].Start);
        Assert.Equal(Midnight, runs[0].Start);
        Assert.True(runs[0].EnergyKwh < runs[^1].EnergyKwh, "the partial slot is the earliest one");
    }

    [Fact]
    public void A_goal_that_does_not_ask_to_be_spread_stays_at_the_ceiling_and_earliest_first()
    {
        // Why: the digests without the EV module. The same energy without the flag is planned as before: earliest slots, the device's full ceiling in every entry.
        var plain = Solve(Make(12, spread: false));
        var spread = Solve(Make(12));

        var runs = Runs(plain);
        Assert.Equal(Midnight, runs[0].Start);
        Assert.All(runs, e => Assert.Equal(4600, e.MaxPowerW, 6));
        Assert.NotEqual(plain.Canonical(), spread.Canonical());
    }

    [Fact]
    public void The_ceiling_never_exceeds_what_the_static_limits_leave()
    {
        // Why: protection first: a spread ceiling is a planned value, the limits are the guard's. With 3 kW left on the phase the entry ceiling is at most 3 kW.
        var input = Make(18, capacityW: 3000);

        var plan = Solve(input);

        Assert.All(Runs(plan), e => Assert.True(e.MaxPowerW <= 3000 + 1e-9));
        Assert.Equal(18, Runs(plan).Sum(e => e.EnergyKwh), 9);
        Assert.Empty(PlanVerifier.Verify(input, plan));
    }

    [Fact]
    public void A_replan_every_hour_keeps_the_same_ceiling_while_the_charge_progresses()
    {
        // Why: no oscillation. Energy delivered at the ceiling shrinks need and window together, so the lowest ceiling does not move: the amp setpoint stays put and
        // persistence_up_cycles has nothing to count. 12 kWh in 8 h, replanned at each hour edge with what the previous plan delivered.
        var remaining = 12.0;
        var ceilings = new List<double>();
        for (var hour = 0; hour < 7; hour++)
        {
            var now = Midnight.AddHours(hour);
            var input = Input(now, Flat(now, 8 - hour), [Amps()], [Spread(remaining, Midnight.AddHours(8))], Capacity(5000));
            var plan = Solve(input);
            var first = plan.Device(DeviceA)!.Entries[0];
            if (first.Want == PlanWant.Run)
            {
                ceilings.Add(first.MaxPowerW);
                remaining -= first.EnergyKwh;
            }
        }

        Assert.NotEmpty(ceilings);
        Assert.All(ceilings, c => Assert.Equal(1610, c, 6));
    }

    [Fact]
    public void Planning_the_same_input_twice_gives_the_same_plan()
    {
        // Why: the bisection and the rounding are deterministic; a plan that differed run to run would be adopted and re-adopted by the service.
        Assert.Equal(Solve(Make(12)).Canonical(), Solve(Make(12)).Canonical());
    }

    [Fact]
    public void An_infeasible_spread_goal_runs_from_now_at_the_ceiling()
    {
        // Why: spreading is a luxury of a goal with time to spare. 20 kWh in two hours cannot be met, so the plan asks for everything at once (urgent) and says so.
        var goal = Spread(20, Midnight.AddHours(2));
        var input = Input(Midnight, Flat(Midnight, 8), [Amps()], [goal], Capacity(5000));

        var plan = Solve(input);

        var runs = Runs(plan);
        Assert.Equal(Midnight, runs[0].Start);
        Assert.All(runs, e => Assert.Equal(4600, e.MaxPowerW, 6));
        Assert.All(runs, e => Assert.Equal(PlanWant.Urgent, e.Want));
    }

    [Fact]
    public void A_goal_at_its_latest_safe_start_is_not_spread()
    {
        // Why: the safety net of the hard goal is computed at the full ceiling (latest grid start). 36.8 kWh in 8 h at 4.6 kW needs all 8 h: the goal is at its latest
        // start now, so it is urgent and runs at the ceiling in every slot instead of being spread.
        var plan = Solve(Make(36.8));

        Assert.All(Runs(plan), e => Assert.Equal(4600, e.MaxPowerW, 6));
        Assert.Equal(PlanWant.Urgent, Runs(plan)[0].Want);
    }

    [Fact]
    public void A_spread_goal_with_slack_is_not_turned_urgent_by_the_escalation_margin()
    {
        // Why: the escalation margin (30 min) is a buffer before the ceiling's latest start. A spread goal runs below the ceiling on purpose, so on schedule it would
        // always be inside that margin in the last hours (remaining x (1 - P/ceiling) <= margin) and jump from 14-16 A to 20 A, finishing early, against "ends as late
        // as the window allows". 7.2 kWh in the last 2 h is 3.6 kW, a latest grid start 26 min after now: not urgent yet, still spread at 16 A.
        var now = Midnight.AddHours(4);
        var input = Input(now, Flat(now, 2), [Amps()], [Spread(7.2, Midnight.AddHours(6))], Capacity(5000));

        var plan = Solve(input);

        var runs = Runs(plan);
        Assert.NotEmpty(runs);
        Assert.All(runs, e => Assert.Equal(PlanWant.Run, e.Want));
        Assert.All(runs, e => Assert.True(e.MaxPowerW < 4600, "spread, not the ceiling"));
        Assert.Equal(7.2, runs.Sum(e => e.EnergyKwh), 9);
        Assert.False(plan.Device(DeviceA)!.Goals.Single().Urgent);
    }

    [Fact]
    public void A_spread_goal_that_has_fallen_behind_goes_urgent_when_delivery_at_the_ceiling_only_just_fits()
    {
        // Why: the other half of the rule above. 9.2 kWh in 2 h at 4.6 kW needs all of the 2 h: the goal is at its latest start now, so it must run at the ceiling.
        var now = Midnight.AddHours(4);
        var input = Input(now, Flat(now, 2), [Amps()], [Spread(9.2, Midnight.AddHours(6))], Capacity(5000));

        var plan = Solve(input);

        Assert.All(Runs(plan), e => Assert.Equal(PlanWant.Urgent, e.Want));
        Assert.All(Runs(plan), e => Assert.Equal(4600, e.MaxPowerW, 6));
    }

    [Fact]
    public void A_relay_is_never_spread()
    {
        // Why: a relay has no setpoint to carry a ceiling below its maximum: its entries say the device's own power. Declared here with a minimum below the maximum so
        // that a spread pass that wrongly touched it would lower the ceiling to 750 W; the late tie-break may still move its slots, the power may not change.
        var relay = new PlanDevice(DeviceA, PlanDeviceKind.Relay, 500, 2000, TimeSpan.Zero, [new PlanKeyShare(L1, 1)], true, new ValueSpec(10, ValueFollows.Static, 1), null, 1);
        var input = Input(Midnight, Flat(Midnight, 8), [relay], [Spread(6, Midnight.AddHours(8))], Capacity(5000));

        var plan = Solve(input);

        Assert.Equal(6, Runs(plan).Sum(e => e.EnergyKwh), 9);
        Assert.All(Runs(plan), e => Assert.Equal(2000, e.MaxPowerW, 6));
        Assert.Empty(PlanVerifier.Verify(input, plan));
    }

    [Fact]
    public void A_spread_that_the_minimum_run_forbids_keeps_the_energy_where_it_was()
    {
        // Why: spreading 12 kWh over 8 h puts 1.6 kWh in a slot, below the 2.76 kWh a two-hour minimum run needs; the spread is then given up and the goal stays as
        // planned at the ceiling. It must never lose energy on the way: the sum is still 12 kWh and the plan still passes the verifier.
        var goal = Spread(12, Midnight.AddHours(8));
        var input = Input(Midnight, Flat(Midnight, 8), [Amps(minRun: 120)], [goal], Capacity(5000));

        var plan = Solve(input);

        Assert.Equal(12, Runs(plan).Sum(e => e.EnergyKwh), 9);
        Assert.All(Runs(plan), e => Assert.Equal(4600, e.MaxPowerW, 6));
        Assert.Empty(PlanVerifier.Verify(input, plan));
    }

    [Fact]
    public void An_unmet_quiet_stop_goal_with_no_surplus_holds_the_device_until_its_deadline()
    {
        // Why: the PV-only cap with on_met stop, no surplus before its midnight deadline. Without a hold the slots read as "no entry" and the device is a v2
        // opportunist: it charges from the grid in the off-peak zone up to full, which is what the cap exists to prevent. The hold is a Defer until the deadline,
        // and only for a goal that is quiet about its shortfall: the same goal not flagged keeps the v2 reading, so installations without the EV module do not move.
        var evening = Local(2026, 10, 26, 22);
        var inputs = Custom(evening, 6, _ => ("offpeak", 0.6, 0.1, 0));
        var cap = Goal(DeviceA, 2, evening.AddHours(2), hard: false, sources: SourceSet.PvOnly, onMet: GoalOnMet.Stop);

        var held = Solve(Input(evening, inputs, [Amps()], [cap with { QuietShortfall = true }], Capacity(5000))).Device(DeviceA)!.Entries;
        var plain = Solve(Input(evening, inputs, [Amps()], [cap], Capacity(5000))).Device(DeviceA)!.Entries;

        Assert.All(held.Take(2), e => Assert.Equal(PlanWant.Defer, e.Want));
        Assert.All(held.Skip(2), e => Assert.Equal(PlanWant.None, e.Want));
        Assert.All(plain, e => Assert.Equal(PlanWant.None, e.Want));
    }

    [Fact]
    public void A_spread_ceiling_above_the_device_maximum_is_rejected_by_the_verifier()
    {
        // Why: the verifier is the net under the planner: a spread entry that claims more than the device can draw must be refused whatever made it.
        var input = Make(12);
        var plan = Solve(input);
        var run = Runs(plan)[0];
        var broken = plan.WithDevices([.. plan.Devices.Select(d => d with { Entries = [.. d.Entries.Select(e => e == run ? e with { MaxPowerW = 5000 } : e)] })]);

        Assert.Contains(PlanViolationCode.DevicePowerExceeded, PlanVerifier.Verify(input, broken).Select(v => v.Code));
    }

    [Fact]
    public void An_unplaced_quiet_goal_does_not_raise_the_shortfall_note()
    {
        // Why: a PV-only cap that a cloudy evening cannot fill is the normal case, not a warning. The same goal without the flag does raise the note, so the flag is
        // the only difference and a real shortfall of another goal still shows.
        var pvOnly = Goal(DeviceA, 4, Midnight.AddHours(6), hard: false, sources: SourceSet.PvOnly);
        PlanSnapshot Plan(GoalRequest goal) => Solve(Input(Midnight, Flat(Midnight, 8), [Amps()], [goal], Capacity(5000)));

        var loud = Plan(pvOnly);
        var quiet = Plan(pvOnly with { QuietShortfall = true });

        Assert.Contains(PlanNote.GoalShortfall, loud.Notes);
        Assert.DoesNotContain(PlanNote.GoalShortfall, quiet.Notes);
        Assert.Equal(4, quiet.Device(DeviceA)!.Goals.Single().ShortfallKwh, 9);
    }

    [Fact]
    public void A_spread_goal_completes_at_the_time_its_ceiling_allows()
    {
        // Why: the predicted completion divides the last slot's energy by the entry's ceiling; with the spread ceiling the goal is done at the slot's end for a full
        // slot, not at a fictitious earlier time that the ceiling of 20 A would have allowed.
        var plan = Solve(Make(12));

        var goal = plan.Device(DeviceA)!.Goals.Single();
        Assert.Equal(Midnight.AddHours(8), goal.PredictedCompletion);
    }
}
