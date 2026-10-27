using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Goals;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Goals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Goals;

/// <summary>
/// PLAN-02 and PLAN-04: the goals of the configuration evaluated against real signals on the virtual clock (the signal store, not a
/// fake): what a goal still needs, and that a missing, stale, unavailable, unreadable or out-of-range input makes it inactive with the
/// reason rather than guessing, never keeping an old value. The seam merges sources by precedence.
/// </summary>
public sealed class GoalSourceTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly DeviceName Car = DeviceName.Parse("ev_charger");

    private const string Tariff = "economics:\n  currency: PLN\n  tariff:\n    default_zone: offpeak\n    zones:\n      - id: offpeak\n        price: 0.60\n        when: []\n  settlement:\n    model: hourly_netting\n    export_price: 0.35\n";

    private const string SocGoal =
        "      kind: soc\n      hard: true\n      capacity_kwh: 18\n      loss_fraction: 0.10\n      soc: { entity: sensor.car_battery_level, max_age: 30m }\n      target: { value: 90 }\n      deadline: { entity: input_datetime.car_departure, margin: 30m }\n      want: { entity: input_boolean.car_wanted, max_age: 30m }\n      sources: pv_and_zone:offpeak\n";

    private const string EnergyGoal =
        "      kind: energy\n      target_kwh: 12\n      loss_fraction: 0.10\n      deadline: { at: \"06:30\" }\n";

    private sealed class Rig
    {
        public Rig(string goal)
        {
            Harness = new StoreHarness(TestDevices.Document(
                "  - id: ev_charger\n    type: modulating\n    actuator: number.charger_current\n    phase: L1\n    min: 6\n    max: 16\n    step: 1\n    value_per_kwh: 2.03\n    goal:\n" + goal,
                extra: Tariff));
            Assert.True(Harness.Configuration.Status == Switchboard.Application.Configuration.ConfigurationStatus.Valid, string.Join("; ", Harness.Configuration.Errors));
            Ledger = new GoalLedger(NullLogger<GoalLedger>.Instance);
        }

        public StoreHarness Harness { get; }

        public GoalLedger Ledger { get; }

        public GoalOffer Offer() => new ConfigGoalSource().Goals(Context());

        public GoalContext Context() =>
            new(Harness.Now, Harness.Configuration.Configuration!, GoalInputs.From(Harness.Snapshot()), Warsaw, Ledger);

        public void Feed(string soc = "40", string want = "on", string deadline = "2026-10-25 07:00:00")
        {
            Harness.Push("sensor.car_battery_level", soc);
            Harness.Push("input_boolean.car_wanted", want);
            Harness.Push("input_datetime.car_departure", deadline);
        }

        public InactiveGoal Inactive() => Assert.Single(Offer().Inactive);

        public GoalRequest Request() => Assert.Single(Offer().Requests);
    }

    [Fact]
    public void The_worked_example_of_the_design_is_9_9_kwh_due_at_the_deadline_less_the_margin()
    {
        // Why: the end-to-end slice 1 promise: entities in, the remaining need of 15-PLANNER's worked example out, with the deadline read in the
        // local zone (07:00 on the day the clocks went back is 06:00 UTC).
        var rig = new Rig(SocGoal);
        rig.Feed();

        var request = rig.Request();

        Assert.Equal(9.9, request.RemainingKwh, 9);
        Assert.Equal((GoalKind.Soc, 90.0, 40.0), (request.Kind, request.TargetSoc, request.CurrentSoc));
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 6, 0, 0, TimeSpan.Zero), request.Deadline);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 5, 30, 0, TimeSpan.Zero), request.UsableDeadline);
        Assert.Equal((true, GoalOrigin.Entity, null), (request.Hard, request.Origin, request.Since));
        Assert.Equal("pv_and_zone:offpeak", request.Sources!.ToText());
    }

    [Fact]
    public void The_remaining_need_follows_the_soc_signal_and_reaches_met()
    {
        var rig = new Rig(SocGoal);
        rig.Feed();
        rig.Harness.Advance(TimeSpan.FromMinutes(10));
        rig.Harness.Push("sensor.car_battery_level", "65");
        var halfway = rig.Request();
        rig.Harness.Advance(TimeSpan.FromSeconds(1));
        rig.Harness.Push("sensor.car_battery_level", "91");

        var evaluated = new GoalEvaluator([new ConfigGoalSource()]).Evaluate(rig.Context());

        Assert.Equal((90 - 65) / 100.0 * 18 * 1.1, halfway.RemainingKwh, 9);
        Assert.Equal(GoalStatus.Met, Assert.Single(evaluated.Goals).Status);
    }

    [Fact]
    public void An_input_that_has_not_arrived_makes_the_goal_inactive_and_names_it()
    {
        // Why: the design: never guessed. A goal whose SoC nobody has reported cannot be planned; saying "missing" is the truth.
        var rig = new Rig(SocGoal);
        rig.Harness.Push("input_boolean.car_wanted", "on");
        rig.Harness.Push("input_datetime.car_departure", "2026-10-25 07:00:00");

        var inactive = rig.Inactive();

        Assert.Equal((GoalInactiveReason.InputMissing, GoalInputKind.Soc, GoalOrigin.Entity, true), (inactive.Reason, inactive.Input, inactive.Origin, inactive.Hard));
    }

    [Fact]
    public void A_stale_input_is_inactive_and_the_old_value_is_not_kept()
    {
        // Why: the failure table: a goal input stale at max_age makes the goal inactive. A car that stopped reporting 40 percent an hour
        // ago may be full; planning on the last known value would charge a full battery or miss a trip.
        var rig = new Rig(SocGoal);
        rig.Feed();
        Assert.Single(rig.Offer().Requests);
        rig.Harness.Advance(TimeSpan.FromMinutes(31));
        rig.Harness.Push("input_boolean.car_wanted", "on");
        rig.Harness.Push("input_datetime.car_departure", "2026-10-25 07:00:00");

        var offer = rig.Offer();

        Assert.Empty(offer.Requests);
        Assert.Equal((GoalInactiveReason.InputStale, GoalInputKind.Soc), (offer.Inactive.Single().Reason, offer.Inactive.Single().Input));
    }

    [Theory]
    [InlineData("unavailable", GoalInactiveReason.InputUnavailable)]
    [InlineData("unknown", GoalInactiveReason.InputUnavailable)]
    [InlineData("not a number", GoalInactiveReason.InputInvalid)]
    [InlineData("120", GoalInactiveReason.InputOutOfRange)]
    [InlineData("-3", GoalInactiveReason.InputOutOfRange)]
    public void A_soc_that_is_unavailable_unreadable_or_not_a_percentage_is_inactive_with_its_reason(string state, GoalInactiveReason expected)
    {
        var rig = new Rig(SocGoal);
        rig.Feed(soc: state);

        var inactive = rig.Inactive();

        Assert.Equal((expected, GoalInputKind.Soc), (inactive.Reason, inactive.Input));
    }

    [Fact]
    public void A_soc_reported_in_the_wrong_unit_is_refused_not_converted_on_a_guess()
    {
        // Why: the signal layer refuses a unit that differs from the declaration (a 1000 times error waiting to happen); the goal sees a
        // rejected input.
        var rig = new Rig(SocGoal);
        rig.Feed();
        rig.Harness.Advance(TimeSpan.FromSeconds(1));
        rig.Harness.Push("sensor.car_battery_level", "40", unit: "W");

        Assert.Equal((GoalInactiveReason.InputInvalid, GoalInputKind.Soc), (rig.Inactive().Reason, rig.Inactive().Input));
    }

    [Theory]
    [InlineData("off", GoalInactiveReason.WantOff)]
    [InlineData("false", GoalInactiveReason.WantOff)]
    [InlineData("maybe", GoalInactiveReason.InputInvalid)]
    [InlineData("unavailable", GoalInactiveReason.InputUnavailable)]
    public void The_want_gate_off_or_unreadable_means_no_goal_now(string state, GoalInactiveReason expected)
    {
        // Why: want is the user's "do not charge now"; an unreadable gate must not read as "wanted" (never guessed).
        var rig = new Rig(SocGoal);
        rig.Feed(want: state);

        var inactive = rig.Inactive();

        Assert.Equal((expected, GoalInputKind.Want), (inactive.Reason, inactive.Input));
    }

    [Theory]
    [InlineData("on")]
    [InlineData("true")]
    public void The_want_gate_on_lets_the_goal_through(string state)
    {
        var rig = new Rig(SocGoal);
        rig.Feed(want: state);

        Assert.Single(rig.Offer().Requests);
    }

    [Theory]
    [InlineData("whenever", GoalInactiveReason.InputInvalid)]
    [InlineData("unavailable", GoalInactiveReason.InputUnavailable)]
    public void A_deadline_that_cannot_be_read_makes_the_goal_inactive(string state, GoalInactiveReason expected)
    {
        var rig = new Rig(SocGoal);
        rig.Feed(deadline: state);

        var inactive = rig.Inactive();

        Assert.Equal((expected, GoalInputKind.Deadline), (inactive.Reason, inactive.Input));
    }

    [Fact]
    public void Past_the_deadline_an_unmet_hard_goal_is_missed_and_still_listed()
    {
        // Why: "a goal past its deadline is missed (hard, shown, still pursued)": it must stay on the board with its remaining need, not vanish.
        var rig = new Rig(SocGoal);
        rig.Feed(deadline: "2026-10-23 10:00:00");

        var board = new GoalEvaluator([new ConfigGoalSource()]).Evaluate(rig.Context());

        var goal = Assert.Single(board.Goals);
        Assert.Equal(GoalStatus.Missed, goal.Status);
        Assert.Equal(9.9, goal.Request.RemainingKwh, 9);
    }

    [Fact]
    public void An_energy_goal_counts_the_ledger_and_the_loss_on_what_is_left()
    {
        // Why: energy progress is what the device drew, from the ledger, not a signal: 12 kWh asked, 4 delivered, 10 percent loss on the rest.
        var rig = new Rig(EnergyGoal);
        var deadline = GoalDeadlines.NextOccurrence(new TimeOnly(6, 30), rig.Harness.Now, Warsaw);
        rig.Ledger.Begin(Car, GoalKind.Energy, rig.Harness.Now.AddHours(-1), deadline);
        rig.Ledger.AddDelivered(Car, 4, rig.Harness.Now);

        var request = rig.Request();

        Assert.Equal((12 - 4) * 1.1, request.RemainingKwh, 9);
        Assert.Equal((12.0, 4.0, GoalOrigin.Config), (request.TargetKwh, request.DeliveredKwh, request.Origin));
        Assert.Equal(rig.Harness.Now.AddHours(-1), request.Since);
        Assert.Equal(deadline, request.Deadline);
    }

    [Fact]
    public void An_energy_goal_without_a_ledger_entry_starts_at_zero_now()
    {
        var rig = new Rig(EnergyGoal);

        var request = rig.Request();

        Assert.Equal((13.2, 0.0, rig.Harness.Now), (Math.Round(request.RemainingKwh, 9), request.DeliveredKwh, request.Since));
    }

    [Fact]
    public void Energy_counted_toward_a_deadline_that_has_passed_does_not_count_toward_the_next_one()
    {
        // Why: yesterday's charge must not make tomorrow's recurring goal look met.
        var rig = new Rig(EnergyGoal);
        var yesterday = rig.Harness.Now.AddHours(-3);
        rig.Ledger.Begin(Car, GoalKind.Energy, yesterday.AddDays(-1), yesterday);
        rig.Ledger.AddDelivered(Car, 12, yesterday);

        var request = rig.Request();

        Assert.Equal(0, request.DeliveredKwh);
        Assert.Equal(13.2, request.RemainingKwh, 9);
    }

    [Fact]
    public void A_source_with_a_higher_origin_supersedes_the_config_goal_and_the_loser_is_shown()
    {
        // Why: PLAN-04: the seam merges whatever sources exist by the stated precedence; the EV module and the manual store will be such
        // sources. Here a manual request for the same device and deadline wins and the file's goal is shown as set aside.
        var rig = new Rig(EnergyGoal);
        var config = rig.Request();
        var manual = config with { Origin = GoalOrigin.Manual, RemainingKwh = 3, Since = rig.Harness.Now };

        var board = new GoalEvaluator([new ConfigGoalSource(), new FixedSource(manual)]).Evaluate(rig.Context());

        Assert.Equal((GoalOrigin.Manual, 3.0), (Assert.Single(board.Goals).Request.Origin, board.Goals[0].Request.RemainingKwh));
        Assert.Equal(GoalOrigin.Config, Assert.Single(board.Superseded).Origin);
    }

    [Fact]
    public void A_device_without_a_goal_has_neither_a_request_nor_an_inactive_entry()
    {
        // Why: no goal means a v2 opportunist; the plan never invents a want.
        var harness = new StoreHarness(TestDevices.Document(TestDevices.Reference));
        var context = new GoalContext(harness.Now, harness.Configuration.Configuration!, GoalInputs.From(harness.Snapshot()), Warsaw, new GoalLedger(NullLogger<GoalLedger>.Instance));

        var offer = new ConfigGoalSource().Goals(context);

        Assert.Empty(offer.Requests);
        Assert.Empty(offer.Inactive);
    }

    private sealed class FixedSource(GoalRequest request) : IGoalSource
    {
        public GoalOffer Goals(GoalContext context) => new([request], ImmutableArray<InactiveGoal>.Empty);
    }
}
