using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Planning;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Planning;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.EconomicsScenarios;

namespace Switchboard.Tests.Application.Dispatch;

/// <summary>
/// "How the plan meets the gates" (15-PLANNER), one intent per row, on the virtual clock in the Warsaw zone with a G13-like tariff: what a plan entry may do to a
/// decision, and, as important, what it may not. The plan is scripted (<see cref="ScriptedPlanView"/>: the entry dispatch is handed once the plan view has judged
/// the plan valid; <see cref="PlanViewTests"/> holds that judging). Each row is the same world as a v2 row of <see cref="EconomicsAllowanceTests"/> with and without an
/// entry, so what the entry changed is visible against what v2 did.
/// </summary>
public sealed class PlanDispatchGateTests
{
    private static readonly DeviceName Dev = DeviceName.Parse("dev");
    private static readonly DateTimeOffset Night = Workday(23);
    private static readonly DateTimeOffset Morning = Workday(11);

    private sealed record Outcome(GuardScenario S)
    {
        public bool Started(string device = "dev") => Starts(S, device).Any();

        public bool Stopped(string device = "dev") => Stops(S, device).Any();

        public ReasonCode? Pending(string device = "dev") => EconomicsScenarios.Pending(S, device);

        public Decision FirstStart(string device = "dev") => Starts(S, device).First();

        public PlanDecisionInputs? Block(string device = "dev") => S.Dispatches[^1].Wish(DeviceName.Parse(device))!.Inputs.Plan;
    }

    private static Outcome Play(
        string devices,
        DateTimeOffset at,
        string[] off,
        PlannerDispatchMode mode,
        Action<GuardScenario, ScriptedPlanView>? plan = null,
        double wh = -100,
        double export = -500,
        double l1 = 1,
        double minutes = 3,
        string control = OneBoundary)
    {
        var view = new ScriptedPlanView(mode);
        var s = Create(devices, at, off, Economics(), control, plan: view);
        plan?.Invoke(s, view);
        Export(s, export);
        var feed = Feed(s, wh);
        feed();
        s.PollUntil(s.Now + TimeSpan.FromMinutes(minutes), l1, 1, 1, feed);
        return new Outcome(s);
    }

    private static Action<GuardScenario, ScriptedPlanView> Entry(PlanWant want, string? klass = null, double energyKwh = 5, double maxPowerW = 10_000, bool goalAllowsZone = true, string device = "dev") =>
        (s, view) => view.Entries[DeviceName.Parse(device)] = ScriptedPlanView.Entry(s.Now, want, klass, energyKwh, maxPowerW, goalAllowsZone);

    private static string Car(string sources, double value = 2.03, string extra = "") => Device("dev", "L1", 6.6, 1500, value, sources, extra);

    private const string Offpeak = "zone:offpeak";

    // Defer ---------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(PlannerDispatchMode.Narrow)]
    [InlineData(PlannerDispatchMode.Full)]
    public void Defer_refuses_a_zone_class_start_with_plan_deferred_where_v2_starts_it(PlannerDispatchMode mode)
    {
        // Why: the plan found cheaper energy later (a PV hour tomorrow); buying the same kWh from the zone now is exactly what the goal's cost saving is made of.
        var v2 = Play(Car("pv_and_zone:offpeak"), Night, ["dev"], PlannerDispatchMode.Off, Entry(PlanWant.Defer));
        Assert.Equal(ReasonCode.ZoneAllowed, v2.FirstStart().Reason);

        var planned = Play(Car("pv_and_zone:offpeak"), Night, ["dev"], mode, Entry(PlanWant.Defer));

        Assert.False(planned.Started());
        Assert.Equal(ReasonCode.PlanDeferred, planned.Pending());
        Assert.Equal("defer", planned.Block()!.Want);
    }

