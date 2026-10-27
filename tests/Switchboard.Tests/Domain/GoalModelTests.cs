using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Goals;

namespace Switchboard.Tests.Domain;

/// <summary>
/// PLAN-03 and PLAN-04, the pure part: what a goal still needs (15-PLANNER, The goal model), when it is met, expired or missed, how two
/// requests for one device are merged, and when a ledger entry still belongs to the goal in force. All of it is pure arithmetic on the
/// values handed in; nothing reads a clock.
/// </summary>
public sealed class GoalModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 17, 0, 0, TimeSpan.Zero);
    private static readonly DeviceName Car = DeviceName.Parse("ev_charger");
    private static readonly DeviceName Tank = DeviceName.Parse("boiler");

    private static GoalRequest Request(DeviceName? device = null, GoalOrigin origin = GoalOrigin.Config, DateTimeOffset? deadline = null, bool hard = false, double remaining = 5, long revision = 1) =>
        new(device ?? Car, GoalKind.Energy, remaining, 10, null, null, 0, deadline, TimeSpan.Zero, hard, null, hard, GoalOnMet.Opportunistic, Now, origin, revision);

    [Fact]
    public void The_worked_example_of_the_design_needs_9_9_kwh()
    {
        // Why: 15-PLANNER's worked example (40 to 90 percent of an 18 kWh battery, 10 percent loss) is the golden number the planner will be
        // held to; the loss fraction turns energy into the battery into energy drawn.
        var needed = GoalMath.RemainingSocKwh(90, 40, 18, 0.10);

        Assert.Equal(9.9, needed, 9);
    }

    [Fact]
    public void An_energy_goal_counts_what_was_delivered_and_the_loss_on_what_is_left()
    {
        Assert.Equal(6.6, GoalMath.RemainingEnergyKwh(10, 4, 0.10), 9);
    }

    [Theory]
    [InlineData(90, 95)]
    [InlineData(90, 90)]
    public void A_target_already_reached_needs_nothing_and_never_a_negative_amount(double target, double soc)
    {
        // Why: a negative remaining need would let a planner schedule "less than nothing" and reduce another goal's energy.
        var needed = GoalMath.RemainingSocKwh(target, soc, 18, 0.10);

        Assert.Equal(0, needed);
        Assert.True(GoalMath.IsMet(needed));
        Assert.Equal(0, GoalMath.RemainingEnergyKwh(5, 7, 0.10));
    }

    [Fact]
    public void A_float_residue_is_not_an_unmet_need()
    {
        Assert.True(GoalMath.IsMet(1e-12));
        Assert.False(GoalMath.IsMet(0.001));
    }

    [Theory]
    [InlineData(0, false, GoalStatus.Met)]
    [InlineData(2, false, GoalStatus.OnTrack)]
    public void A_goal_before_its_deadline_is_met_or_on_track(double remaining, bool hard, GoalStatus expected)
    {
        var goal = Goal.Of(Request(remaining: remaining, hard: hard, deadline: Now.AddHours(3)), Now);

        Assert.Equal(expected, goal.Status);
    }

    [Fact]
    public void Past_its_deadline_an_unmet_soft_goal_lapses_and_an_unmet_hard_goal_is_missed()
    {
        // Why: the design is explicit: soft lapses (expired), hard is shown and still pursued (missed) until met or removed. Mixing them
        // up would either drop a trip or keep chasing an offer that is no longer worth its price.
        var soft = Goal.Of(Request(deadline: Now.AddMinutes(-1)), Now);
        var hard = Goal.Of(Request(deadline: Now.AddMinutes(-1), hard: true), Now);
        var metLate = Goal.Of(Request(deadline: Now.AddMinutes(-1), hard: true, remaining: 0), Now);

        Assert.Equal((GoalStatus.Expired, GoalStatus.Missed, GoalStatus.Met), (soft.Status, hard.Status, metLate.Status));
    }

    [Fact]
    public void The_deadline_itself_is_already_too_late()
    {
        Assert.Equal(GoalStatus.Expired, Goal.Of(Request(deadline: Now), Now).Status);
    }

    [Fact]
    public void The_plan_aims_to_finish_a_margin_before_the_deadline()
    {
        var request = Request(deadline: Now.AddHours(10)) with { Margin = TimeSpan.FromMinutes(30) };

        Assert.Equal(Now.AddHours(10).AddMinutes(-30), request.UsableDeadline);
        Assert.Null(Request().UsableDeadline);
    }

    [Fact]
    public void For_one_device_and_one_deadline_manual_beats_module_beats_entity_beats_config_and_the_loser_is_shown()
    {
        // Why: the precedence is the contract of the goal seam: a "want now" from the user must win over the car's calendar, which wins
        // over a helper entity, which wins over the file. The loser is shown, not applied, so the user sees why theirs did not count.
        var deadline = Now.AddHours(8);
        var requests = new[]
        {
            Request(origin: GoalOrigin.Config, deadline: deadline, revision: 1),
            Request(origin: GoalOrigin.Manual, deadline: deadline, revision: 2),
            Request(origin: GoalOrigin.Entity, deadline: deadline, revision: 3),
            Request(origin: GoalOrigin.Module, deadline: deadline, revision: 4),
        };

        var merged = GoalMerge.Merge(requests);

        Assert.Equal(GoalOrigin.Manual, Assert.Single(merged.Applied).Origin);
        Assert.Equal([GoalOrigin.Module, GoalOrigin.Entity, GoalOrigin.Config], merged.Superseded.Select(r => r.Origin).OrderByDescending(o => o));
    }

    [Fact]
    public void Two_goals_of_one_device_with_different_deadlines_both_stand_ordered_by_deadline()
    {
        // Why: two trips in one day are two goals; the later cumulative need is due at its own deadline, so neither may replace the other.
        var later = Request(deadline: Now.AddHours(30), revision: 1);
        var sooner = Request(deadline: Now.AddHours(6), revision: 2);
        var open = Request(deadline: null, revision: 3);

        var merged = GoalMerge.Merge([later, open, sooner]);

        Assert.Equal([sooner, later, open], merged.Applied);
        Assert.Empty(merged.Superseded);
    }

    [Fact]
    public void A_tie_in_origin_falls_to_the_harder_goal_and_the_result_does_not_depend_on_the_order_given()
    {
        // Why: the planner is deterministic (the same input gives the same bytes), so the merge may not depend on the order sources answer in.
        var deadline = Now.AddHours(4);
        var soft = Request(deadline: deadline, revision: 7);
        var hard = Request(deadline: deadline, hard: true, revision: 9);

        var forward = GoalMerge.Merge([soft, hard]);
        var backward = GoalMerge.Merge([hard, soft]);

        Assert.True(Assert.Single(forward.Applied).Hard);
        Assert.Equal(forward.Applied, backward.Applied);
        Assert.Equal(forward.Superseded, backward.Superseded);
    }

    [Fact]
    public void Devices_are_kept_apart_and_listed_by_name()
    {
        var merged = GoalMerge.Merge([Request(Tank, deadline: Now.AddHours(1)), Request(Car, deadline: Now.AddHours(1))]);

        Assert.Equal([Tank, Car], merged.Applied.Select(r => r.Device));
    }

    [Fact]
    public void The_goal_origin_numbers_are_the_precedence()
    {
        // Why: the merge orders by the enum; reordering the enum would silently reorder the precedence.
        Assert.True(GoalOrigin.Manual > GoalOrigin.Module && GoalOrigin.Module > GoalOrigin.Entity && GoalOrigin.Entity > GoalOrigin.Config);
    }

    [Fact]
    public void A_ledger_entry_belongs_to_the_goal_until_its_deadline_has_passed_and_a_new_one_begins()
    {
        // Why: progress is the energy of one cycle. A departure put back from 07:00 to 07:30 is the same trip and keeps what was delivered; a
        // recurring goal whose deadline passed is the next day's goal and starts at zero. Mixing the two would either lose a night's charge
        // or count yesterday's energy toward tomorrow.
        var deadline = Now.AddHours(6);
        var entry = new GoalLedgerEntry(GoalKind.Energy, Now.AddHours(-2), deadline, 3, Now);

        Assert.True(entry.Continues(GoalKind.Energy, deadline, Now));
        Assert.True(entry.Continues(GoalKind.Energy, deadline.AddMinutes(30), Now));
        Assert.True(entry.Continues(GoalKind.Energy, null, Now));
        Assert.False(entry.Continues(GoalKind.Energy, deadline.AddDays(1), deadline));
        Assert.True(entry.Continues(GoalKind.Energy, deadline, deadline.AddMinutes(5)));
        Assert.False(entry.Continues(GoalKind.Soc, deadline, Now));
    }

    [Fact]
    public void The_spec_origin_follows_what_is_read_from_an_entity()
    {
        var literal = Spec(GoalNumber.Of(5), null, null);
        var entity = Spec(GoalNumber.From(Switchboard.Domain.Signals.SignalName.Parse("device_boiler_goal_target")), null, null);
        var want = Spec(GoalNumber.Of(5), null, Switchboard.Domain.Signals.SignalName.Parse("device_boiler_goal_want"));

        Assert.Equal((GoalOrigin.Config, GoalOrigin.Entity, GoalOrigin.Entity), (literal.Origin, entity.Origin, want.Origin));
    }

    private static GoalSpec Spec(GoalNumber target, GoalDeadlineSpec? deadline, Switchboard.Domain.Signals.SignalName? want) =>
        new(Tank, GoalKind.Energy, false, 0, null, null, target, deadline, want, SourceSet.PvOnly, false, GoalOnMet.Opportunistic);
}
