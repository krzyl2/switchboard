using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Configuration;
using Switchboard.Application.Ev;
using Switchboard.Application.Goals;
using Switchboard.Application.Planning;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Ev;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Planning;
using Switchboard.Domain.Signals;
using Switchboard.Host.Web;
using Switchboard.Infrastructure.Persistence;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.PlannerFixtures;

namespace Switchboard.Tests.Application.Ev;

/// <summary>
/// The EV failure table (docs/EV.md section 9, EV-08): every row has a named test, and each one says why the reaction matters. The module runs here as it runs
/// in the add-on, minus the guard: a real signal store feeds the EV inputs, the engine computes beside the control loop, and the goal source answers what the
/// guard's step would see. Index (row of the table: test):
/// <list type="number">
/// <item>SoC stale or unavailable: <see cref="Row01_soc_stale_goals_are_inactive_never_the_last_value"/>, <see cref="Row01_without_charger_data_a_plugged_car_loses_the_estimate_at_once"/></item>
/// <item>Range stale, 0 or unknown: <see cref="Row02_range_zero_uses_the_fallback_and_rejects_the_sample"/>, <see cref="Row02_a_usable_pair_is_learned_and_saved"/></item>
/// <item>Calendar unavailable: <see cref="Row03_calendar_unavailable_keeps_the_last_trip_until_its_start"/></item>
/// <item>Calendar not updated for long: <see cref="Row04_a_calendar_older_than_its_max_age_is_unavailable_and_the_trip_stands"/></item>
/// <item>Event without km, all-day, out of bounds, hiding the next trip: <see cref="Row05_an_event_without_km_is_not_a_trip_and_is_listed_as_unparsed"/></item>
/// <item>Trip in progress or departure passed (B4): <see cref="Row06_a_trip_disappears_the_moment_its_start_is_reached"/></item>
/// <item>Trip added just before departure: <see cref="Row07_a_trip_added_just_before_departure_is_infeasible_with_a_shortfall"/></item>
/// <item>No time at home before the departure: <see cref="Row08_no_time_before_the_departure_is_infeasible_and_still_pursued"/></item>
/// <item>Not plugged: <see cref="Row09_an_unplugged_car_gets_no_goal_and_nothing_is_reserved"/></item>
/// <item>Plugged but the charger is unavailable: <see cref="Row10_an_unreadable_plugged_state_leaves_the_goal_inactive"/></item>
/// <item>PV forecast missing: <see cref="Row11_a_missing_pv_forecast_is_a_note_and_the_hard_goal_still_escalates"/></item>
/// <item>Clock step, DST, weekly day in local time: <see cref="Row12_midnights_follow_the_local_day_on_23_and_25_hour_days"/></item>
/// <item>Add-on zone differs from the calendar's: <see cref="Row13_a_calendar_zone_that_disagrees_is_an_issue"/></item>
/// <item>Restart: <see cref="Row14_a_restart_restores_ev_json_and_offers_nothing_until_the_first_computation"/></item>
/// <item>ev.json corrupt: <see cref="Row15_a_corrupt_ev_json_is_set_aside_and_the_module_starts_from_defaults"/></item>
/// <item>SoC jump or the same value again: <see cref="Row16_the_same_value_again_keeps_the_anchor_and_a_changed_one_resets_it"/></item>
/// <item>Air conditioning on a plugged car: <see cref="Row17_energy_the_soc_never_saw_drops_the_estimate_at_the_drift_limit"/></item>
/// <item>Configuration change: <see cref="Row18_a_snapshot_for_another_device_is_not_offered"/></item>
/// <item>Goal source throws (B1): <see cref="Row19_a_computation_that_throws_is_a_failed_offer_and_never_reaches_the_caller"/>, <see cref="Row19_a_failed_offer_degrades_the_board"/></item>
/// <item>Protection and L1: <see cref="Row20_the_module_can_only_ask_for_energy_by_a_deadline"/></item>
/// </list>
/// </summary>
public sealed class EvGoalSourceFailureTableTests
{
    private static GoalRequest Single(GoalOffer offer, double? targetSoc) => offer.Requests.Single(r => r.TargetSoc == targetSoc);

