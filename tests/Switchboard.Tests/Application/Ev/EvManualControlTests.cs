using Switchboard.Application.Ev;
using Switchboard.Application.Goals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Ev;
using Switchboard.Domain.Goals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Ev;

/// <summary>
/// EV-09, the user's controls of the module (docs/EV.md S6, points 3.10 and B9). Why it matters: a person's "charge now" and "PV only" are the only things that
/// may override the module's own rules, so each must (a) change only the add-on's own state (ev.json, a manual goal), never an entity, (b) survive a restart where
/// it should and lapse by itself where it must (a forgotten "charge now" must not keep a car charging next week, an unplugged car must not hold a goal), (c) be
/// refused when it cannot work (charge now at <c>planner.dispatch: off</c> or <c>narrow</c> would promise a charge the plan never places) and (d) never let the
/// module and the manual goal double count the same energy (B8).
/// </summary>
public sealed class EvManualControlTests
{
    private const string Full = "planner:\n  dispatch: full\n";

    private static readonly DeviceName Car = DeviceName.Parse("ev_charger");

    private sealed class NoEvInputs(Func<Switchboard.Application.Configuration.ConfigurationResult> configuration) : Switchboard.Application.Signals.IEvInputSource
    {
        public Switchboard.Application.Signals.EvInputView Current => new(configuration(), Switchboard.Application.Signals.EvInputs.Empty);
    }

    private static EvRig PlugedAt(double soc, string planner = Full)
    {
        var rig = new EvRig(planner: planner);
        rig.PlugInCar(soc);
        rig.Tick();
        return rig;
    }

    [Fact]
    public void The_mode_is_set_at_once_persisted_on_the_next_computation_and_survives_a_restart()
    {
        // Why: PV only is a standing wish (the maintainer leaves the car on surplus for weeks); a restart of the add-on must not silently put the car back on the
        // grid. The engine is the only writer of ev.json, so the file follows on its next computation, not on the request.
        var rig = PlugedAt(40);

        Assert.Equal(EvControlResult.Done, rig.Control.SetMode(EvMode.PvOnly));
        Assert.Equal(EvMode.PvOnly, rig.Control.Manual.Mode);
        Assert.NotEqual(EvMode.PvOnly, rig.Saved?.Mode);

        var snapshot = rig.Tick();
        Assert.Equal((EvMode.PvOnly, EvAssessmentCode.PvOnly), (rig.Saved!.Mode, snapshot.Composition.Code));
        Assert.All(rig.Offer().Requests, r => Assert.Equal("pv_only", r.Sources!.ToText()));

        rig.Restart();
        rig.PushSoc(40);
        var restored = rig.Tick();

        Assert.Equal((EvMode.PvOnly, EvAssessmentCode.PvOnly), (restored.Mode, restored.Composition.Code));
        Assert.Equal(EvMode.PvOnly, rig.Control.Manual.Mode);
    }

    [Fact]
    public void Setting_the_same_mode_again_changes_nothing()
    {
        var rig = PlugedAt(40);
        rig.Control.SetMode(EvMode.PvOnly);
        var wakes = rig.Trigger.Wakes;

        Assert.Equal(EvControlResult.Unchanged, rig.Control.SetMode(EvMode.PvOnly));
        Assert.Equal(wakes, rig.Trigger.Wakes);
    }

    [Fact]
    public void Without_an_ev_section_nothing_can_be_set()
    {
        // Why: a control for a module that is not configured would claim something the add-on does not do; the API answers 404.
        EvRig? holder = null;
        var rig = new EvRig(ev: string.Empty, inputs: () => new NoEvInputs(() => holder!.H.Configuration));
        holder = rig;

        Assert.False(rig.Control.Configured);
        Assert.Equal(EvControlResult.NotConfigured, rig.Control.SetMode(EvMode.PvOnly));
        Assert.Equal(EvControlResult.NotConfigured, rig.Control.StartChargeNow(80, null));
        Assert.Equal(EvControlResult.NotConfigured, rig.Control.CancelChargeNow());
    }

