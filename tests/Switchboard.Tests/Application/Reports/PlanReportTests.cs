using Switchboard.Application.Configuration;
using Switchboard.Application.Goals;
using Switchboard.Application.Planning;
using Switchboard.Application.Reports;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Planning;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Reports;

/// <summary>
/// What GET api/plan decides before it is worded. Without a planner the goals are still listed but nothing is planned, so no inputs, slots or status may
/// appear as if something were; devices are listed in a fixed order so that the page does not reshuffle between two requests.
/// </summary>
public sealed class PlanReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 17, 0, 0, TimeSpan.Zero);

    private static ConfigurationResult Config(string planner = "") =>
        TestConfigurations.Parse(TestDevices.Document(
            "  - id: ev_charger\n    type: modulating\n    actuator: number.charger_current\n    phase: L1\n    min: 6\n    max: 16\n    step: 1\n    value_per_kwh: 2.03\n    goal:\n      kind: energy\n      target_kwh: 12\n",
            extra: planner));

    private static GoalRequest Request(string device) =>
        new(DeviceName.Parse(device), new EnergyTarget(12, 4, Now.AddHours(-1)), 8.8, null, TimeSpan.FromMinutes(30), false, SourceSet.PvOnly, false, GoalOnMet.Stop, GoalOrigin.Config, 5);

    [Fact]
    public void Without_a_planner_the_goals_are_listed_and_nothing_is_planned()
    {
        var report = PlanReport.Compose(Config(), new GoalBoard(Now, [Goal.Of(Request("ev_charger"), Now)], [], []));

        Assert.Null(report.Planner);
        Assert.Equal(PlanStatus.Absent, report.Status);
        Assert.Null(report.Inputs);
        Assert.Null(report.Plan);
        Assert.Equal(1, report.GoalsActive);
        Assert.Equal("ev_charger", Assert.Single(report.Devices).Device);
        Assert.Null(Assert.Single(report.Devices[0].Goals).Judged);
    }

    [Fact]
    public void Devices_are_ordered_by_name_whatever_order_the_board_lists_the_goals_in()
    {
        var board = new GoalBoard(Now, [Goal.Of(Request("wallbox"), Now), Goal.Of(Request("boiler"), Now), Goal.Of(Request("ev_charger"), Now)], [], []);

        var report = PlanReport.Compose(Config(), board);

        Assert.Equal(["boiler", "ev_charger", "wallbox"], report.Devices.Select(d => d.Device));
    }

    [Fact]
    public void A_goal_input_that_is_stale_adds_the_goal_input_note_once()
    {
        var inactive = new InactiveGoal(DeviceName.Parse("ev_charger"), GoalInactiveReason.InputStale, GoalInputKind.Soc, GoalOrigin.Config, GoalKind.Energy, false);
        var board = new GoalBoard(Now, [], [inactive, inactive with { Input = GoalInputKind.Target }], []);

        var report = PlanReport.Compose(Config("planner:\n  dispatch: off\n"), board);

        Assert.Equal([PlanNote.GoalInputStale], report.Notes);
        Assert.Equal(2, Assert.Single(report.Devices).Goals.Count);
    }
}