    [Fact]
    public void Defer_does_not_hold_back_the_surplus_class_it_only_narrows_the_zone()
    {
        // Why: "Defer blocks only the zone class". Surplus that exists now is free energy the plan never forbids; refusing it would waste PV the forecast
        // may have got wrong.
        var s = Play(Car("any"), Night, ["dev"], PlannerDispatchMode.Narrow, Entry(PlanWant.Defer), wh: 600, export: 3000);

        var start = s.FirstStart();

        Assert.Equal(ReasonCode.BalanceSurplus, start.Reason);
        Assert.Equal(Switchboard.Domain.Economics.EnergyClass.Pv, start.Inputs.Dispatch!.Economics!.Class);
    }

    [Fact]
    public void Defer_does_not_stop_a_device_already_running_on_the_zone()
    {
        // Why: Defer refuses a start or an increase, not a run in progress; stopping a charger mid-session on a forecast is the stop-start the lockouts exist for.
        var s = Play(Car("pv_and_zone:offpeak"), Night, [], PlannerDispatchMode.Full, Entry(PlanWant.Defer), wh: 100, export: 0);

        Assert.False(s.Stopped());
    }

    // Done ----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Done_stops_a_running_device_with_goal_met_where_v2_keeps_it()
    {
        // Why: on_met: stop makes the target a cap: a charger that reached its target must not keep charging because the zone is cheap.
        var v2 = Play(Car("pv_and_zone:offpeak"), Night, [], PlannerDispatchMode.Off, Entry(PlanWant.Done), wh: 100, export: 0);
        Assert.False(v2.Stopped());

        var planned = Play(Car("pv_and_zone:offpeak"), Night, [], PlannerDispatchMode.Narrow, Entry(PlanWant.Done), wh: 100, export: 0);

        var stop = Stops(planned.S, "dev").First();
        Assert.Equal(ReasonCode.GoalMet, stop.Reason);
        Assert.Equal("done", stop.Inputs.Dispatch!.Plan!.Want);
    }

    [Fact]
    public void Done_refuses_every_class_for_a_device_that_is_off()
    {
        // Why: "refuses every class": neither the surplus nor the zone may start a device whose goal is met and set to stay off.
        foreach (var (sources, wh, export) in new[] { ("any", 600.0, 3000.0), ("pv_and_zone:offpeak", -100.0, -500.0) })
        {
            var planned = Play(Car(sources), Night, ["dev"], PlannerDispatchMode.Narrow, Entry(PlanWant.Done), wh, export);

            Assert.False(planned.Started());
            Assert.Equal(ReasonCode.GoalMet, planned.Pending());
        }
    }

    // Slot budget and ceiling ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_run_entry_whose_budget_is_delivered_refuses_the_next_increase_with_plan_slot_target()
    {
        // Why: the slot's energy is the plan's estimate of what is worth buying; once it is delivered, another kWh is energy the plan placed in a later slot.
        var s = Play(Car("pv_and_zone:offpeak"), Night, ["dev"], PlannerDispatchMode.Narrow, Entry(PlanWant.Run, "zone:offpeak", energyKwh: 0));

        Assert.False(s.Started());
        Assert.Equal(ReasonCode.PlanSlotTarget, s.Pending());
    }

    [Fact]
    public void A_run_entry_with_budget_left_changes_nothing_in_narrow()
    {
        // Why: narrow only narrows: a slot that still has energy to place is exactly what v2 would have done.
        var v2 = Play(Car("pv_and_zone:offpeak"), Night, ["dev"], PlannerDispatchMode.Off);
        var planned = Play(Car("pv_and_zone:offpeak"), Night, ["dev"], PlannerDispatchMode.Narrow, Entry(PlanWant.Run, "zone:offpeak", energyKwh: 5));

        Assert.Equal(v2.FirstStart().At, planned.FirstStart().At);
        Assert.Equal(ReasonCode.ZoneAllowed, planned.FirstStart().Reason);
    }

    [Fact]
    public void The_slot_ceiling_below_a_devices_power_keeps_it_from_starting()
    {
        // Why: the ceiling is what the static budget leaves of the phase (and the device's range); a ceiling under the smallest level is a slot the device must not run in.
        var s = Play(Car("pv_and_zone:offpeak"), Night, ["dev"], PlannerDispatchMode.Narrow, Entry(PlanWant.Run, "zone:offpeak", maxPowerW: 1000));

        Assert.False(s.Started());
        Assert.Equal(ReasonCode.PlanSlotTarget, s.Pending());
    }

