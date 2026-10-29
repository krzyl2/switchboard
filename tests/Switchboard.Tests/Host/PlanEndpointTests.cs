using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Switchboard.Application.Configuration;
using Switchboard.Application.Goals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Goals;
using Switchboard.Host.Web;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// GET api/plan (PLAN-01 to PLAN-05, slice 1): read-only, and it shows goals only. It must say plainly that there is no planner (a 0.10
/// file), echo the dispatch mode as configured, list each goal with its status or why it is inactive, and show what the precedence merge
/// set aside. It carries no slots, entries or cost: there is no plan yet, and it must not look as if there were.
/// </summary>
public sealed class PlanEndpointTests
{
    private const string Supervisor = "172.30.32.2";
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 17, 0, 0, TimeSpan.Zero);
    private static readonly DeviceName Car = DeviceName.Parse("ev_charger");

    private static ConfigurationResult Config(string planner = "", string goal = "      kind: energy\n      target_kwh: 12\n") =>
        TestConfigurations.Parse(TestDevices.Document(
            "  - id: ev_charger\n    type: modulating\n    actuator: number.charger_current\n    phase: L1\n    min: 6\n    max: 16\n    step: 1\n    value_per_kwh: 2.03\n    goal:\n" + goal,
            extra: planner));

    private static GoalRequest Request(GoalOrigin origin = GoalOrigin.Config, DateTimeOffset? deadline = null, bool hard = false) =>
        new(Car, GoalKind.Energy, 8.8, 12, null, null, 4, deadline, TimeSpan.FromMinutes(30), hard, SourceSet.PvOnly, hard, GoalOnMet.Stop, Now.AddHours(-1), origin, 5);

    [Fact]
    public void A_file_without_a_planner_section_is_not_enabled_and_has_no_dispatch_mode_but_still_shows_its_goals()
    {
        // Why: PLAN-05: enabled is false without a planner section (404-free), so the UI can say "no planner" instead of failing; the goals
        // are still shown, they simply feed nothing yet.
        var response = PlanEndpoint.Build(Config(), new GoalBoard(Now, [Goal.Of(Request(), Now)], [], []));

        Assert.False(response.Enabled);
        Assert.Null(response.DispatchMode);
        Assert.Equal("absent", response.Status);
        Assert.Equal("ev_charger", Assert.Single(response.Devices).Device);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("narrow")]
    [InlineData("full")]
    public void A_planner_section_is_enabled_and_echoes_its_dispatch_mode(string mode)
    {
        var response = PlanEndpoint.Build(Config($"planner:\n  dispatch: {mode}\n"), GoalBoard.Empty(Now));

        Assert.True(response.Enabled);
        Assert.Equal(mode, response.DispatchMode);
        Assert.Empty(response.Devices);
        Assert.Equal(0, response.Inputs.GoalsActive);
    }

    [Fact]
    public void A_goal_is_listed_with_its_kind_origin_need_deadline_and_status()
    {
        var deadline = Now.AddHours(10);

        var response = PlanEndpoint.Build(Config(), new GoalBoard(Now, [Goal.Of(Request(GoalOrigin.Entity, deadline, hard: true), Now)], [], []));

        var goal = Assert.Single(response.Devices).Goal!;
        Assert.Equal(("energy", true, "entity", "on_track", "stop"), (goal.Kind, goal.Hard, goal.Source, goal.Status, goal.OnMet));
        Assert.Equal((12.0, 8.8, 4.0, deadline, 1800.0), (goal.TargetKwh, goal.RemainingKwh, goal.DeliveredKwh, goal.Deadline, goal.MarginS));
        Assert.Equal(("pv_only", true), (goal.Sources, goal.Escalate));
        Assert.Equal(Now.AddHours(-1), goal.Since);
        Assert.Equal(1, response.Inputs.GoalsActive);
        Assert.Null(goal.InactiveReason);
    }

    [Fact]
    public void An_inactive_goal_says_which_input_failed_and_why_and_has_no_remaining_need()
    {
        // Why: the contract of PLAN-02: a goal whose input is missing, stale or out of range is shown with the reason, never guessed.
        var inactive = new InactiveGoal(Car, GoalInactiveReason.InputStale, GoalInputKind.Soc, GoalOrigin.Entity, GoalKind.Soc, true);

        var response = PlanEndpoint.Build(Config(), new GoalBoard(Now, [], [inactive], []));

        var goal = Assert.Single(response.Devices).Goal!;
        Assert.Equal(("inactive", "input_stale", "soc", "soc", null), (goal.Status, goal.InactiveReason, goal.InactiveInput, goal.Kind, goal.RemainingKwh));
        Assert.Equal(new GoalInactiveDto("ev_charger", "input_stale", "soc"), Assert.Single(response.Inputs.GoalsInactive));
        Assert.Equal(0, response.Inputs.GoalsActive);
    }

    [Theory]
    [InlineData(GoalInactiveReason.InputStale, true)]
    [InlineData(GoalInactiveReason.InputUnavailable, true)]
    [InlineData(GoalInactiveReason.WantOff, false)]
    [InlineData(GoalInactiveReason.InputOutOfRange, false)]
    public void A_goal_inactive_for_a_stale_or_unavailable_input_raises_the_goal_input_stale_note(GoalInactiveReason reason, bool noted)
    {
        // Why: PLAN-17 row "goal input stale": the plan drops the goal silently otherwise, and the UI warns by this note. A goal off by want or by a bad value is
        // not a stale input and must not raise it (the note would send the user to look at a fresh sensor).
        var inactive = new InactiveGoal(Car, reason, GoalInputKind.Soc, GoalOrigin.Entity, GoalKind.Soc, true);

        var response = PlanEndpoint.Build(Config("planner:\n  dispatch: off\n"), new GoalBoard(Now, [], [inactive], []));

        Assert.Equal(noted, response.Notes.Contains("goal_input_stale"));
    }

    [Fact]
    public void With_all_goal_inputs_fresh_there_is_no_goal_input_stale_note()
    {
        var response = PlanEndpoint.Build(Config("planner:\n  dispatch: off\n"), new GoalBoard(Now, [Goal.Of(Request(), Now)], [], []));

        Assert.DoesNotContain("goal_input_stale", response.Notes);
    }

    [Fact]
    public void Several_goals_of_one_device_are_listed_in_the_order_given_with_the_first_as_the_goal()
    {
        var sooner = Request(deadline: Now.AddHours(5));
        var later = Request(deadline: Now.AddHours(29));

        var response = PlanEndpoint.Build(Config(), new GoalBoard(Now, [Goal.Of(sooner, Now), Goal.Of(later, Now)], [], []));

        var device = Assert.Single(response.Devices);
        Assert.Equal([Now.AddHours(5), Now.AddHours(29)], device.Goals.Select(g => g.Deadline));
        Assert.Equal(device.Goals[0], device.Goal);
    }

    [Fact]
    public void A_request_the_merge_set_aside_is_shown_as_superseded_and_not_applied()
    {
        var response = PlanEndpoint.Build(Config(), new GoalBoard(Now, [Goal.Of(Request(GoalOrigin.Manual), Now)], [], [Request(GoalOrigin.Config)]));

        Assert.Equal("superseded", Assert.Single(response.Superseded).Status);
        Assert.Equal("manual", Assert.Single(response.Devices).Goal!.Source);
    }

    [Fact]
    public void The_configuration_issues_of_the_planner_and_of_a_goal_are_carried_and_no_others()
    {
        // Why: the answer is where the user looks for why a goal is missing; a goal dropped for a literal outside its range must be named
        // here, while an unrelated warning belongs to its own page.
        var result = Config(goal: "      kind: soc\n      capacity_kwh: 18\n      soc: { value: 40 }\n      target: { value: 150 }\n");

        var response = PlanEndpoint.Build(result, GoalBoard.Empty(Now));

        Assert.Contains(response.Issues, i => i.Code == ConfigurationIssueCodes.GoalDropped && i.Path == "devices[0].goal");
        Assert.All(response.Issues, i => Assert.True(i.Path.StartsWith("devices[0].goal", StringComparison.Ordinal) || i.Path.StartsWith("planner", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_answer_has_the_documented_keys_and_no_plan_field()
    {
        // Why: the answer must not look like it plans when there is no plan: the keys of slices 1 to 3 are all still there (nothing was removed or renamed), the
        // plan fields slice 4 added (id, computedAt, cost, lastSolve) are present but null, no device has an entry, and status stays absent. Without sources (as
        // here) the forecast inputs are null, so nothing is shown that was not computed.
        var json = JsonSerializer.SerializeToElement(
            PlanEndpoint.Build(Config("planner:\n  dispatch: off\n"), new GoalBoard(Now, [Goal.Of(Request(), Now)], [], [])),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var keys = json.EnumerateObject().Select(p => p.Name).ToList();
        Assert.All(["at", "devices", "dispatchMode", "enabled", "inputs", "issues", "notes", "slots", "status", "superseded"], key => Assert.Contains(key, keys));
        Assert.All(["id", "computedAt", "validUntil", "horizonEnd", "slotMinutes", "configRevision", "suspended", "cost", "lastSolve"], key => Assert.Contains(key, keys));
        Assert.All(["id", "computedAt", "validUntil", "horizonEnd", "configRevision", "cost"], key => Assert.Equal(JsonValueKind.Null, json.GetProperty(key).ValueKind));
        Assert.False(json.GetProperty("suspended").GetBoolean());
        Assert.Equal("absent", json.GetProperty("status").GetString());
        Assert.Equal(["goalsActive", "goalsInactive", "loadForecast", "prices", "pvForecast"], json.GetProperty("inputs").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(0, json.GetProperty("slots").GetArrayLength());
        var device = json.GetProperty("devices")[0];
        Assert.All(["device", "goal", "goals"], key => Assert.Contains(key, device.EnumerateObject().Select(p => p.Name)));
        Assert.Equal(0, device.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task Through_the_host_a_fresh_install_answers_not_enabled_with_no_goals()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        _ = factory.Server;

        var context = await SendAsync(factory, "GET", Supervisor);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var json = JsonDocument.Parse(context.Response.Body);
        Assert.False(json.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal("absent", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("devices").GetArrayLength());
        Assert.Equal(0, json.RootElement.GetProperty("inputs").GetProperty("goalsActive").GetInt32());
    }

    [Fact]
    public async Task The_endpoint_is_read_only_and_only_for_ingress()
    {
        // Why: no new port and no write beyond ingress (hard rule); a POST must not be a way to set a goal before slice 7 builds the store.
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        _ = factory.Server;

        var post = await SendAsync(factory, "POST", Supervisor);
        var outsider = await SendAsync(factory, "GET", "10.1.2.3");

        Assert.NotEqual(StatusCodes.Status200OK, post.Response.StatusCode);
        Assert.NotEqual(StatusCodes.Status200OK, outsider.Response.StatusCode);
    }

    [Fact]
    public void The_empty_board_is_empty_everywhere()
    {
        var board = GoalBoard.Empty(Now);

        Assert.Equal((Now, 0, 0, 0), (board.At, board.Goals.Length, board.Inactive.Length, board.Superseded.Length));
    }

    private static Task<HttpContext> SendAsync(SwitchboardHostFactory factory, string method, string from) =>
        factory.Server.SendAsync(c =>
        {
            c.Request.Method = method;
            c.Request.Path = "/api/plan";
            c.Connection.RemoteIpAddress = IPAddress.Parse(from);
        });
}
