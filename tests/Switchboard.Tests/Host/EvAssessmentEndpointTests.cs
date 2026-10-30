using System.Text.Json;
using Switchboard.Application.Ev;
using Switchboard.Application.Goals;
using Switchboard.Application.Signals;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Ev;
using Switchboard.Domain.Goals;
using Switchboard.Host.Web;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// The EV assessment in GET api/ev and the <c>cause</c> of the module's goals in GET api/plan (EV-06, EV-07, docs/EV.md section 3 point 5). Why it matters: the
/// module has its own closed list so that the reason codes of every decision line stay at list version 7; the UI must be able to tell "the module offers nothing
/// because it failed" from "because there is nothing to do", and a goal must say why it exists.
/// </summary>
public sealed class EvAssessmentEndpointTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static EvResponse Build(EvRig rig, EvPublished? published = null, DateTimeOffset? at = null) =>
        EvEndpoint.Build(rig.H.Configuration, at ?? rig.Now, ((IEvInputSource)rig).Current.Inputs, EvRig.Warsaw, published ?? rig.Keeper.Current);

    [Fact]
    public void The_assessment_names_the_reason_the_notes_and_the_goals_in_the_modules_own_words()
    {
        // Why: the planner is at dispatch off in this configuration, so the module says it is a preview (a note) next to why it wants energy.
        var rig = new EvRig();
        rig.PlugInCar(40);
        rig.Tick();

        var assessment = Build(rig).Assessment!;

        Assert.False(assessment.Failed);
        Assert.Equal("ev_surplus", assessment.Reason);
        Assert.Contains("ev_without_planner_dispatch", assessment.Notes);
        Assert.Equal("auto", assessment.Mode);
        var goal = Assert.Single(assessment.Goals);
        Assert.Equal(("cap", 90.0, false), (goal.Kind, goal.TargetPercent, goal.Hard));
        Assert.StartsWith("pv", goal.Sources, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_assessment_code_has_its_own_wire_text_and_the_decision_reason_list_stays_at_version_7()
    {
        // Why: EV.md 3.5. A v8 reason list would change the list number of every decision line and make v7 lines with a plan block unreadable; the module's states
        // are a list of their own, one text each.
        var texts = EvAssessmentCodes.All.Select(EvAssessmentCodes.Text).ToList();

        Assert.Equal(14, texts.Count);
        Assert.Equal(texts.Count, texts.Distinct().Count());
        Assert.All(texts, t => Assert.Matches("^ev_[a-z_]+$", t));
        Assert.Equal(7, ReasonCodes.ListVersion);
    }

    [Fact]
    public void A_failed_computation_says_failed_and_offers_no_goals()
    {
        // Why: "nothing to do" and "the module broke" must not look alike; after a failure the car is v2 and the user should hear about it.
        var rig = new EvRig();

        var assessment = Build(rig, new EvPublished(null, true)).Assessment!;

        Assert.True(assessment.Failed);
        Assert.Null(assessment.Reason);
        Assert.Empty(assessment.Goals);
    }

    [Fact]
    public void A_snapshot_past_its_age_is_shown_as_expired()
    {
        // Why: the guard no longer sees its goals; the page must not show them as current.
        var rig = new EvRig();
        rig.PlugInCar(40);
        var snapshot = rig.Tick();

        var assessment = Build(rig, at: snapshot.ValidUntil + TimeSpan.FromSeconds(1)).Assessment!;

        Assert.True(assessment.Expired);
        Assert.False(Build(rig, at: snapshot.ValidUntil).Assessment!.Expired);
    }

    [Fact]
    public void Before_the_first_computation_and_without_a_section_there_is_no_assessment()
    {
        var rig = new EvRig();
        var none = TestConfigurations.Parse(TestDevices.Document(EvRig.Charger, extra: "planner:\n  dispatch: off\n"));

        Assert.Null(Build(rig, EvPublished.None).Assessment);
        Assert.Null(EvEndpoint.Build(none, rig.Now, null, null, new EvPublished(rig.Tick(), false)).Assessment);
    }

    [Fact]
    public void The_assessment_serializes_without_an_entity_id()
    {
        // Why: the core never knows an entity ID; the answer carries logical names and the module's own codes only.
        var rig = new EvRig();
        rig.PlugInCar(40);
        rig.PushCalendar(rig.Trip(TimeSpan.FromHours(10), 42));
        rig.Tick();

        var json = JsonSerializer.Serialize(Build(rig), Web);

        Assert.DoesNotContain("sensor.", json, StringComparison.Ordinal);
        Assert.DoesNotContain("calendar.", json, StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"ev_trip_deadline\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_module_goal_in_api_plan_carries_the_modules_reason_and_no_other_goal_does()
    {
        // Why: the day-plan screen must say why the car's goal exists in the module's words; a goal of another source has no such cause.
        var rig = new EvRig();
        rig.PlugInCar(40);
        rig.Tick();
        var evaluator = new GoalEvaluator([rig.Source], Microsoft.Extensions.Logging.Abstractions.NullLogger<GoalEvaluator>.Instance);
        var board = evaluator.Evaluate(new GoalContext(rig.Now, rig.Configuration, GoalInputs.Empty, EvRig.Warsaw, new GoalLedger(Microsoft.Extensions.Logging.Abstractions.NullLogger<GoalLedger>.Instance)));
        var other = board.Goals[0] with { Request = board.Goals[0].Request with { Origin = GoalOrigin.Config } };

        var plan = PlanEndpoint.Build(rig.H.Configuration, board, ev: rig.Keeper.Current);
        var other1 = PlanEndpoint.Build(rig.H.Configuration, board with { Goals = [other] }, ev: rig.Keeper.Current);

        var goal = Assert.Single(plan.Devices.Single().Goals);
        Assert.Equal(("module", "ev_surplus"), (goal.Source, goal.Cause));
        Assert.Null(Assert.Single(other1.Devices.Single().Goals).Cause);
    }

    [Fact]
    public void Each_module_goal_in_api_plan_carries_the_cause_of_its_own_kind()
    {
        // Why: one assessment code on every goal would call the floor "ev_trip_deadline" the moment a trip exists. The day-plan screen asks "why does the car have
        // this goal": the trip says the deadline, the cap the surplus, the floor the cheap zone.
        var rig = new EvRig();
        rig.PlugInCar(20);
        rig.PushCalendar(rig.Trip(TimeSpan.FromHours(10), 5));
        rig.Tick();
        var evaluator = new GoalEvaluator([rig.Source], Microsoft.Extensions.Logging.Abstractions.NullLogger<GoalEvaluator>.Instance);
        var board = evaluator.Evaluate(new GoalContext(rig.Now, rig.Configuration, GoalInputs.Empty, EvRig.Warsaw, new GoalLedger(Microsoft.Extensions.Logging.Abstractions.NullLogger<GoalLedger>.Instance)));

        var goals = PlanEndpoint.Build(rig.H.Configuration, board, ev: rig.Keeper.Current).Devices.Single().Goals;

        var causes = goals.Select(g => g.Cause).ToList();
        Assert.Contains("ev_trip_deadline", causes);
        Assert.All(causes, c => Assert.StartsWith("ev_", c, StringComparison.Ordinal));
        Assert.True(goals.Count >= 2, "a trip and a cap at least");
        Assert.Contains("ev_surplus", causes);
    }
}