    [Fact]
    public void The_delivered_energy_of_the_slot_is_counted_from_the_devices_power_and_shown_in_the_block()
    {
        // Why: the budget is only as good as the count: a relay on at its rated 1.5 kW for three minutes has drawn 75 Wh, and the decision says so.
        var s = Play(Car("pv_and_zone:offpeak"), Night, [], PlannerDispatchMode.Narrow, Entry(PlanWant.Run, "zone:offpeak", energyKwh: 5), wh: 100, export: 0);

        var delivered = s.Block()!.DeliveredKwh;

        Assert.InRange(delivered, 0.05, 0.08);
    }

    // The plan class (full) -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Narrow_never_opens_a_class_a_run_entry_on_a_zone_the_device_may_not_use_starts_nothing()
    {
        // Why: "narrow: nothing starts that v2 would not start". The plan's own class exists in full mode only.
        var s = Play(Car("pv_only", 0.35), Night, ["dev"], PlannerDispatchMode.Narrow, Entry(PlanWant.Run, "zone:offpeak"));

        Assert.False(s.Started());
        Assert.Equal(ReasonCode.SourceNotAllowed, s.Pending());
    }

    [Fact]
    public void Full_opens_the_plan_class_for_a_run_entry_on_a_grid_zone_with_the_reason_plan_slot_target()
    {
        // Why: the planner placed this goal's energy on the off-peak zone, which the goal declares; the device's own sources (pv_only) are the v2 opportunist's.
        var s = Play(Car("pv_only", 0.35), Night, ["dev"], PlannerDispatchMode.Full, Entry(PlanWant.Run, "zone:offpeak"));

        var start = s.FirstStart();

        Assert.Equal(ReasonCode.PlanSlotTarget, start.Reason);
        Assert.Equal(Switchboard.Domain.Economics.EnergyClass.Zone, start.Inputs.Dispatch!.Economics!.Class);
        Assert.Equal(("run", "zone:offpeak", "full"), (start.Inputs.Dispatch.Plan!.Want, start.Inputs.Dispatch.Plan.Class, start.Inputs.Dispatch.Plan.Mode));
    }

    [Fact]
    public void The_plan_class_skips_the_price_test_and_the_zone_end_margin_because_the_planner_did_them()
    {
        // Why: the planner placed the slot knowing the price and where the zone ends (a slot is a slot, not a minute). Asked again at run time against v2's tests the
        // same slot could refuse: a value of 0.35 against 0.60, or the last five minutes of the zone.
        var cheap = Play(Car("pv_and_zone:offpeak", 0.35), Night, ["dev"], PlannerDispatchMode.Off);
        Assert.Equal(ReasonCode.ValueBelowCost, cheap.Pending());
        var late = Play(Car("pv_and_zone:offpeak"), Workday(6, 55), ["dev"], PlannerDispatchMode.Off);
        Assert.Equal(ReasonCode.SourceNotAllowed, late.Pending());

        var byPrice = Play(Car("pv_and_zone:offpeak", 0.35), Night, ["dev"], PlannerDispatchMode.Full, Entry(PlanWant.Run, "zone:offpeak"));
        var byMargin = Play(Car("pv_and_zone:offpeak"), Workday(6, 55), ["dev"], PlannerDispatchMode.Full, Entry(PlanWant.Run, "zone:offpeak"));

        Assert.Equal(ReasonCode.PlanSlotTarget, byPrice.FirstStart().Reason);
        Assert.Equal(ReasonCode.PlanSlotTarget, byMargin.FirstStart().Reason);
    }