    // ---- 1 ----

    [Fact]
    public void Row01_soc_stale_goals_are_inactive_never_the_last_value()
    {
        // Why: B10. The estimate between readings is allowed only while the car's last report is younger than ev.soc_max_age; one second past it a goal stated from
        // the old value would plan energy for a battery nobody has seen for hours, so the goal is inactive and the car is v2 again.
        var rig = new EvRig();
        rig.PlugInCar(40);
        Assert.NotEmpty(rig.TickAndOffer().Requests);

        var maxAge = rig.Configuration.Control.Ev.SocMaxAge;
        var steps = (int)(maxAge.TotalMinutes) - 1;
        for (var i = 0; i < steps; i++)
        {
            rig.Advance(TimeSpan.FromMinutes(1));
            rig.PushPlugged();
            rig.Tick();
        }

        Assert.NotEmpty(rig.Offer().Requests);

        rig.Advance(TimeSpan.FromMinutes(2));
        var offer = rig.TickAndOffer();

        Assert.Empty(offer.Requests);
        var inactive = Assert.Single(offer.Inactive);
        Assert.Equal((GoalInactiveReason.InputStale, GoalInputKind.Soc, GoalOrigin.Module), (inactive.Reason, inactive.Input, inactive.Origin));
        Assert.Equal(EvAssessmentCode.SocStale, rig.Keeper.Current.Snapshot!.Composition.Code);
    }

    [Fact]
    public void Row01_without_charger_data_a_plugged_car_loses_the_estimate_at_once()
    {
        // Why: energy may have flowed unseen while the charger's power was unknown, so the estimate is dropped until the car reports a changed value (an estimate that
        // silently ignores a charge is the one error that plans too little).
        var rig = new EvRig();
        rig.PlugInCar(40);
        Assert.Equal(40, rig.Tick().SocPercent);

        rig.ChargerWatts = null;
        rig.Advance(TimeSpan.FromMinutes(1));

        var snapshot = rig.Tick();
        Assert.Null(snapshot.SocPercent);
        Assert.Equal(SocEstimateGap.ChargerStale, snapshot.SocGap);
    }

    [Fact]
    public void Row01_a_parked_unplugged_car_keeps_its_estimate_through_a_charger_blip()
    {
        // Why: the known gap of S3. A stale charger entity says nothing about energy while nothing is plugged in; the estimate must survive the blip and the car
        // must not start from nothing when it is plugged in again with fresh charger data.
        var rig = new EvRig();
        rig.PlugInCar(40);
        rig.PushPlugged("Idle");
        Assert.Equal(40, rig.Tick().SocPercent);

        rig.ChargerWatts = null;
        rig.Advance(TimeSpan.FromMinutes(1));
        rig.PushPlugged("Idle");
        Assert.Equal(40, rig.Tick().SocPercent);

        rig.ChargerWatts = 0;
        rig.PushPlugged();
        Assert.Equal(40, rig.Tick().SocPercent);
    }

    // ---- 2 ----

    [Fact]
    public void Row02_range_zero_uses_the_fallback_and_rejects_the_sample()
    {
        // Why: a PHEV with a depleted buffer reports a range of 0; dividing by it would poison the learned consumption. The fallback stands, nothing is saved, and
        // the module says so.
        var rig = new EvRig();
        rig.PlugInCar(60);
        rig.PushRange(0);

        var snapshot = rig.Tick();

        Assert.Contains(EvAssessmentCode.RangeUnusable, snapshot.Composition.Notes);
        Assert.Equal(rig.Configuration.Control.Ev.FallbackKwhPerKm, snapshot.KwhPerKm);
        Assert.Equal(0, rig.Saves);
    }

    [Fact]
    public void Row02_a_usable_pair_is_learned_and_saved()
    {
        // Why: the counterpart of the row above: with a plausible pair taken outside charging the consumption is learned (60 % of 18 kWh over 54 km is 0.2 kWh/km)
        // and survives a restart because it is in ev.json.
        var rig = new EvRig();
        rig.PlugInCar(60);
        rig.PushRange(45);

        var snapshot = rig.Tick();

        Assert.Equal(60.0 / 100 * 18 / 45, snapshot.KwhPerKm, 9);
        Assert.Equal(1, rig.Saves);
        Assert.Equal(snapshot.KwhPerKm, rig.Saved!.Learned.Smoothed!.Value, 9);
    }