    [Fact]
    public void A_requested_full_charge_offers_the_weekly_goal_on_any_day_and_is_done_once_the_car_is_full()
    {
        // Why: 2a, a long trip or a wish raises the target to 100 % whatever the day; and it is one-off, otherwise the battery would sit at 100 % for good.
        var rig = new EvRig(planner: Full);
        rig.AdvanceTo(EvRig.Local(2026, 11, 3, 10));
        rig.PlugInCar(40);
        rig.Tick();

        rig.Control.SetMode(EvMode.FullCharge);
        var snapshot = rig.Tick();

        Assert.Equal(EvAssessmentCode.Manual, snapshot.Composition.Code);
        var weekly = Assert.Single(snapshot.Composition.Goals, g => g.Kind == EvGoalKind.WeeklyFull);
        Assert.Equal(100.0, weekly.Request.TargetSoc);
        Assert.Equal(EvMode.FullCharge, rig.Control.Manual.Mode);
        Assert.Null(rig.Saved!.LastFullAt);

        rig.Advance(TimeSpan.FromMinutes(1));
        rig.PushSoc(100);
        rig.Tick();

        Assert.Equal(EvMode.Auto, rig.Control.Manual.Mode);
        Assert.Equal((EvMode.Auto, rig.Now), (rig.Saved!.Mode, rig.Saved.LastFullAt));
    }

    [Fact]
    public void Charge_now_is_refused_unless_the_plan_reaches_dispatch()
    {
        // Why: EV.md 3.10. At off or narrow no goal is ever placed on its own, so the click would promise a charge that never starts; the API answers 409 with the
        // hint to use the device's force_on override.
        foreach (var planner in new[] { "planner:\n  dispatch: off\n", "planner:\n  dispatch: narrow\n", string.Empty })
        {
            var rig = PlugedAt(40, planner);

            Assert.Equal(EvControlResult.NeedsFullDispatch, rig.Control.StartChargeNow(80, null));
            Assert.Empty(rig.GoalStore.Current.Goals);
            Assert.Null(rig.Control.Manual.ChargeNow);
        }
    }

    [Fact]
    public void Charge_now_is_a_manual_hard_goal_that_replaces_the_modules_goals_and_never_double_counts()
    {
        // Why: B8/B9. The module's own goals and a manual goal on one device would add up in the plan; the manual goal wins and the module offers nothing. The goal
        // runs on the module's SoC estimate and on the device's own sources, and is hard (a person asked), without margin; the plan makes it Urgent only when the
        // margin is within the escalation margin.
        var rig = PlugedAt(40);
        Assert.NotEmpty(rig.Offer().Requests);

        var result = rig.Control.StartChargeNow(80, TimeSpan.FromHours(3));

        Assert.Equal(EvControlResult.Done, result);
        var stored = rig.GoalStore.Current.Goals[Car];
        Assert.Equal((GoalKind.Soc, 80.0, true, rig.Now + TimeSpan.FromHours(3)), (stored.Kind, stored.Target, stored.Hard, stored.Deadline));
        var request = Assert.Single(rig.ManualOffer().Requests);
        Assert.Equal((GoalOrigin.Manual, true, true, TimeSpan.Zero, 80.0, 40.0), (request.Origin, request.Hard, request.Escalate, request.Margin, request.TargetSoc, request.CurrentSoc));
        Assert.Equal(40 / 100.0 * 18 * 1.10, request.RemainingKwh, 9);
        Assert.Equal("pv_and_zone:offpeak", request.Sources!.ToText());

        var snapshot = rig.Tick();
        Assert.Equal(EvAssessmentCode.Manual, snapshot.Composition.Code);
        Assert.Empty(rig.Offer().Requests);
        Assert.Equal((true, 80.0, rig.Now + TimeSpan.FromHours(3)), (rig.Saved!.ChargeNow is not null, rig.Saved.ChargeNow!.TargetPercent, rig.Saved.ChargeNow.ExpiresAt));
        Assert.True(rig.Trigger.Wakes > 0);
    }

