using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Planning;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Planning;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.PlannerFixtures;

namespace Switchboard.Tests.Application.Planning;

/// <summary>
/// <c>PlanView.For(device, now)</c> (15-PLANNER, "When the plan is missing, stale or wrong: exactly v2"): one named row per row of that table. Why it matters: a
/// plan that dispatch uses when it should not is the one way the planner could hurt a decision, so every way a plan can be wrong must come back as no entry at
/// all (the v2 path), never as "defer everything".
/// </summary>
public sealed class PlanViewTests
{
    private static readonly DeviceName Heater2 = DeviceName.Parse("heater_l2");

    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(45);

    /// <summary>A plan with two goal devices (the car on the off-peak zone, a heater on any), no forecast: grid cells all the way.</summary>
    private sealed class World
    {
        public World(GoalOnMet carOnMet = GoalOnMet.Opportunistic, double carRemaining = 9.9)
        {
            var now = Local(2026, 10, 26, 18);
            var deadline = Local(2026, 10, 28, 7);
            var inputs = Inputs(now, PvForecast.None, [deadline - TimeSpan.FromMinutes(30)]);
            Plan = Solve(Input(
                now,
                inputs,
                [Charger(Offpeak), Heater("heater_l2", L2, SourceSet.Any)],
                [Goal(Car, carRemaining, deadline, margin: TimeSpan.FromMinutes(30), sources: Offpeak, onMet: carOnMet), Goal(Heater2, 3, deadline, margin: TimeSpan.FromMinutes(30), sources: SourceSet.Any)]));
            Holder.Publish(new PlanState(Plan, false, null, [], MaxAge));
            Currency.ConfigRevision = Plan.ConfigRevision;
            foreach (var device in Plan.Devices)
            {
                Currency.Specs[device.Device] = device.Goals[0].Ref.SpecIdentity;
                foreach (var goal in device.Goals)
                {
                    Currency.Goals[(device.Device, goal.Ref.Id, goal.Ref.Revision)] = new PlanGoalState(device.Device == Car ? Offpeak : SourceSet.Any, goal.OnMet == GoalOnMet.Stop);
                }
            }

            View = new PlanView(Holder, Currency, Logs.For<PlanView>());
        }

        public PlanSnapshot Plan { get; }

        public PlanHolder Holder { get; } = new();

        public StubCurrency Currency { get; } = new();

        public LogCapture Logs { get; } = new();

        public PlanView View { get; }

        /// <summary>The first slot start at which both devices have a planned entry (run or defer, urgent): a time both can be asked at.</summary>
        public DateTimeOffset BothPlanned => Plan.Slots.Select(s => s.Slot.Start)
            .First(t => Plan.EntryAt(Car, t) is { Want: not PlanWant.None } && Plan.EntryAt(Heater2, t) is { Want: not PlanWant.None });

        public void Republish(PlanState state) => Holder.Publish(state);
    }

    [Fact]
    public void A_fresh_plan_made_for_the_configuration_spec_and_goal_gives_the_entry_of_the_slot_containing_now()
    {
        // Why: the happy path the rest are exceptions to: dispatch gets exactly the slot's entry (want, class, budget, ceiling) and nothing of the rest of the plan.
        var world = new World();
        var at = world.BothPlanned + TimeSpan.FromMinutes(10);

        var entry = world.View.For(Car, at);

        var planned = world.Plan.EntryAt(Car, at)!;
        Assert.NotNull(entry);
        Assert.Equal(planned.Want, entry.Want);
        Assert.Equal(planned.Class, entry.Class);
        Assert.Equal(planned.EnergyKwh, entry.EnergyKwh);
        Assert.Equal(planned.MaxPowerW, entry.MaxPowerW);
        Assert.Equal((planned.Start, planned.End), (entry.SlotStart, entry.SlotEnd));
        Assert.Equal(world.Plan.Id, entry.PlanId);
    }

    [Fact]
    public void Mode_off_gives_no_entry_whatever_the_plan_says()
    {
        // Why: planner.dispatch off is the default and is byte for byte v2 (PLAN-14): not even a perfect plan may reach a decision.
        var world = new World();
        world.Currency.Mode = PlannerDispatchMode.Off;

        Assert.Null(world.View.For(Car, world.BothPlanned));
        Assert.Equal(PlannerDispatchMode.Off, world.View.Mode);
    }

    [Fact]
    public void No_plan_yet_gives_no_entry_and_never_defers_everything()
    {
        // Why: "a missing plan never means defer everything": absence is v2, a Defer is an explicit entry of a fresh plan.
        var world = new World();
        world.Republish(PlanState.Empty);

        Assert.Null(world.View.For(Car, world.BothPlanned));
    }