    // ---- 3 ----

    [Fact]
    public void Row03_calendar_unavailable_keeps_the_last_trip_until_its_start()
    {
        // Why: a calendar that drops out must not delete the trip the car is being charged for; but once its start has passed the trip is gone whatever the calendar says.
        var rig = new EvRig();
        rig.PlugInCar(40);
        var departure = rig.Trip(TimeSpan.FromHours(4), 42);
        rig.PushCalendar(departure);
        Assert.Equal(EvAssessmentCode.TripDeadline, rig.Tick().Composition.Code);

        rig.Advance(TimeSpan.FromMinutes(1));
        rig.PushCalendar(null, state: "unavailable");
        var offer = rig.TickAndOffer();

        var snapshot = rig.Keeper.Current.Snapshot!;
        Assert.Contains(EvAssessmentCode.CalendarUnavailable, snapshot.Composition.Notes);
        Assert.Contains(offer.Requests, r => r.Hard && r.Deadline == departure.Start);

        rig.Cruise(departure.Start - rig.Now + TimeSpan.FromMinutes(1));
        var after = rig.TickAndOffer();

        Assert.Null(rig.Keeper.Current.Snapshot!.NextTrip);
        Assert.DoesNotContain(after.Requests, r => r.Deadline == departure.Start);
    }

    // ---- 4 ----

    [Fact]
    public void Row04_a_calendar_older_than_its_max_age_is_unavailable_and_the_trip_stands()
    {
        // Why: the calendar entity reports rarely; its declared max_age (24 h here) decides when silence means unavailable. Inside it the trip is plain data; past it
        // the module says so and keeps the last parsed trip until it starts.
        var rig = new EvRig();
        rig.PlugInCar(40);
        rig.PushCalendar(rig.Trip(TimeSpan.FromHours(40), 42));
        rig.Tick();

        rig.Advance(TimeSpan.FromHours(23));
        rig.PushSoc(40);
        rig.PushPlugged();
        var inside = rig.Tick();
        Assert.DoesNotContain(EvAssessmentCode.CalendarUnavailable, inside.Composition.Notes);
        Assert.NotNull(inside.NextTrip);

        rig.Advance(TimeSpan.FromHours(2));
        rig.PushSoc(40);
        rig.PushPlugged();
        var past = rig.Tick();
        Assert.Contains(EvAssessmentCode.CalendarUnavailable, past.Composition.Notes);
        Assert.NotNull(past.NextTrip);
    }

    // ---- 5 ----

    [Fact]
    public void Row05_an_event_without_km_is_not_a_trip_and_is_listed_as_unparsed()
    {
        // Why: guessing a distance would plan energy for a trip nobody described. The event is ignored, no trip goal exists, and the UI lists it so the user can add km.
        var rig = new EvRig();
        rig.PlugInCar(40);
        rig.PushCalendar(new CalendarEvent("Praca", "bez dystansu", rig.Now + TimeSpan.FromHours(5), rig.Now + TimeSpan.FromHours(6), false));

        var offer = rig.TickAndOffer();

        Assert.DoesNotContain(offer.Requests, r => r.Hard && r.Deadline is not null);
        Assert.NotEqual(EvAssessmentCode.TripDeadline, rig.Keeper.Current.Snapshot!.Composition.Code);
        var trips = EvEndpoint.Build(rig.H.Configuration, rig.Now, ((IEvInputSource)rig).Current.Inputs, EvRig.Warsaw, rig.Keeper.Current).Trips!;
        Assert.Null(trips.NextTrip);
        var unparsed = Assert.Single(trips.Unparsed);
        Assert.Equal("no_km", unparsed.Reason);
        Assert.Single(trips.Warnings);
    }

    // ---- 6 ----