    [Fact]
    public void Charge_now_takes_the_configured_target_and_duration_when_none_are_given()
    {
        var rig = PlugedAt(40);

        rig.Control.StartChargeNow(null, null);

        var p = rig.Configuration.Control.Ev.ChargeNow;
        var stored = rig.GoalStore.Current.Goals[Car];
        Assert.Equal((p.TargetPercent, rig.Now + p.Duration), (stored.Target, stored.Deadline));
    }

    [Theory]
    [InlineData(0.0, 3.0, EvControlResult.TargetOutOfRange)]
    [InlineData(101.0, 3.0, EvControlResult.TargetOutOfRange)]
    [InlineData(double.NaN, 3.0, EvControlResult.TargetOutOfRange)]
    [InlineData(80.0, 0.0, EvControlResult.DurationOutOfRange)]
    [InlineData(80.0, -1.0, EvControlResult.DurationOutOfRange)]
    [InlineData(80.0, 100000.0, EvControlResult.DeadlineBeyondHorizon)]
    [InlineData(30.0, 3.0, EvControlResult.AlreadyAtTarget)]
    [InlineData(40.0, 3.0, EvControlResult.AlreadyAtTarget)]
    public void Charge_now_refuses_what_cannot_work_and_stores_nothing(double target, double hours, EvControlResult expected)
    {
        // Why: a target outside (0, 100], a duration that is not positive or ends beyond the plan's horizon, and a target the car already has would each leave
        // either a goal nothing can meet or one that is met before it starts.
        var rig = PlugedAt(40);

        Assert.Equal(expected, rig.Control.StartChargeNow(target, TimeSpan.FromHours(hours)));
        Assert.Empty(rig.GoalStore.Current.Goals);
        Assert.Null(rig.Control.Manual.ChargeNow);
    }

    [Fact]
    public void Charge_now_is_refused_for_an_unplugged_car_and_without_a_soc_estimate()
    {
        var unplugged = PlugedAt(40);
        unplugged.PushPlugged("Idle");
        unplugged.Tick();
        Assert.Equal(EvControlResult.NotPlugged, unplugged.Control.StartChargeNow(80, null));

        var blind = new EvRig(planner: Full);
        blind.PushPlugged();
        blind.PushCalendar(null);
        blind.Tick();
        Assert.Equal(EvControlResult.SocUnknown, blind.Control.StartChargeNow(80, null));
        Assert.Empty(blind.GoalStore.Current.Goals);
    }

    [Fact]
    public void Charge_now_lapses_at_its_deadline_and_the_module_takes_over_again()
    {
        // Why: a forgotten "charge now" must not keep a car charging next week.
        var rig = PlugedAt(40);
        rig.Control.StartChargeNow(80, TimeSpan.FromMinutes(10));
        rig.Tick();
        Assert.NotNull(rig.Saved!.ChargeNow);

        rig.Cruise(TimeSpan.FromMinutes(11));
        rig.GoalStore.Expire(rig.Now);
        var snapshot = rig.Tick();

        Assert.Empty(rig.GoalStore.Current.Goals);
        Assert.Null(rig.Control.Manual.ChargeNow);
        Assert.Null(rig.Saved!.ChargeNow);
        Assert.NotEqual(EvAssessmentCode.Manual, snapshot.Composition.Code);
        Assert.NotEmpty(rig.Offer().Requests);
    }