    [Fact]
    public void The_plan_class_needs_the_zone_in_the_goals_own_sources_and_a_grid_class()
    {
        // Why: the class opens one door: a goal's declared zone. A pv entry opens nothing (surplus is the allowance's), and a zone the goal does not declare is not
        // the plan's to open.
        var notDeclared = Play(Car("pv_only", 0.35), Night, ["dev"], PlannerDispatchMode.Full, Entry(PlanWant.Run, "zone:offpeak", goalAllowsZone: false));
        var surplusEntry = Play(Car("pv_only", 0.35), Night, ["dev"], PlannerDispatchMode.Full, Entry(PlanWant.Run, "pv"));

        Assert.False(notDeclared.Started());
        Assert.False(surplusEntry.Started());
    }

    [Fact]
    public void A_device_started_on_the_plan_class_is_not_stopped_by_the_price_test_the_next_step()
    {
        // Why: a plan class that started a charger and a gate that stopped it one cycle later for "value below cost" would be a stop-start loop through the
        // lockouts; the entry that opened the class keeps the device on it for the slot.
        var s = Play(Car("pv_and_zone:offpeak", 0.35), Night, [], PlannerDispatchMode.Full, Entry(PlanWant.Run, "zone:offpeak"), wh: 100, export: 0, minutes: 6);
        var v2 = Play(Car("pv_and_zone:offpeak", 0.35), Night, [], PlannerDispatchMode.Off, wh: 100, export: 0, minutes: 6);

        Assert.True(v2.Stopped());
        Assert.False(s.Stopped());
        Assert.Equal(Switchboard.Domain.Economics.EnergyClass.Zone, s.S.Dispatches[^1].Economics!.Devices.Single(d => d.Device == Dev).Class);
    }

    // Urgent --------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Urgent_opens_every_zone_in_full_where_the_devices_sources_would_not()
    {
        // Why: a hard goal that would miss its deadline is worth more than the zone price: the morning peak, which a pv_only device never uses, is open to it.
        var v2 = Play(Car("pv_only", 0.35), Morning, ["dev"], PlannerDispatchMode.Off);
        Assert.Equal(ReasonCode.SourceNotAllowed, v2.Pending());

        var s = Play(Car("pv_only", 0.35), Morning, ["dev"], PlannerDispatchMode.Full, Entry(PlanWant.Urgent, "zone:morning_peak"));

        Assert.Equal(ReasonCode.GoalDeadlineUrgent, s.FirstStart().Reason);
        Assert.Equal("urgent", s.FirstStart().Inputs.Dispatch!.Plan!.Want);
    }

    [Fact]
    public void Urgent_in_narrow_is_only_a_ceiling()
    {
        // Why: narrow never adds a class: the escalation is the first thing the maintainer enables last (full), after a shadow week of narrow.
        var s = Play(Car("pv_only", 0.35), Morning, ["dev"], PlannerDispatchMode.Narrow, Entry(PlanWant.Urgent, "zone:morning_peak"));

        Assert.False(s.Started());
        Assert.Equal(ReasonCode.SourceNotAllowed, s.Pending());
    }

    [Fact]
    public void An_urgent_device_is_first_in_the_start_walk_in_full_and_not_in_narrow()
    {
        // Why: with headroom for one, the deadline at risk is served before the more valuable opportunist.
        var devices = Device("dev_hi", "L1", 6.6, 1500, 2.03, "any") + Device("dev", "L1", 6.6, 1500, 0.35, "pv_only");
        var off = new[] { "dev_hi", "dev" };

        var v2 = Play(devices, Morning, off, PlannerDispatchMode.Off);
        var narrow = Play(devices, Morning, off, PlannerDispatchMode.Narrow, Entry(PlanWant.Urgent, "zone:morning_peak"));
        var full = Play(devices, Morning, off, PlannerDispatchMode.Full, Entry(PlanWant.Urgent, "zone:morning_peak"));

        Assert.Equal((1, 2), (Rank(v2.S, "dev_hi")!.Value, Rank(v2.S, "dev")!.Value));
        Assert.Equal((1, 2), (Rank(narrow.S, "dev_hi")!.Value, Rank(narrow.S, "dev")!.Value));
        Assert.Equal((2, 1), (Rank(full.S, "dev_hi")!.Value, Rank(full.S, "dev")!.Value));
    }