    [Fact]
    public void Row06_a_trip_disappears_the_moment_its_start_is_reached()
    {
        // Why: B4. The calendar entity shows the current event, so a trip in progress must not become a hard goal whose deadline is in the past: that goal is
        // "missed" and would run in every zone, peak included, at full power. The source drops it by the clock, even from a snapshot computed a moment ago.
        var rig = new EvRig();
        rig.PlugInCar(40);
        var departure = rig.Trip(TimeSpan.FromSeconds(60), 42);
        rig.PushCalendar(departure);
        var start = departure.Start;
        rig.Tick();

        Assert.Contains(rig.Offer(start - TimeSpan.FromSeconds(1)).Requests, r => r.Hard && r.Deadline == start);
        Assert.DoesNotContain(rig.Offer(start).Requests, r => r.Deadline == start);
        Assert.DoesNotContain(rig.Offer(start + TimeSpan.FromSeconds(1)).Requests, r => r.Hard && r.Deadline is not null);
    }

    // ---- 7, 8 ----

    private static PlanSnapshot PlanFor(EvRig rig, GoalRequest trip)
    {
        var input = WorkedExample(now: rig.Now);
        return Solve(input with { Goals = [trip] });
    }

    [Fact]
    public void Row07_a_trip_added_just_before_departure_is_infeasible_with_a_shortfall()
    {
        // Why: 9.9 kWh cannot be delivered in the 20 minutes left at 2.99 kW; the plan must not pretend. It says infeasible, shows the shortfall and runs from now.
        var rig = new EvRig();
        rig.AdvanceTo(EvRig.Local(2026, 10, 26, 18));
        rig.PlugInCar(40);
        rig.PushCalendar(rig.Trip(TimeSpan.FromMinutes(20), 42));
        var trip = rig.TickAndOffer().Requests.Single(r => r.Hard);

        var plan = PlanFor(rig, trip with { Sources = Offpeak });

        var goal = plan.Device(Car)!.Goals.Single();
        Assert.Equal(GoalStatus.Infeasible, goal.Status);
        Assert.True(goal.ShortfallKwh > 0);
        Assert.Contains(PlanNote.GoalInfeasible, plan.Notes);
    }

    [Fact]
    public void Row08_no_time_before_the_departure_is_infeasible_and_still_pursued()
    {
        // Why: with the departure inside the ready margin there is no window at all. The hard goal is shown as infeasible and is still pursued (urgent), never dropped.
        var rig = new EvRig();
        rig.AdvanceTo(EvRig.Local(2026, 10, 26, 18));
        rig.PlugInCar(40);
        rig.PushCalendar(rig.Trip(TimeSpan.FromMinutes(5), 42));
        var trip = rig.TickAndOffer().Requests.Single(r => r.Hard);

        var plan = PlanFor(rig, trip with { Sources = Offpeak });

        Assert.Equal(GoalStatus.Infeasible, plan.Device(Car)!.Goals.Single().Status);
        Assert.Contains(plan.Device(Car)!.Entries, e => e.Want == PlanWant.Urgent);
    }

    // ---- 9, 10 ----

    [Fact]
    public void Row09_an_unplugged_car_gets_no_goal_and_nothing_is_reserved()
    {
        // Why: a car that is not there cannot take energy, so a goal for it would hold slots, budget and cost for nobody.
        var rig = new EvRig();
        rig.PlugInCar(40);
        rig.PushPlugged("Idle");

        var offer = rig.TickAndOffer();

        Assert.Empty(offer.Requests);
        Assert.Empty(offer.Inactive);
        Assert.Equal(EvAssessmentCode.NotPlugged, rig.Keeper.Current.Snapshot!.Composition.Code);
    }

    [Fact]
    public void Row10_an_unreadable_plugged_state_leaves_the_goal_inactive()
    {
        // Why: a lost link to the charger is not an unplugged car. The goal exists but cannot be stated, and the plan says why (an input is unavailable).
        var rig = new EvRig();
        rig.PlugInCar(40);
        rig.PushPlugged("unavailable");

        var offer = rig.TickAndOffer();

        Assert.Empty(offer.Requests);
        var inactive = Assert.Single(offer.Inactive);
        Assert.Equal(GoalInactiveReason.InputUnavailable, inactive.Reason);
    }