    [Fact]
    public void Charge_now_ends_when_the_estimate_reaches_its_target()
    {
        // Why: "ends at the target". The manual goal's own opportunist on_met would otherwise carry on charging after the goal is met.
        var rig = PlugedAt(40);
        rig.Control.StartChargeNow(80, TimeSpan.FromHours(6));
        rig.Tick();

        rig.Advance(TimeSpan.FromMinutes(1));
        rig.PushSoc(81);
        rig.Tick();
        rig.GoalStore.Expire(rig.Now);
        rig.Tick();

        Assert.Empty(rig.GoalStore.Current.Goals);
        Assert.Null(rig.Control.Manual.ChargeNow);
        Assert.Null(rig.Saved!.ChargeNow);
    }

    [Fact]
    public void Charge_now_ends_when_the_car_is_unplugged_and_survives_an_unknown_state()
    {
        // Why: an unplugged car has nothing to charge; but an unreadable plugged state ends nothing (the goal is not dropped on a blip).
        var rig = PlugedAt(40);
        rig.Control.StartChargeNow(80, TimeSpan.FromHours(6));
        rig.Tick();

        rig.Advance(TimeSpan.FromMinutes(1));
        rig.PushPlugged("unavailable");
        rig.Tick();
        rig.GoalStore.Expire(rig.Now);
        Assert.Contains(Car, rig.GoalStore.Current.Goals.Keys);

        rig.Advance(TimeSpan.FromMinutes(1));
        rig.PushPlugged("Idle");
        rig.Tick();
        rig.GoalStore.Expire(rig.Now);
        rig.Tick();

        Assert.Empty(rig.GoalStore.Current.Goals);
        Assert.Null(rig.Control.Manual.ChargeNow);
    }

    [Fact]
    public void Charge_now_is_cancelled_by_the_user_and_cancelling_twice_says_so()
    {
        var rig = PlugedAt(40);
        rig.Control.StartChargeNow(80, TimeSpan.FromHours(6));

        Assert.Equal(EvControlResult.Done, rig.Control.CancelChargeNow());
        Assert.Empty(rig.GoalStore.Current.Goals);
        Assert.Null(rig.Control.Manual.ChargeNow);
        Assert.Equal(EvControlResult.NoChargeNow, rig.Control.CancelChargeNow());

        rig.Tick();
        Assert.NotEmpty(rig.Offer().Requests);
    }

    [Fact]
    public void A_charge_now_survives_a_restart_while_its_manual_goal_does()
    {
        // Why: the goal itself is persisted with the manual goals in state.json; ev.json remembers that it is a charge-now so the UI keeps saying so. Both must
        // come back together, and the module must not have cleared it in between.
        var rig = PlugedAt(40);
        rig.Control.StartChargeNow(80, TimeSpan.FromHours(6));
        rig.Tick();
        var expires = rig.Saved!.ChargeNow!.ExpiresAt;

        rig.Restart();
        rig.PushSoc(40);
        rig.Tick();

        Assert.Equal(expires, rig.Control.Manual.ChargeNow!.ExpiresAt);
        Assert.Contains(Car, rig.GoalStore.Current.Goals.Keys);
    }

    [Fact]
    public void A_manual_goal_on_the_ev_device_without_a_goal_block_is_accepted_only_for_the_modules_device()
    {
        // Why: B9. The synthetic SoC spec belongs to the module's device; a SoC goal for any other device without a goal block stays refused.
        var rig = new EvRig(planner: Full, extraDevices: "  - id: heater_l1\n    type: onoff\n    actuator: switch.heater_1\n    phase: L1\n    rated_current_a: 6.6\n    rated_power_w: 1500\n    value_per_kwh: 0.35\n");
        rig.PlugInCar(40);
        rig.Tick();

        Assert.Equal(ManualGoalResult.Begun, rig.GoalStore.Begin(Car, GoalKind.Soc, 70, rig.Now + TimeSpan.FromHours(2), hard: false));
        Assert.Equal(ManualGoalResult.SocNotConfigured, rig.GoalStore.Begin(DeviceName.Parse("heater_l1"), GoalKind.Soc, 70, rig.Now + TimeSpan.FromHours(2), hard: false));
        Assert.Equal(ManualGoalResult.Begun, rig.GoalStore.Begin(DeviceName.Parse("heater_l1"), GoalKind.Energy, 2, rig.Now + TimeSpan.FromHours(2), hard: false));
    }