    // What the plan never does --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_restore_only_device_is_never_started_by_the_plan_in_any_mode()
    {
        // Why: restore_only means Switchboard only gives back what it took; no planner, goal or urgency may start such a device (15-PLANNER).
        foreach (var want in new[] { PlanWant.Run, PlanWant.Urgent })
        {
            var s = Play(Car("any", extra: "    start: restore_only\n"), Morning, ["dev"], PlannerDispatchMode.Full, Entry(want, "zone:morning_peak"));

            Assert.False(s.Started());
        }
    }

    [Fact]
    public void The_plan_never_starts_a_device_the_guard_does_not_permit_to_increase()
    {
        // Why: protection outranks everything: with L1 in an overload the envelope allows no increase, and an urgent plan in full mode is still only a want.
        var s = Play(Car("pv_only", 0.35), Morning, ["dev"], PlannerDispatchMode.Full, Entry(PlanWant.Urgent, "zone:morning_peak"), l1: 32);

        Assert.False(s.Started());
    }

    [Fact]
    public void The_plan_class_waits_for_persistence_exactly_as_the_zone_class_does()
    {
        // Why: persistence, lockouts and settling are not the plan's to bypass: an urgent start comes at the same boundary as a v2 zone start in the same world, not before.
        var v2 = Play(Car("any"), Morning, ["dev"], PlannerDispatchMode.Off, control: string.Empty);
        var urgent = Play(Car("pv_only", 0.35), Morning, ["dev"], PlannerDispatchMode.Full, Entry(PlanWant.Urgent, "zone:morning_peak"), control: string.Empty);

        Assert.Equal(v2.FirstStart().At, urgent.FirstStart().At);
        Assert.True(urgent.FirstStart().At > Morning);
    }

    [Fact]
    public void A_device_in_an_override_has_no_entry_and_no_plan_block()
    {
        // Why: "Manual overrides outrank allocation"; the plan never outranks an override, so it must not even be asked about one.
        var view = new ScriptedPlanView(PlannerDispatchMode.Full);
        var s = Create(Car("pv_only", 0.35), Morning, ["dev"], Economics(), OneBoundary, plan: view);
        view.Entries[Dev] = ScriptedPlanView.Entry(s.Now, PlanWant.Urgent, "zone:morning_peak");
        s.Override("dev", OverrideMode.ForceOff);
        Export(s, -500);
        var feed = Feed(s, -100);
        feed();
        s.PollUntil(s.Now + TimeSpan.FromMinutes(3), 1, 1, 1, feed);

        Assert.All(s.Dispatches.SelectMany(d => d.Wishes), w => Assert.Null(w.Inputs.Plan));
        Assert.False(Starts(s, "dev").Any());
    }

    [Fact]
    public void With_the_mode_off_no_decision_carries_a_plan_block_and_the_view_is_not_asked_for_an_entry()
    {
        // Why: off is v2 byte for byte (PLAN-14): no block in the log line, and no work done for a plan nobody reads.
        var view = new ScriptedPlanView(PlannerDispatchMode.Off);
        var s = Create(Car("pv_and_zone:offpeak"), Night, ["dev"], Economics(), OneBoundary, plan: view);
        view.Entries[Dev] = ScriptedPlanView.Entry(s.Now, PlanWant.Defer);
        Export(s, -500);
        var feed = Feed(s, -100);
        feed();
        s.PollUntil(s.Now + TimeSpan.FromMinutes(3), 1, 1, 1, feed);

        Assert.All(s.Decisions.Where(d => d.Inputs.Dispatch is not null), d => Assert.Null(d.Inputs.Dispatch!.Plan));
        Assert.DoesNotContain("\"plan\"", string.Concat(s.Decisions.Select(Switchboard.Infrastructure.Persistence.DecisionJson.Serialize)), StringComparison.Ordinal);
        Assert.Equal(0, view.Asked);
        Assert.True(view.Observed > 0);
    }
}