    // ---- 11 ----

    [Fact]
    public void Row11_a_missing_pv_forecast_is_a_note_and_the_hard_goal_still_escalates()
    {
        // Why: no forecast means zero PV, not no plan: the trip goal is still offered, hard and allowed to escalate, so the planner buys the energy the deadline needs.
        var rig = new EvRig(planner: "planner:\n  dispatch: narrow\n");
        rig.PlugInCar(40);
        rig.PushCalendar(rig.Trip(TimeSpan.FromHours(10), 42));
        rig.Plans.Publish(new PlanState(null, false, null, [PlanNote.PvForecastMissing], TimeSpan.FromHours(1)));

        var offer = rig.TickAndOffer();

        Assert.Contains(EvAssessmentCode.ForecastMissing, rig.Keeper.Current.Snapshot!.Composition.Notes);
        var trip = offer.Requests.Single(r => r.Hard);
        Assert.True(trip.Escalate);
    }

    // ---- 12 ----

    [Theory]
    [InlineData(2026, 10, 24, 20, 2026, 10, 25, 0)]
    [InlineData(2026, 10, 25, 12, 2026, 10, 26, 0)]
    [InlineData(2027, 3, 27, 20, 2027, 3, 28, 0)]
    [InlineData(2027, 3, 28, 12, 2027, 3, 29, 0)]
    public void Row12_midnights_follow_the_local_day_on_23_and_25_hour_days(int y, int mo, int d, int h, int dy, int dmo, int dd, int dh)
    {
        // Why: the weekly full charge is due at the end of the local weekend day. On 2026-10-25 (25 hours) and 2027-03-28 (23 hours; the virtual clock starts in October 2026 and does not run back, EvComposerTests hold 2026-03-29) a deadline built by adding 24 hours
        // to the clock would be an hour wrong; the module reads the add-on's zone.
        var rig = new EvRig();
        rig.AdvanceTo(EvRig.Local(y, mo, d, h));
        rig.PlugInCar(40);

        var offer = rig.TickAndOffer();

        var weekly = offer.Requests.Single(r => r.TargetSoc == 100);
        Assert.Equal(EvRig.Local(dy, dmo, dd, dh), weekly.Deadline);
        Assert.False(weekly.Hard);
    }

    // ---- 13 ----

    [Fact]
    public void Row13_a_calendar_zone_that_disagrees_is_an_issue()
    {
        // Why: the calendar's times are naive local times; read in the wrong zone a trip starts at the wrong hour. The entity's own zone is compared with the add-on's.
        var rig = new EvRig();
        rig.PlugInCar(40);
        rig.Tick();
        var inputs = ((IEvInputSource)rig).Current.Inputs with { CalendarZoneName = "America/New_York" };

        var response = EvEndpoint.Build(rig.H.Configuration, rig.Now, inputs, EvRig.Warsaw, rig.Keeper.Current);

        Assert.Contains(response.Issues, i => i.Code == ConfigurationIssueCodes.EvCalendarZoneMismatch);
    }

    // ---- 14 ----

    [Fact]
    public void Row14_a_restart_restores_ev_json_and_offers_nothing_until_the_first_computation()
    {
        // Why: the learned consumption and the frozen trip target are weeks of data and a promise to the user; the plan is not persisted, so until the first
        // computation the module offers nothing and the car is v2.
        var rig = new EvRig();
        rig.PlugInCar(60);
        rig.PushRange(45);
        rig.PushCalendar(rig.Trip(TimeSpan.FromHours(10), 42));
        var before = rig.Tick();
        var saved = rig.Saved!;
        Assert.NotNull(saved.LastTrip);

        rig.Restart();

        Assert.Equal(EvPublished.None, rig.Keeper.Current);
        Assert.Empty(rig.Offer().Requests);
        rig.PushSoc(60);
        var after = rig.Tick();
        Assert.Equal(before.KwhPerKm, after.KwhPerKm, 9);
        Assert.Equal(saved.LastTrip!.TripKey, rig.Saved!.LastTrip!.TripKey);
        Assert.Equal(saved.LastTrip.TargetPercent, rig.Saved.LastTrip.TargetPercent);
    }