    [Fact]
    public void Without_the_module_the_manual_goal_of_a_device_with_no_goal_block_still_needs_one()
    {
        var rig = new EvRig(ev: string.Empty);
        rig.PushSoc(40);

        Assert.Equal(ManualGoalResult.SocNotConfigured, rig.GoalStore.Begin(Car, GoalKind.Soc, 70, rig.Now + TimeSpan.FromHours(2), hard: false));
    }

    [Fact]
    public void A_stale_module_reading_makes_the_manual_soc_goal_inactive_never_computed_from_the_last_value()
    {
        // Why: EV.md section 9, SoC stale: goals inactive, never the last value (the same rule as the module's own goals).
        var rig = PlugedAt(40);
        rig.Control.StartChargeNow(80, TimeSpan.FromHours(6));

        rig.Advance(rig.Configuration.Control.Ev.SnapshotMaxAge + TimeSpan.FromSeconds(1));
        var offer = rig.ManualOffer();

        Assert.Empty(offer.Requests);
        Assert.Equal(GoalInactiveReason.InputStale, Assert.Single(offer.Inactive).Reason);
    }

    private sealed class AcceptingGoals : IManualGoalControl
    {
        public ManualGoalSnapshot Current => ManualGoalSnapshot.None;

        public int Begun { get; private set; }

        public ManualGoalResult Begin(DeviceName device, GoalKind kind, double target, DateTimeOffset deadline, bool hard)
        {
            Begun++;
            return ManualGoalResult.Begun;
        }

        public ManualGoalResult End(DeviceName device) => ManualGoalResult.NotFound;
    }

    private sealed class TickingGoals(ManualGoalStore store, Action tick) : IManualGoalControl
    {
        public ManualGoalSnapshot Current => store.Current;

        public ManualGoalResult Begin(DeviceName device, GoalKind kind, double target, DateTimeOffset deadline, bool hard)
        {
            var result = store.Begin(device, kind, target, deadline, hard);
            tick();
            return result;
        }

        public ManualGoalResult End(DeviceName device) => store.End(device);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(60)]
    public void Charge_now_checks_the_horizon_itself_not_only_through_the_goal_store(int minutesOver)
    {
        // Why: the request's duration is the module's to bound. A store that accepted any deadline (or one that changes its own rule) must not let a charge
        // through that the plan cannot see.
        var rig = PlugedAt(40);
        var goals = new AcceptingGoals();
        var control = new EvControl(rig.Manual, rig, rig.Keeper, goals, rig.Trigger, rig.Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<EvControl>.Instance);
        var horizon = rig.Configuration.Control.Planner.Horizon;

        Assert.Equal(EvControlResult.DeadlineBeyondHorizon, control.StartChargeNow(80, horizon + TimeSpan.FromMinutes(minutesOver)));
        Assert.Equal(EvControlResult.DeadlineBeyondHorizon, control.StartChargeNow(80, TimeSpan.MaxValue / 2));
        Assert.Equal(0, goals.Begun);
        Assert.Null(rig.Manual.Current.ChargeNow);
        Assert.Equal(EvControlResult.Done, control.StartChargeNow(80, horizon));
    }

    [Fact]
    public void Charge_now_ends_at_its_own_deadline_while_the_manual_goal_still_stands()
    {
        // Why: the state's expiry is the module's own clock, not the goal store's. A forgotten charge-now must stop being reported (and persisted) when its time is
        // up even if the store has not swept the goal yet.
        var rig = PlugedAt(40);
        rig.Control.StartChargeNow(80, TimeSpan.FromMinutes(10));
        rig.Tick();

        rig.Cruise(TimeSpan.FromMinutes(11));

        Assert.Contains(Car, rig.GoalStore.Current.Goals.Keys);
        Assert.Null(rig.Control.Manual.ChargeNow);
        Assert.Null(rig.Saved!.ChargeNow);
    }

