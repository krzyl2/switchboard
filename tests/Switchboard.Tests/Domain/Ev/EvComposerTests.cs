using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Ev;
using Switchboard.Domain.Goals;
using static Switchboard.Tests.Domain.Ev.EvTestParameters;

namespace Switchboard.Tests.Domain.Ev;

/// <summary>
/// EV-06 (S5.3): the composition of the module's goals (docs/EV.md sections 3, 4 and 2a). Why it matters: goals on one device add up in the planner, so a
/// composer that restates the same energy in two goals plans twice the charge (B2); one that moves its revision with the SoC makes every plan foreign (B3).
/// Dates are in Europe/Warsaw; 2026-10-30 is a Friday, 2026-10-31 a Saturday, 2026-10-25 the 25-hour and 2026-03-29 the 23-hour day.
/// </summary>
public sealed class EvComposerTests
{
    private static readonly DeviceName Device = DeviceName.Parse("ev_charger");

    private static DateTimeOffset Local(int y, int m, int d, int h = 0, int min = 0)
    {
        var naive = new DateTime(y, m, d, h, min, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(naive, Warsaw.GetUtcOffset(naive));
    }

    private static EvComposerInput Input(
        DateTimeOffset now,
        double? soc,
        Trip? trip = null,
        EvMode mode = EvMode.Auto,
        bool? plugged = true,
        double kwhPerKm = 0.20,
        FrozenTarget? frozen = null,
        DateTimeOffset? lastFull = null,
        bool fullRequested = false,
        Func<DateOnly, bool>? holiday = null,
        EvParameters? p = null) => new()
        {
            Now = now,
            Zone = Warsaw,
            Device = Device,
            CapacityKwh = CapacityKwh,
            Parameters = p ?? Defaults,
            Mode = mode,
            Plugged = plugged,
            SocPercent = soc,
            KwhPerKm = kwhPerKm,
            NextTrip = trip,
            Frozen = frozen,
            LastFullAt = lastFull,
            FullChargeRequested = fullRequested,
            IsHoliday = holiday ?? (_ => false),
            CheapZone = "offpeak",
            DeviceSources = SourceSet.Any,
        };

    private static double Total(EvComposition c) => c.Goals.Sum(g => g.Request.RemainingKwh);

    private static readonly DateTimeOffset Friday = Local(2026, 10, 30, 10);
    private static readonly DateTimeOffset Saturday = Local(2026, 10, 31, 10);

    [Fact]
    public void A_trip_to_90_percent_at_40_percent_is_asked_once_not_three_times()
    {
        // Why: B2. Trip 90 % (55 points + floor 25 + margin 10), cap 90 % and a floor of 25 % are all absolute against the same 40 %; added up they would plan
        // about twice the 9.9 kWh.
        var trip = new Trip("Gdańsk", Local(2026, 10, 31, 6), 49.5);
        var c = EvComposer.Compose(Input(Friday, 40, trip));

        var goal = Assert.Single(c.Goals);
        Assert.Equal(EvGoalKind.Trip, goal.Kind);
        Assert.Equal(50 / 100.0 * 18 * 1.10, goal.Request.RemainingKwh, 9);
        Assert.Equal(9.9, goal.Request.RemainingKwh, 9);
        Assert.Equal(EvAssessmentCode.TripDeadline, c.Code);
    }

    [Fact]
    public void The_trip_goal_is_hard_due_at_the_start_with_the_margin_of_its_own()
    {
        // Why: B2b. A deadline of start minus margin plus a margin counts the margin twice and the charge finishes an hour early.
        var start = Local(2026, 10, 31, 6);
        var r = Assert.Single(EvComposer.Compose(Input(Friday, 40, new Trip("t", start, 49.5))).Goals).Request;

        Assert.Equal(start, r.Deadline);
        Assert.Equal(TimeSpan.FromMinutes(30), r.Margin);
        Assert.True(r.Hard);
        Assert.True(r.Escalate);
        Assert.Equal(GoalKind.Soc, r.Kind);
        Assert.Equal(GoalOrigin.Module, r.Origin);
        Assert.Equal(GoalOnMet.Stop, r.OnMet);
        Assert.Same(SourceSet.Any, r.Sources);
    }

    [Fact]
    public void A_weekend_full_charge_takes_the_cap_increment_too_and_stays_within_100_percent()
    {
        // Why: weekly 100 % and the cap 90 % share the midnight deadline; one goal (the weekly) carries 60 points x 18 kWh x 1.1 = 11.88 kWh, never 11.88 + 9.9.
        var c = EvComposer.Compose(Input(Saturday, 40));

        var goal = Assert.Single(c.Goals);
        Assert.Equal(EvGoalKind.WeeklyFull, goal.Kind);
        Assert.Equal(11.88, goal.Request.RemainingKwh, 9);
        Assert.Equal(100, ((SocTarget)goal.Request.Target).TargetPercent);
        Assert.Equal(Local(2026, 11, 1), goal.Request.Deadline);
        Assert.False(goal.Request.Hard);
        Assert.True(goal.Spread);
        Assert.False(goal.QuietShortfall);
        Assert.Equal(EvAssessmentCode.WeeklyFull, c.Code);
    }

    [Fact]
    public void The_weekly_charge_is_offered_on_a_holiday_and_on_either_weekend_day_but_not_on_a_weekday()
    {
        // Why: amendment 2a: any weekend day, holidays from economics.holidays count as weekend; a Friday is none of them.
        Assert.DoesNotContain(EvComposer.Compose(Input(Friday, 40)).Goals, g => g.Kind == EvGoalKind.WeeklyFull);
        Assert.Contains(EvComposer.Compose(Input(Friday, 40, holiday: d => d == new DateOnly(2026, 10, 30))).Goals, g => g.Kind == EvGoalKind.WeeklyFull);
        Assert.Contains(EvComposer.Compose(Input(Local(2026, 11, 1, 10), 40)).Goals, g => g.Kind == EvGoalKind.WeeklyFull);
    }

    [Fact]
    public void Once_a_week_means_once_in_the_local_calendar_week()
    {
        // Why: the Saturday charge must not be repeated on Sunday; the Monday-based local week turns over at local midnight, not at UTC.
        var sunday = Local(2026, 11, 1, 10);

        Assert.DoesNotContain(EvComposer.Compose(Input(sunday, 40, lastFull: Saturday)).Goals, g => g.Kind == EvGoalKind.WeeklyFull);
        Assert.Contains(EvComposer.Compose(Input(Saturday, 40, lastFull: Local(2026, 10, 24, 22))).Goals, g => g.Kind == EvGoalKind.WeeklyFull);
        Assert.Contains(EvComposer.Compose(Input(Friday, 40, lastFull: Local(2026, 10, 24, 22), fullRequested: true)).Goals, g => g.Kind == EvGoalKind.WeeklyFull);
    }

    [Fact]
    public void Reaching_the_weekly_target_on_any_day_is_reported_once_a_week_so_the_caller_records_it()
    {
        // Why: 2026-11-06. The car reached 100 % from PV on Thursday 2026-11-05 and the weekend would have charged it to 100 % again, from the grid, because only a
        // weekend day counted. Once a week means the car was full this week, whatever the day; LastFullAt is written by the caller, so the composer says it only
        // the first time in the local week (a car that stays at 100 % must not rewrite the state file every cycle).
        var thursday = Local(2026, 10, 29, 13, 15);
        Assert.True(EvComposer.Compose(Input(Saturday, 100)).WeeklyFullReached);
        Assert.False(EvComposer.Compose(Input(Saturday, 99)).WeeklyFullReached);
        Assert.True(EvComposer.Compose(Input(Friday, 100)).WeeklyFullReached);
        Assert.True(EvComposer.Compose(Input(Friday, 100, lastFull: Local(2026, 10, 24, 9))).WeeklyFullReached);
        Assert.False(EvComposer.Compose(Input(Friday, 100, lastFull: thursday)).WeeklyFullReached);
        Assert.False(EvComposer.Compose(Input(Friday, 100, p: Defaults with { WeeklyFull = new EvWeeklyFullParameters([], 100) })).WeeklyFullReached);

        // ...and it is then not asked for again that weekend.
        Assert.DoesNotContain(EvComposer.Compose(Input(Saturday, 70, lastFull: thursday)).Goals, g => g.Kind == EvGoalKind.WeeklyFull);
    }

    [Fact]
    public void A_requested_full_charge_is_reported_when_it_reaches_its_target_even_after_a_full_charge_this_week()
    {
        // Why: the full_charge mode ends when the caller hears it was reached; recorded once a week or not, the request must still end.
        Assert.True(EvComposer.Compose(Input(Friday, 100, lastFull: Local(2026, 10, 29, 13), fullRequested: true)).WeeklyFullReached);
        Assert.False(EvComposer.Compose(Input(Friday, 95, fullRequested: true)).WeeklyFullReached);
    }

    [Fact]
    public void The_weekly_target_is_reached_by_the_same_kwh_epsilon_that_meets_every_other_goal()
    {
        // Why: the epsilon is a kWh quantity; the SoC is percent. A residue of 3e-9 points is 6e-10 kWh drawn (18 kWh, 10 % loss): the goal is met, so LastFullAt must be recorded.
        Assert.True(EvComposer.Compose(Input(Saturday, 100 - 3e-9)).WeeklyFullReached);
        Assert.False(EvComposer.Compose(Input(Saturday, 100 - 1e-6)).WeeklyFullReached);
    }

    [Fact]
    public void A_weekday_without_a_trip_charges_the_floor_from_the_cheap_zone_and_the_rest_from_surplus()
    {
        // Why: below the floor the cheapest zone tops up to 25 %; above it only PV, up to the 90 % cap. 5 + 65 points = 70 points = 13.86 kWh in all.
        var c = EvComposer.Compose(Input(Friday, 20));

        Assert.Equal(13.86, Total(c), 9);
        var floor = c.Goals.Single(g => g.Kind == EvGoalKind.Floor);
        var cap = c.Goals.Single(g => g.Kind == EvGoalKind.Cap);
        Assert.Equal(5 / 100.0 * 18 * 1.10, floor.Request.RemainingKwh, 9);
        Assert.Equal(Local(2026, 10, 30, 15), floor.Request.Deadline);
        Assert.True(floor.Request.Hard);
        Assert.False(floor.Request.Escalate);
        Assert.Equal([new ZoneId("offpeak")], floor.Request.Sources!.Zones);
        Assert.Equal(SourceSet.PvOnly, cap.Request.Sources);
        Assert.Equal(GoalOnMet.Stop, cap.Request.OnMet);
        Assert.False(cap.Spread);
        Assert.Equal(EvAssessmentCode.CheapZone, c.Code);
    }

    [Fact]
    public void With_everything_met_the_highest_goal_is_still_offered_so_its_stop_holds_the_device_off()
    {
        // Why: with no goal at all the device is a v2 opportunist and would charge the full battery off-peak; a met cap with on_met stop prevents that.
        var c = EvComposer.Compose(Input(Friday, 95));

        var goal = Assert.Single(c.Goals);
        Assert.Equal(EvGoalKind.Cap, goal.Kind);
        Assert.Equal(0, goal.Request.RemainingKwh);
        Assert.Equal(EvAssessmentCode.FloorHold, c.Code);
    }

    [Fact]
    public void Below_the_floor_beside_a_trip_ready_before_floor_by_the_floor_is_due_when_the_car_must_be_ready_and_takes_the_first_points()
    {
        // Why: a trip at 06:00 on Saturday comes before Saturday's 15:00. A floor due at 15:00 could be placed after the departure and the car would leave short,
        // so the floor is due at the ready moment instead; the trip asks only for the points above it, so nothing is asked twice (B2). Those first points
        // were the trip's, so the floor escalates like the trip when the cheap zone cannot deliver them in time.
        var start = Local(2026, 10, 31, 6);
        var c = EvComposer.Compose(Input(Local(2026, 10, 30, 18), 10, new Trip("t", start, 20)));

        var floor = c.Goals.Single(g => g.Kind == EvGoalKind.Floor).Request;
        var trip = c.Goals.Single(g => g.Kind == EvGoalKind.Trip).Request;
        Assert.Equal(start - TimeSpan.FromMinutes(30), floor.Deadline);
        Assert.True(floor.Hard);
        Assert.True(floor.Escalate);
        Assert.Equal([new ZoneId("offpeak")], floor.Sources!.Zones);
        Assert.Equal(15 / 100.0 * 18 * 1.10, floor.RemainingKwh, 9);
        Assert.Equal((((SocTarget)trip.Target).TargetPercent - 25) / 100 * 18 * 1.10, trip.RemainingKwh, 9);
        Assert.Equal(EvAssessmentCode.TripDeadline, c.Code);
    }

    [Fact]
    public void A_trip_after_floor_by_leaves_the_floor_on_its_own_deadline_without_escalation_and_asks_only_above_it()
    {
        // Why: the maintainer's case (floor 50, Monday 19:30 at 33 %, an 18 km trip on Thursday). The reserve is due Tuesday 15:00, so the planner can wait for
        // Tuesday's sun and charge the night only for what the forecast will not bring; it is no trip's point, so it never buys a dear zone. The trip asks for its
        // own 20 points and the margin above the reserve, due at its start.
        var p = Defaults with { FloorPercent = 50 };
        var start = Local(2026, 11, 5, 14, 25);
        var c = EvComposer.Compose(Input(Local(2026, 11, 2, 19, 30), 33, new Trip("Odbiór dzieci", start, 18), p: p));

        var floor = c.Goals.Single(g => g.Kind == EvGoalKind.Floor).Request;
        var trip = c.Goals.Single(g => g.Kind == EvGoalKind.Trip).Request;
        Assert.Equal(Local(2026, 11, 3, 15), floor.Deadline);
        Assert.False(floor.Escalate);
        Assert.True(floor.Hard);
        Assert.Equal(17 / 100.0 * 18 * 1.10, floor.RemainingKwh, 9);
        Assert.Equal(80, ((SocTarget)trip.Target).TargetPercent, 9);
        Assert.Equal(30 / 100.0 * 18 * 1.10, trip.RemainingKwh, 9);
        Assert.Equal(start, trip.Deadline);
    }

    [Fact]
    public void The_floor_is_due_at_the_next_floor_by_after_the_suns_hours_so_the_night_is_charged_only_for_what_the_forecast_will_not_bring()
    {
        // Why: 2026-11-06. A floor due the next morning has no PV in its window and always charges the night; due at 15:00 the planner sees the day's forecast
        // first. Before 15:00 it is today's 15:00, after it tomorrow's; the time is configuration and keeps its local hour on the 25-hour day.
        Assert.Equal(Local(2026, 10, 30, 15), FloorDeadline(Local(2026, 10, 30, 10)));
        Assert.Equal(Local(2026, 10, 31, 15), FloorDeadline(Local(2026, 10, 30, 15)));
        Assert.Equal(Local(2026, 10, 31, 15), FloorDeadline(Local(2026, 10, 30, 21)));
        Assert.Equal(Local(2026, 10, 25, 15), FloorDeadline(Local(2026, 10, 24, 22)));
        Assert.Equal(Local(2026, 10, 31, 7), FloorDeadline(Local(2026, 10, 30, 21), Defaults with { FloorBy = new TimeOnly(7, 0) }));

        static DateTimeOffset? FloorDeadline(DateTimeOffset now, EvParameters? p = null) =>
            EvComposer.Compose(Input(now, 10, p: p)).Goals.Single(g => g.Kind == EvGoalKind.Floor).Request.Deadline;
    }

    [Fact]
    public void An_estimate_too_old_only_for_its_age_still_plans_the_floor_and_nothing_else()
    {
        // Why: 2026-11-06. The car reports every few hours while it sleeps, so at night the 6 h limit left no reserve goal and nothing charged. The floor may use
        // the aged value (a few kWh at the cheap price if it is wrong); a trip, the weekly charge and the cap never plan on it, and pv_only stays the cap's alone.
        var c = EvComposer.Compose(Input(Friday, null, new Trip("t", Local(2026, 10, 31, 6), 45)) with { FloorSocPercent = 10 });

        var goal = Assert.Single(c.Goals);
        Assert.Equal(EvGoalKind.Floor, goal.Kind);
        Assert.Equal(15 / 100.0 * 18 * 1.10, goal.Request.RemainingKwh, 9);
        Assert.Equal(Local(2026, 10, 30, 15), goal.Request.Deadline);
        Assert.False(goal.Request.Escalate);
        Assert.Equal(EvAssessmentCode.CheapZone, c.Code);
        Assert.Contains(EvAssessmentCode.SocStale, c.Notes);
        Assert.False(c.WeeklyFullReached);

        var pvOnly = EvComposer.Compose(Input(Friday, null, mode: EvMode.PvOnly) with { FloorSocPercent = 10 });
        Assert.Empty(pvOnly.Goals);
        Assert.Equal(EvAssessmentCode.SocStale, pvOnly.Code);
        Assert.False(EvComposer.Compose(Input(Saturday, null) with { FloorSocPercent = 100 }).WeeklyFullReached);
    }

    [Fact]
    public void Without_a_ready_margin_the_floor_would_share_the_trips_deadline_so_the_trip_carries_the_whole_way_as_before()
    {
        // Why: B8. With the trip before floor_by the floor would be due at the trip's own deadline: two module goals at one deadline merge into the trip and the
        // floor's points would vanish, and a floor due at floor_by could be placed after the departure. Either way the car would leave short, so the trip alone is
        // asked for the whole way from the SoC.
        var p = Defaults with { ReadyMargin = TimeSpan.Zero };
        var c = EvComposer.Compose(Input(Local(2026, 10, 30, 18), 10, new Trip("t", Local(2026, 10, 31, 6), 20), p: p));

        var goal = c.Goals.Single(g => g.Kind == EvGoalKind.Trip);
        Assert.Equal((((SocTarget)goal.Request.Target).TargetPercent - 10) / 100 * 18 * 1.10, goal.Request.RemainingKwh, 9);
        Assert.DoesNotContain(c.Goals, g => g.Kind == EvGoalKind.Floor);
    }

    [Fact]
    public void Inside_the_ready_margin_the_floor_has_no_deadline_left_so_the_trip_carries_the_whole_way()
    {
        // Why: a floor due in the past is a missed hard goal, which runs in every zone from now; the trip already escalates on its own deadline.
        var start = Local(2026, 10, 30, 10, 20);
        var c = EvComposer.Compose(Input(Friday, 10, new Trip("t", start, 20)));

        Assert.DoesNotContain(c.Goals, g => g.Kind == EvGoalKind.Floor);
        Assert.Contains(c.Goals, g => g.Kind == EvGoalKind.Trip);
    }

    [Fact]
    public void Power_bands_become_local_instants_on_every_goal_and_keep_their_hours_on_the_25_hour_night()
    {
        // Why: the bands are wall-clock hours. On 2026-10-25 the clock goes back at 03:00, so 00:00-07:00 lasts 8 hours; a band computed in UTC would end at 06:00.
        var p = Defaults with
        {
            PowerBands = [new PowerBand(new HoursRange(21 * 60, 24 * 60), 2000), new PowerBand(new HoursRange(0, 7 * 60), 3000)],
        };
        var now = Local(2026, 10, 24, 19);

        var c = EvComposer.Compose(Input(now, 20, p: p) with { PlanHorizon = TimeSpan.FromHours(48) });

        Assert.All(c.Goals, g => Assert.False(g.Request.PowerCaps.IsDefaultOrEmpty));
        var caps = c.Goals[0].Request.PowerCaps;
        Assert.Contains(new PowerCap(Local(2026, 10, 24, 21), Local(2026, 10, 25, 0), 2000), caps);
        var night = Assert.Single(caps, cap => cap.From == Local(2026, 10, 25, 0));
        Assert.Equal(TimeSpan.FromHours(8), night.To - night.From);
        Assert.Equal(3000, night.MaxW);
        Assert.DoesNotContain(caps, cap => cap.To <= now);
        Assert.Contains(caps, cap => cap.From == Local(2026, 10, 26, 21));
    }

    [Fact]
    public void Without_power_bands_no_goal_carries_a_cap()
    {
        // Why: the default; every plan without bands must stay exactly as it was.
        Assert.All(EvComposer.Compose(Input(Friday, 20)).Goals, g => Assert.True(g.Request.PowerCaps.IsDefaultOrEmpty));
    }

    [Fact]
    public void Without_a_trip_the_floor_is_still_offered_below_it()
    {
        // Why: the exemption is for a trip that covers the floor, not a general removal of the floor.
        Assert.Contains(EvComposer.Compose(Input(Friday, 10)).Goals, g => g.Kind == EvGoalKind.Floor);
    }

    [Fact]
    public void Only_the_cap_is_quiet_about_its_shortfall_and_the_weekly_charge_does_not_escalate()
    {
        // Why: B12 asks for silence about a PV-only cap that a cloudy evening cannot fill; a weekly full charge left short is a real shortfall the maintainer wants to
        // see. And the weekly goal is soft with no escalation: it never buys energy from a zone outside its sources to reach 100 %.
        var weekly = EvComposer.Compose(Input(Saturday, 40));
        var weekday = EvComposer.Compose(Input(Friday, 40));

        var full = weekly.Goals.Single(g => g.Kind == EvGoalKind.WeeklyFull);
        Assert.False(full.QuietShortfall);
        Assert.False(full.Request.Escalate);
        Assert.False(full.Request.Hard);
        Assert.True(weekday.Goals.Single(g => g.Kind == EvGoalKind.Cap).QuietShortfall);
        Assert.All(weekday.Goals.Where(g => g.Kind != EvGoalKind.Cap), g => Assert.False(g.QuietShortfall));
    }

    [Fact]
    public void The_next_local_midnight_in_a_zone_whose_midnight_is_ambiguous_or_missing_is_the_first_or_the_next_hour()
    {
        // Why: Warsaw changes at 02:00/03:00 and never exercises the midnight rules; Havana changes at midnight. 2026-11-01 00:00 happens twice (take the first, the
        // summer offset) and 2026-03-08 00:00 does not exist (take 01:00).
        TimeZoneInfo havana;
        try
        {
            havana = TimeZoneInfo.FindSystemTimeZoneById("America/Havana");
        }
        catch (TimeZoneNotFoundException)
        {
            return;
        }

        var ambiguous = EvComposer.NextLocalMidnight(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.FromHours(-4)), havana);
        var missing = EvComposer.NextLocalMidnight(new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.FromHours(-5)), havana);

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(-4)), ambiguous);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 1, 0, 0, TimeSpan.FromHours(-4)), missing);
    }

    [Fact]
    public void A_trip_that_has_started_is_gone_at_that_instant()
    {
        // Why: B4. A deadline in the past becomes a Missed hard goal that runs Urgent in every zone, i.e. full power in the peak, for a car that already left.
        var start = Local(2026, 10, 31, 6);
        var trip = new Trip("t", start, 45);

        Assert.Contains(EvComposer.Compose(Input(start - TimeSpan.FromSeconds(1), 40, trip)).Goals, g => g.Kind == EvGoalKind.Trip);
        var gone = EvComposer.Compose(Input(start, 40, trip));
        Assert.DoesNotContain(gone.Goals, g => g.Kind == EvGoalKind.Trip);
        Assert.NotEqual(EvAssessmentCode.TripDeadline, gone.Code);
        Assert.Null(EvComposer.Compose(Input(start.AddHours(2), 40, trip)).Frozen);
    }

    [Fact]
    public void A_trip_starting_at_the_next_local_midnight_wins_the_deadline_and_the_soft_goals_are_dropped()
    {
        // Why: B8. Two goals on one deadline would coexist in the merge and double count; the trip (hard) keeps it.
        var c = EvComposer.Compose(Input(Saturday, 40, new Trip("t", Local(2026, 11, 1), 49.5)));

        var goal = Assert.Single(c.Goals);
        Assert.Equal(EvGoalKind.Trip, goal.Kind);
        Assert.Equal(9.9, goal.Request.RemainingKwh, 9);
    }

    [Theory]
    [InlineData(EvMode.Auto, false, 50.0, EvAssessmentCode.NotPlugged)]
    [InlineData(EvMode.Auto, null, 50.0, EvAssessmentCode.NotPlugged)]
    [InlineData(EvMode.Auto, true, null, EvAssessmentCode.SocStale)]
    public void No_goal_when_unplugged_or_without_a_soc_and_never_the_last_value(EvMode mode, bool? plugged, double? soc, EvAssessmentCode expected)
    {
        // Why: EV.md section 9: unplugged reserves nothing, an unknown plugged state is no goal, a missing SoC is inactive (the car returns to v2).
        var c = EvComposer.Compose(Input(Friday, soc, new Trip("t", Local(2026, 10, 31, 6), 45), mode, plugged));

        Assert.Empty(c.Goals);
        Assert.Equal(expected, c.Code);
    }

    [Fact]
    public void A_manual_goal_suppresses_every_module_goal()
    {
        // Why: B8. Left to the (device, deadline) merge a manual goal at another deadline would be added to the module's energy.
        var c = EvComposer.Compose(Input(Friday, 40, new Trip("t", Local(2026, 10, 31, 6), 45)) with { ManualGoalActive = true });

        Assert.Empty(c.Goals);
        Assert.Equal(EvAssessmentCode.Manual, c.Code);
    }

    [Fact]
    public void PV_only_has_the_cap_goal_on_surplus_and_no_floor_and_no_grid()
    {
        // Why: "PV only" is no grid and no floor (decision 6).
        var c = EvComposer.Compose(Input(Saturday, 20, new Trip("t", Local(2026, 11, 2, 6), 45), EvMode.PvOnly));

        var goal = Assert.Single(c.Goals);
        Assert.Equal(EvGoalKind.Cap, goal.Kind);
        Assert.Equal(SourceSet.PvOnly, goal.Request.Sources);
        Assert.Equal(EvAssessmentCode.PvOnly, c.Code);
    }

    [Fact]
    public void Notes_carry_the_conditions_on_top_of_the_primary_code()
    {
        // Why: the closed list has states and conditions; a trip kept from a dead calendar still reads "trip deadline" and says why it is old.
        var c = EvComposer.Compose(Input(Friday, 40, new Trip("t", Local(2026, 10, 31, 6), 45)) with
        {
            CalendarUnavailable = true,
            RangeUnusable = true,
            ForecastMissing = true,
            DispatchOff = true,
        });

        Assert.Equal(EvAssessmentCode.TripDeadline, c.Code);
        Assert.Equal(
            [EvAssessmentCode.WithoutPlannerDispatch, EvAssessmentCode.CalendarUnavailable, EvAssessmentCode.RangeUnusable, EvAssessmentCode.ForecastMissing],
            c.Notes);
    }

    [Fact]
    public void The_revision_names_what_the_goal_is_not_the_target_the_soc_or_the_day()
    {
        // Why: B3. A revision that moved with SoC or the learned kWh/km would change the goal signature, force a replan and make the plan foreign every time.
        var trip = new Trip("t", Local(2026, 10, 31, 6), 49.5);
        var baseline = EvComposer.Compose(Input(Friday, 40, trip)).Goals.Single().Request.Revision;

        foreach (var (soc, kwh) in new[] { (41.0, 0.20), (55.0, 0.21), (30.0, 0.25), (10.0, 0.13) })
        {
            Assert.Equal(baseline, EvComposer.Compose(Input(Friday.AddMinutes(7), soc, trip, kwhPerKm: kwh)).Goals.Single(g => g.Kind == EvGoalKind.Trip).Request.Revision);
        }

        var otherTrip = EvComposer.Compose(Input(Friday, 40, trip with { Start = trip.Start.AddHours(1) })).Goals.Single().Request.Revision;
        Assert.NotEqual(baseline, otherTrip);

        var weeklyA = EvComposer.Compose(Input(Saturday, 40)).Goals.Single().Request.Revision;
        var weeklyB = EvComposer.Compose(Input(Local(2026, 11, 1, 18), 65)).Goals.Single().Request.Revision;
        Assert.Equal(weeklyA, weeklyB);
        Assert.NotEqual(baseline, weeklyA);
    }

    [Fact]
    public void The_revision_moves_with_what_the_goal_is_made_of()
    {
        // Why: the revision must still change when the goal really changes (spread, margin, loss, capacity, sources), or a stale plan would be applied.
        var trip = new Trip("t", Local(2026, 10, 31, 6), 49.5);
        long Rev(EvParameters p) => EvComposer.Compose(Input(Friday, 40, trip, p: p)).Goals.Single().Request.Revision;
        var baseline = Rev(Defaults);

        Assert.NotEqual(baseline, Rev(Defaults with { Spread = false }));
        Assert.NotEqual(baseline, Rev(Defaults with { ReadyMargin = TimeSpan.FromMinutes(45) }));
        Assert.NotEqual(baseline, Rev(Defaults with { LossFraction = 0.12 }));
        Assert.NotEqual(baseline, EvComposer.Compose(Input(Friday, 40, trip) with { DeviceSources = SourceSet.PvOnly }).Goals.Single().Request.Revision);
    }

    [Fact]
    public void The_target_is_frozen_for_the_trip_until_it_moves_by_more_than_the_threshold()
    {
        // Why: B3. A learned kWh/km drifting by a hair would move the target, the need and the plan; the frozen one holds inside the threshold.
        var trip = new Trip("t", Local(2026, 10, 31, 6), 49.5);
        var first = EvComposer.Compose(Input(Friday, 40, trip));
        Assert.Equal(90, first.Frozen!.Target.Percent);

        var small = EvComposer.Compose(Input(Friday.AddHours(1), 40, trip, kwhPerKm: 0.21, frozen: first.Frozen)); // fresh 92.75: 2.75 points away
        Assert.Same(first.Frozen, small.Frozen);
        Assert.Equal(9.9, small.Goals.Single().Request.RemainingKwh, 9);

        var big = EvComposer.Compose(Input(Friday.AddHours(1), 40, trip, kwhPerKm: 0.25, frozen: first.Frozen)); // fresh 100: 10 points away (103.75 clamped)
        Assert.Equal(100, big.Frozen!.Target.Percent);
        Assert.Equal(60 / 100.0 * 18 * 1.10, big.Goals.Single().Request.RemainingKwh, 9);
    }

    [Fact]
    public void A_trip_longer_than_the_electric_range_asks_for_a_full_battery_and_is_not_an_error()
    {
        // Why: EV 9: a PHEV finishes on fuel; the target is 100 %, which is also the "manual full charge" for a long trip.
        var c = EvComposer.Compose(Input(Friday, 40, new Trip("t", Local(2026, 10, 31, 6), 300)));

        Assert.Equal(100, ((SocTarget)c.Goals.Single().Request.Target).TargetPercent);
        Assert.True(c.Frozen!.Target.ExceedsElectricRange);
    }

    [Theory]
    [InlineData(2026, 10, 24, 12, 2026, 10, 25, 0, 2)] // the day before the 25-hour day: midnight is still summer time
    [InlineData(2026, 10, 25, 12, 2026, 10, 26, 0, 1)] // on the 25-hour day: the next midnight is winter time
    [InlineData(2026, 3, 28, 12, 2026, 3, 29, 0, 1)]
    [InlineData(2026, 3, 29, 12, 2026, 3, 30, 0, 2)] // on the 23-hour day: summer time again
    public void The_next_local_midnight_is_the_calendar_midnight_on_the_dst_days(int y, int m, int d, int h, int ey, int em, int ed, int eh, int offsetHours)
    {
        // Why: the weekly and cap deadlines are "local midnight"; adding 24 hours would land an hour off on these two days.
        var midnight = EvComposer.NextLocalMidnight(Local(y, m, d, h), Warsaw);

        Assert.Equal(new DateTimeOffset(ey, em, ed, eh, 0, 0, TimeSpan.FromHours(offsetHours)), midnight);
    }

    [Fact]
    public void The_dst_days_are_23_and_25_hours_long_for_the_weekly_goal()
    {
        // Why: absolute deadlines. The weekly charge on the 25-hour Sunday has a window one hour longer than the clock reads, and the composer must not care.
        var longDay = EvComposer.Compose(Input(Local(2026, 10, 25, 0, 30), 40)).Goals.Single();
        var shortDay = EvComposer.Compose(Input(Local(2026, 3, 29, 0, 30), 40)).Goals.Single();

        Assert.Equal(EvGoalKind.WeeklyFull, longDay.Kind);
        Assert.Equal(TimeSpan.FromHours(24.5), longDay.Request.Deadline!.Value - Local(2026, 10, 25, 0, 30));
        Assert.Equal(EvGoalKind.WeeklyFull, shortDay.Kind);
        Assert.Equal(TimeSpan.FromHours(22.5), shortDay.Request.Deadline!.Value - Local(2026, 3, 29, 0, 30));
    }

    [Fact]
    public void The_assessment_list_is_closed_with_the_fourteen_codes_of_the_plan()
    {
        // Why: EV.md 3.5. A new state needs a new code on purpose; each has a stable wire text.
        Assert.Equal(14, EvAssessmentCodes.All.Count);
        Assert.Equal(14, EvAssessmentCodes.All.Select(EvAssessmentCodes.Text).Distinct().Count());
        Assert.All(EvAssessmentCodes.All, c => Assert.StartsWith("ev_", EvAssessmentCodes.Text(c)));
        Assert.Equal("ev_without_planner_dispatch", EvAssessmentCodes.Text(EvAssessmentCode.WithoutPlannerDispatch));
    }

    [Fact]
    public void Properties_hold_over_random_situations()
    {
        // Why: the invariants that make the planner safe to feed. Over 3000 seeded situations: the energy asked never exceeds the energy the highest target needs
        // (B2), deadlines are unique and at most one goal has none (B8), every offer is a module soc goal that stops when met, the revision does not move with the SoC
        // (B3), no needed energy is negative, and only a trip that is still ahead is offered (B4).
        var rng = new Random(20261030);
        var days = new[] { Friday, Saturday, Local(2026, 11, 1, 22), Local(2026, 10, 25, 1), Local(2026, 3, 29, 1), Local(2026, 3, 28, 23, 59) };
        for (var i = 0; i < 3000; i++)
        {
            var now = days[rng.Next(days.Length)].AddMinutes(rng.Next(0, 600));
            var soc = rng.NextDouble() < 0.1 ? (rng.Next(2) == 0 ? 0 : 100) : rng.NextDouble() * 100;
            Trip? trip = rng.NextDouble() < 0.7 ? new Trip("t", now.AddMinutes(rng.Next(-120, 3000)), rng.NextDouble() * 400) : null;
            var kwh = 0.12 + rng.NextDouble() * 0.48;
            var requested = rng.NextDouble() < 0.1;
            var lastFull = rng.NextDouble() < 0.3 ? now.AddDays(-rng.Next(0, 9)) : (DateTimeOffset?)null;
            var c = EvComposer.Compose(Input(now, soc, trip, kwhPerKm: kwh, lastFull: lastFull, fullRequested: requested));

            Assert.NotEmpty(c.Goals);
            var highest = c.Goals.Max(g => ((SocTarget)g.Request.Target).TargetPercent);
            Assert.True(Total(c) <= Math.Max(0, highest - soc) / 100 * CapacityKwh * 1.10 + 1e-9, $"#{i}: {Total(c)} kWh for {soc} -> {highest}");
            Assert.All(c.Goals, g =>
            {
                Assert.True(g.Request.RemainingKwh >= 0);
                Assert.Equal(GoalOrigin.Module, g.Request.Origin);
                Assert.Equal(GoalKind.Soc, g.Request.Kind);
                Assert.Equal(GoalOnMet.Stop, g.Request.OnMet);
            });
            var deadlines = c.Goals.Select(g => g.Request.Deadline).ToList();
            Assert.Equal(deadlines.Count, deadlines.Distinct().Count());
            Assert.True(c.Goals.Count(g => g.Request.Deadline is null) <= 1);
            Assert.Equal(c.Goals.Length, c.Goals.Select(g => g.Kind).Distinct().Count());
            if (trip is not null && trip.Start <= now)
            {
                Assert.DoesNotContain(c.Goals, g => g.Kind == EvGoalKind.Trip);
            }

            var other = EvComposer.Compose(Input(now, Math.Clamp(soc + rng.NextDouble() * 20 - 10, 0, 100), trip, kwhPerKm: kwh * 1.3, lastFull: lastFull, fullRequested: requested));
            foreach (var g in c.Goals)
            {
                var twin = other.Goals.FirstOrDefault(o => o.Kind == g.Kind);
                if (twin is not null)
                {
                    Assert.Equal(g.Request.Revision, twin.Request.Revision);
                }
            }
        }
    }
}