    // ---- 15 ----

    [Fact]
    public void Row15_a_corrupt_ev_json_is_set_aside_and_the_module_starts_from_defaults()
    {
        // Why: ev.json is untrusted input. A damaged file must cost the module its memory, not the add-on its protection: it is set aside as .corrupt, the module runs
        // from its defaults and nothing engages the kill switch (that is state.json's rule, not this file's).
        var directory = Path.Combine(Path.GetTempPath(), "switchboard-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, EvStateFile.Name), "{ this is not json");
            var rig = new EvRig(store: new EvStateFile(directory, NullLogger<EvStateFile>.Instance));
            rig.PlugInCar(40);

            var offer = rig.TickAndOffer();

            Assert.NotEmpty(offer.Requests);
            Assert.Equal(EvMode.Auto, rig.Keeper.Current.Snapshot!.Mode);
            Assert.True(File.Exists(Path.Combine(directory, EvStateFile.Name + ".corrupt")));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    // ---- 16 ----

    [Fact]
    public void Row16_the_same_value_again_keeps_the_anchor_and_a_changed_one_resets_it()
    {
        // Why: B10. A car that repeats an old value while charging must not reset a correct estimate to the stale reading; a changed value is the truth and restarts it.
        var rig = new EvRig();
        rig.PlugInCar(40);
        rig.Tick();
        rig.ChargerWatts = 3300;
        for (var i = 0; i < 10; i++)
        {
            rig.Advance(TimeSpan.FromMinutes(1));
            rig.PushPlugged();
            rig.Tick();
        }

        var grown = rig.Keeper.Current.Snapshot!.SocPercent!.Value;
        Assert.True(grown > 40.5);

        rig.PushSoc(40);
        Assert.Equal(grown, rig.Tick().SocPercent!.Value, 3);

        rig.PushSoc(55);
        Assert.Equal(55, rig.Tick().SocPercent!.Value, 6);
    }

    // ---- 17 ----

    [Fact]
    public void Row17_energy_the_soc_never_saw_drops_the_estimate_at_the_drift_limit()
    {
        // Why: the go-e's energy for the air conditioning of a plugged car never reaches the battery. Past ev.drift_limit_kwh with no change in the car's own SoC the
        // estimate is dropped (goals inactive) instead of drifting upward for ever.
        var rig = new EvRig();
        rig.PlugInCar(40);
        rig.Tick();
        rig.ChargerWatts = 3600;
        var limit = rig.Configuration.Control.Ev.DriftLimitKwh;
        var minutes = (int)Math.Ceiling(limit / 3.6 * 60) + 2;
        for (var i = 0; i < minutes; i++)
        {
            rig.Advance(TimeSpan.FromMinutes(1));
            rig.PushPlugged();
            rig.Tick();
        }

        var snapshot = rig.Keeper.Current.Snapshot!;
        Assert.Null(snapshot.SocPercent);
        Assert.Equal(SocEstimateGap.DriftExceeded, snapshot.SocGap);
        Assert.Equal(EvAssessmentCode.SocStale, snapshot.Composition.Code);
    }

    // ---- 18 ----

    [Fact]
    public void Row18_a_snapshot_for_another_device_is_not_offered()
    {
        // Why: a configuration change that points the module at another device must not leave the old device's goals in force until the next computation.
        var rig = new EvRig();
        rig.PlugInCar(40);
        var snapshot = rig.Tick() with { Device = DeviceName.Parse("other_charger") };
        var stale = new FixedSource(new EvPublished(snapshot, false));
        var source = new EvGoalSource(stale, _ => false);

        var offer = source.Goals(new GoalContext(rig.Now, rig.Configuration, GoalInputs.Empty, EvRig.Warsaw, new GoalLedger(NullLogger<GoalLedger>.Instance)));

        Assert.Empty(offer.Requests);
        Assert.False(offer.Failed);
    }

    // ---- 19 ----

    [Fact]
    public void Row19_a_computation_that_throws_is_a_failed_offer_and_never_reaches_the_caller()
    {
        // Why: B1. The offer is computed beside the guard; if that throws, the guard's step must see "the source failed" (goals missing, nothing concluded from the
        // absence) and the loop must carry on. When the computation recovers, the goals return.
        var healthy = true;
        EvRig? holder = null;
        var rig = new EvRig(inputs: () => healthy ? holder! : new Throwing());
        holder = rig;
        rig.PlugInCar(40);
        Assert.NotEmpty(rig.TickAndOffer().Requests);

        healthy = false;
        var failedTick = Record.Exception(rig.Keeper.Tick);
        var offer = rig.Offer();

        Assert.Null(failedTick);
        Assert.True(offer.Failed);
        Assert.Empty(offer.Requests);
        Assert.Single(rig.Log.Entries, e => e.Level == LogLevel.Error);

        healthy = true;
        rig.Keeper.Tick();
        Assert.False(rig.Offer().Failed);
        Assert.NotEmpty(rig.Offer().Requests);
    }

    [Fact]
    public void Row19_a_failed_offer_degrades_the_board()
    {
        // Why: the tracker lets the goal ledger follow the board only when it is complete. A failed EV source must mark the board partial, or the energy ledger of a
        // config goal would be reset by the module's failure (S0 review).
        var healthy = true;
        EvRig? holder = null;
        var rig = new EvRig(inputs: () => healthy ? holder! : new Throwing());
        holder = rig;
        rig.PlugInCar(40);
        rig.Tick();
        healthy = false;
        rig.Keeper.Tick();
        var evaluator = new GoalEvaluator([rig.Source], NullLogger<GoalEvaluator>.Instance);

        var board = evaluator.Evaluate(new GoalContext(rig.Now, rig.Configuration, GoalInputs.Empty, EvRig.Warsaw, new GoalLedger(NullLogger<GoalLedger>.Instance)));

        Assert.True(board.Degraded);
        Assert.Empty(board.Goals);
    }

    // ---- 20 ----

    [Fact]
    public void Row20_the_module_can_only_ask_for_energy_by_a_deadline()
    {
        // Why: protection outranks everything. What the module hands over has no power, current or setpoint in it: only an amount of energy, a target, a deadline and
        // the classes it may come from. The guard's limits and the dispatch envelope cap the charger whatever is asked (the architecture tests hold the rest).
        string[] forbidden = ["Power", "Current", "Amp", "Setpoint", "Watt", "Command", "Override"];
        var members = typeof(EvGoalOffer).GetProperties().Concat(typeof(GoalRequest).GetProperties()).Select(p => p.Name).Where(n => n != nameof(GoalRequest.CurrentSoc)).ToList();

        Assert.DoesNotContain(members, m => forbidden.Any(f => m.Contains(f, StringComparison.OrdinalIgnoreCase)));
        var rig = new EvRig();
        rig.PlugInCar(40);
        Assert.All(rig.TickAndOffer().Requests, r => Assert.Equal(GoalOrigin.Module, r.Origin));
    }

    // ---- beyond the table ----

    [Fact]
    public void The_revision_names_what_a_goal_is_so_a_moving_soc_never_replans()
    {
        // Why: B3. A revision that moves with the SoC or the learned consumption changes the goal's signature every cycle, triggers an immediate replan and makes the
        // plan view return null until then: dispatch would jump between the plan and v2.
        var rig = new EvRig();
        rig.PlugInCar(40);
        rig.PushCalendar(rig.Trip(TimeSpan.FromHours(10), 42));
        var first = rig.TickAndOffer().Requests.Select(r => (r.TargetSoc, r.Revision)).ToList();

        rig.ChargerWatts = 3300;
        for (var i = 0; i < 5; i++)
        {
            rig.Advance(TimeSpan.FromMinutes(1));
            rig.PushPlugged();
            rig.PushCalendar(rig.Trip(TimeSpan.FromHours(10) - TimeSpan.FromMinutes(i + 1), 42));
            rig.Tick();
        }

        rig.PushRange(45);
        var later = rig.TickAndOffer().Requests;
        Assert.True(later.First().CurrentSoc > 40);
        Assert.All(first, f => Assert.Contains(later, r => r.Revision == f.Revision));
    }

    [Fact]
    public void A_manual_goal_for_the_device_switches_the_modules_goals_off()
    {
        // Why: B8. A manual goal beats a module goal only at the same deadline; at another one both would be planned and the energy counted twice.
        var rig = new EvRig();
        rig.PlugInCar(40);
        Assert.NotEmpty(rig.TickAndOffer().Requests);

        rig.ManualGoal = true;

        // At once, from the snapshot computed before the manual goal existed: the source does not wait for the next computation, or the two would be planned together.
        Assert.Empty(rig.Offer().Requests);
        Assert.Empty(rig.TickAndOffer().Requests);
        Assert.Equal(EvAssessmentCode.Manual, rig.Keeper.Current.Snapshot!.Composition.Code);
    }

    [Fact]
    public void A_snapshot_past_its_age_is_a_failed_offer_not_the_old_goals()
    {
        // Why: B1. If the keeper stalls, the guard must not keep planning from a picture of the car that is minutes old.
        var rig = new EvRig();
        rig.PlugInCar(40);
        rig.Tick();
        var validUntil = rig.Keeper.Current.Snapshot!.ValidUntil;

        Assert.False(rig.Offer(validUntil).Failed);
        Assert.True(rig.Offer(validUntil + TimeSpan.FromSeconds(1)).Failed);
    }

    [Fact]
    public void The_modules_goals_carry_the_planner_flags_and_a_cap_never_raises_a_shortfall()
    {
        // Why: EV.md 2a and B12. A trip and the weekly goal are spread over their window; a PV-only cap that a cloudy evening cannot fill must not raise the standing
        // goal_shortfall note.
        var rig = new EvRig();
        rig.PlugInCar(40);
        rig.PushCalendar(rig.Trip(TimeSpan.FromHours(10), 20));

        var requests = rig.TickAndOffer().Requests;

        Assert.All(requests.Where(r => r.Hard && r.Deadline is not null), r => Assert.True(r.Spread));
        Assert.All(requests.Where(r => !r.Hard), r => Assert.True(r.QuietShortfall));
        Assert.All(requests.Where(r => r.Hard), r => Assert.False(r.QuietShortfall));
    }

    [Fact]
    public void The_weekly_full_charge_is_recorded_once_and_not_wanted_again_that_week()
    {
        // Why: one full charge a week. Reaching the target on a weekend day writes LastFullAt to ev.json; the next computation does not ask for 100 % again.
        var rig = new EvRig();
        rig.AdvanceTo(EvRig.Local(2026, 10, 24, 10));
        rig.PlugInCar(100);

        rig.Tick();

        Assert.NotNull(rig.Saved?.LastFullAt);
        rig.Advance(TimeSpan.FromHours(1));
        rig.PushSoc(70);
        rig.PushPlugged();
        var offer = rig.TickAndOffer();
        Assert.DoesNotContain(offer.Requests, r => r.TargetSoc == 100);
    }

    [Fact]
    public void Without_an_ev_section_the_source_offers_nothing_and_computes_nothing()
    {
        // Why: a configuration from before the module must decide byte for byte as before; the source answers an empty offer and the keeper publishes no snapshot.
        var result = TestConfigurations.Parse(TestDevices.Document(EvRig.Charger, extra: "planner:\n  dispatch: off\n"));
        var source = new EvGoalSource(new FixedSource(EvPublished.None), _ => false);
        var offer = source.Goals(new GoalContext(DateTimeOffset.UnixEpoch, result.Configuration!, GoalInputs.Empty, EvRig.Warsaw, new GoalLedger(NullLogger<GoalLedger>.Instance)));

        Assert.Equal(GoalOffer.Empty, offer);
    }

    private sealed class FixedSource(EvPublished published) : IEvSnapshotSource
    {
        public EvPublished Current => published;
    }

    private sealed class Throwing : IEvInputSource
    {
        public EvInputView Current => throw new InvalidOperationException("the input source broke");
    }
}