    [Fact]
    public void A_computation_landing_between_the_goal_and_the_state_of_a_new_charge_now_does_not_clear_it()
    {
        // Why: charge now stores its goal first and its state second. A computation in between sees a goal and no charge-now; one right after sees both. Neither may
        // clear the charge-now on the strength of a goal that is not there yet.
        var rig = PlugedAt(40);
        var goals = new TickingGoals(rig.GoalStore, () => rig.Tick());
        var control = new EvControl(rig.Manual, rig, rig.Keeper, goals, rig.Trigger, rig.Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<EvControl>.Instance);

        Assert.Equal(EvControlResult.Done, control.StartChargeNow(80, TimeSpan.FromHours(6)));
        rig.Tick();

        Assert.NotNull(rig.Manual.Current.ChargeNow);
        Assert.Contains(Car, rig.GoalStore.Current.Goals.Keys);
        Assert.NotNull(rig.Saved!.ChargeNow);
    }

    [Fact]
    public void Cancelling_ends_a_manual_goal_the_state_has_forgotten()
    {
        // Why: the state can lag or be lost (ev.json gone, a request racing the engine). A goal left behind would charge to its target with nothing in the UI that
        // could cancel it: DELETE must end the goal whenever there is one.
        var rig = PlugedAt(40);
        rig.Control.StartChargeNow(80, TimeSpan.FromHours(6));
        rig.Manual.Update(m => m with { ChargeNow = null });
        Assert.Contains(Car, rig.GoalStore.Current.Goals.Keys);

        Assert.Equal(EvControlResult.Done, rig.Control.CancelChargeNow());
        Assert.Empty(rig.GoalStore.Current.Goals);
        Assert.Equal(EvControlResult.NoChargeNow, rig.Control.CancelChargeNow());
    }

    [Fact]
    public void A_field_the_user_changed_before_the_engine_seeded_wins_but_the_other_comes_from_the_file()
    {
        // Why: a charge-now asked for before the first computation must not throw away the mode ev.json held (a standing "PV only"), and a mode set first must not
        // throw away a running charge-now.
        var charge = new EvChargeNow(DateTimeOffset.UnixEpoch, 80, DateTimeOffset.UnixEpoch.AddHours(6));
        var other = new EvChargeNow(DateTimeOffset.UnixEpoch, 70, DateTimeOffset.UnixEpoch.AddHours(3));

        var chargeFirst = new EvManualState();
        chargeFirst.Update(m => m with { ChargeNow = other });
        chargeFirst.Seed(EvMode.PvOnly, charge);
        Assert.Equal(new EvManual(EvMode.PvOnly, other), chargeFirst.Current);

        var modeFirst = new EvManualState();
        modeFirst.Update(m => m with { Mode = EvMode.FullCharge });
        modeFirst.Seed(EvMode.PvOnly, charge);
        Assert.Equal(new EvManual(EvMode.FullCharge, charge), modeFirst.Current);

        modeFirst.Seed(EvMode.Auto, null);
        Assert.Equal(new EvManual(EvMode.FullCharge, charge), modeFirst.Current);
    }

    [Fact]
    public void The_module_reads_the_soc_of_its_own_device_only()
    {
        // Why: the manual goal of another device must never take the car's estimate as its own state of charge.
        var rig = PlugedAt(40);
        var source = new EvModuleSocSource(() => rig.Keeper, rig.Clock);

        Assert.Equal(40, source.Read(Car)!.Value.Percent!.Value, 0);
        Assert.Null(source.Read(DeviceName.Parse("heater_l1")));
    }
}