    [Fact]
    public void A_plan_older_than_plan_max_age_gives_no_entry()
    {
        // Why: a plan is a forecast of the next hours; an old one is a hazard. The age is judged on every use, not when it was published.
        var world = new World();
        var at = world.Plan.ComputedAt + MaxAge + TimeSpan.FromSeconds(1);

        Assert.Null(world.View.For(Car, at));
        Assert.NotNull(world.View.For(Car, world.Plan.ComputedAt + MaxAge));
    }

    [Fact]
    public void A_plan_from_the_future_gives_no_entry()
    {
        // Why: a clock stepped back leaves a plan computed "later" than now; it was made for a world that has not happened.
        var world = new World();

        Assert.Null(world.View.For(Car, world.Plan.ComputedAt - TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_moment_beyond_the_horizon_gives_no_entry()
    {
        // Why: no slot contains it; the plan says nothing about it. (The maximum age is kept alive by republishing a plan of the same content.)
        var world = new World();
        world.Republish(new PlanState(world.Plan, false, null, [], TimeSpan.FromDays(10)));

        Assert.Null(world.View.For(Car, world.Plan.HorizonEnd));
        Assert.Null(world.View.For(Car, world.Plan.HorizonEnd + TimeSpan.FromHours(1)));
    }

    [Fact]
    public void A_suspended_plan_gives_no_entry()
    {
        // Why: safe state, the kill switch or no Home Assistant: everything is off as before and the plan is ignored.
        var world = new World();
        world.Republish(new PlanState(world.Plan, true, null, [], MaxAge));

        Assert.Null(world.View.For(Car, world.BothPlanned));
    }

    [Fact]
    public void A_plan_made_for_another_configuration_gives_no_entry()
    {
        // Why: after a reload the plan describes devices, goals and prices that may no longer exist; the next replan makes a new one.
        var world = new World();
        world.Currency.ConfigRevision = world.Plan.ConfigRevision + 1;

        Assert.Null(world.View.For(Car, world.BothPlanned));
        Assert.Null(world.View.For(Heater2, world.BothPlanned));
    }

    [Fact]
    public void An_entry_made_for_another_spec_or_goal_is_not_applied_to_that_device_and_the_others_keep_theirs()
    {
        // Why: the table row "that device v2, others keep their entries": a charger re-declared with another range must not run on the old ceiling, but the heater
        // is untouched by it.
        var world = new World();
        var at = world.BothPlanned;
        Assert.NotNull(world.View.For(Car, at));
        Assert.NotNull(world.View.For(Heater2, at));

        world.Currency.Specs[Car] += 1;
        Assert.Null(world.View.For(Car, at));
        Assert.NotNull(world.View.For(Heater2, at));

        world.Currency.Specs[Car] -= 1;
        var goal = world.Plan.Device(Car)!.Goals[0].Ref;
        world.Currency.Goals.Remove((Car, goal.Id, goal.Revision));
        world.Currency.Goals[(Car, goal.Id, goal.Revision + 1)] = new PlanGoalState(Offpeak, false);
        Assert.Null(world.View.For(Car, at));
        Assert.NotNull(world.View.For(Heater2, at));
    }

    [Fact]
    public void A_device_with_no_entry_or_no_goal_in_the_plan_gives_no_entry()
    {
        // Why: opportunists (no goal) are v2; the plan never invents a want. An undeclared device has nothing to compare with.
        var world = new World();

        Assert.Null(world.View.For(DeviceName.Parse("heater_l1"), world.BothPlanned));
        world.Currency.Specs.Remove(Car);
        Assert.Null(world.View.For(Car, world.BothPlanned));
    }

    [Fact]
    public void A_done_entry_is_used_only_while_the_goal_still_says_stop_when_met()
    {
        // Why: Done refuses every class on the strength of on_met: stop. If the goal was edited to opportunistic since the plan, Done would hold a charger off for
        // a want that no longer exists.
        var world = new World(GoalOnMet.Stop, carRemaining: 2);
        world.Republish(new PlanState(world.Plan, false, null, [], TimeSpan.FromDays(10)));
        var done = world.Plan.Slots.Select(s => s.Slot.Start).First(t => world.Plan.EntryAt(Car, t) is { Want: PlanWant.Done });

        Assert.Equal(PlanWant.Done, world.View.For(Car, done)!.Want);

        var goal = world.Plan.Device(Car)!.Goals[0].Ref;
        world.Currency.Goals[(Car, goal.Id, goal.Revision)] = new PlanGoalState(Offpeak, false);
        Assert.Null(world.View.For(Car, done));
    }

    [Fact]
    public void The_plan_class_may_open_a_grid_zone_only_when_the_goal_itself_declares_it()
    {
        // Why: class plan skips the price test. It is meant for the zone the user wrote into goal.sources; a goal that declares none, or another zone, leaves the
        // device's own sources in charge (the planner's value test is not the dispatch's to trust blindly).
        var world = new World();
        world.Republish(new PlanState(world.Plan, false, null, [], TimeSpan.FromDays(10)));
        var zone = world.Plan.Slots.Select(s => s.Slot.Start).First(t => world.Plan.EntryAt(Car, t) is { Want: PlanWant.Run, Class: { } c } && PlanClasses.IsZone(c));
        var goal = world.Plan.Device(Car)!.Goals[0].Ref;

        Assert.True(world.View.For(Car, zone)!.GoalAllowsZone);

        foreach (var sources in new SourceSet?[] { null, SourceSet.PvOnly })
        {
            world.Currency.Goals[(Car, goal.Id, goal.Revision)] = new PlanGoalState(sources, false);
            Assert.False(world.View.For(Car, zone)!.GoalAllowsZone);
        }
    }

    [Fact]
    public void The_fallback_is_logged_once_per_change_with_a_reason_and_no_entity_or_device()
    {
        // Why: PLAN-16. One line when dispatch stops using the plan and one when it may again, not one per step; the line is neither a decision nor a place for an
        // entity ID (CLAUDE.md: the core never knows one).
        var world = new World();
        var at = world.BothPlanned;
        world.View.Observe(at);
        Assert.Empty(world.Logs.Entries);

        world.Republish(PlanState.Empty);
        for (var i = 0; i < 5; i++)
        {
            world.View.Observe(at + TimeSpan.FromSeconds(i));
        }

        var entered = Assert.Single(world.Logs.Entries);
        Assert.Equal(LogLevel.Warning, entered.Level);
        Assert.Contains("plan_fallback entered", entered.Text, StringComparison.Ordinal);
        Assert.Contains("no_plan", entered.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("ev_charger", entered.Text, StringComparison.Ordinal);

        world.Republish(new PlanState(world.Plan, false, null, [], MaxAge));
        world.View.Observe(at);
        world.View.Observe(at + TimeSpan.FromSeconds(10));

        Assert.Equal(2, world.Logs.Entries.Count);
        Assert.Equal(LogLevel.Information, world.Logs.Entries[1].Level);
        Assert.Contains("plan_fallback left", world.Logs.Entries[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_global_reason_for_the_fallback_is_named_and_the_mode_off_says_nothing()
    {
        // Why: the log is how the maintainer learns why a plan was not used (stale, from the future, another configuration, suspended): the reasons differ, and
        // with planner.dispatch off there is nothing to report, as a plan nobody reads is not a fallback.
        var world = new World();
        var at = world.BothPlanned;
        var rows = new (string Reason, Action<World> Arrange, DateTimeOffset Now)[]
        {
            ("plan_too_old", _ => { }, world.Plan.ComputedAt + MaxAge + TimeSpan.FromMinutes(1)),
            ("plan_from_the_future", _ => { }, world.Plan.ComputedAt - TimeSpan.FromMinutes(1)),
            ("suspended", w => w.Republish(new PlanState(w.Plan, true, null, [], MaxAge)), at),
            ("configuration_changed", w => w.Currency.ConfigRevision = 99, at),
            ("outside_the_horizon", w => w.Republish(new PlanState(w.Plan, false, null, [], TimeSpan.FromDays(10))), world.Plan.HorizonEnd + TimeSpan.FromHours(1)),
        };
        foreach (var (reason, arrange, now) in rows)
        {
            var fresh = new World();
            arrange(fresh);

            fresh.View.Observe(now);

            Assert.Contains(reason, Assert.Single(fresh.Logs.Entries).Text, StringComparison.Ordinal);
        }

        world.Currency.Mode = PlannerDispatchMode.Off;
        world.Republish(PlanState.Empty);
        world.View.Observe(at);
        Assert.Empty(world.Logs.Entries);
    }

    [Fact]
    public void The_view_never_publishes_or_changes_the_plan()
    {
        // Why: dispatch reads the plan, the planner writes it (the holder's only writer is the service); the view is a pure judge of what is there.
        var world = new World();
        var before = world.Holder.Current;

        _ = world.View.For(Car, world.BothPlanned);
        world.View.Observe(world.BothPlanned);

        Assert.Same(before, world.Holder.Current);
        Assert.Equal(ImmutableArray<PlanNote>.Empty.Length, before.Notes.Length);
    }
}
