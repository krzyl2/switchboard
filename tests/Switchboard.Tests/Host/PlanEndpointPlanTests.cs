using System.Collections.Immutable;
using System.Text.Json;
using Switchboard.Application.Configuration;
using Switchboard.Application.Goals;
using Switchboard.Application.Planning;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Planning;
using Switchboard.Host.Web;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.PlannerFixtures;

namespace Switchboard.Tests.Host;

/// <summary>
/// GET api/plan with a plan (15-PLANNER, API; PLAN-10 to PLAN-12): the plan the planner service holds, shown beside the goals and the slots of slices 1 to 3.
/// Why it matters: the day-plan screen (slice 7) and the maintainer's shadow week read the plan from here, so what is shown must be what was computed (entries
/// of the same slots, the same cost), must say how old it is and whether it can be used, and must never make a held-over, suspended or failed plan look fresh.
/// The keys of the earlier slices stay (the contract only grew).
/// </summary>
public sealed class PlanEndpointPlanTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static ConfigurationResult Config(string planner = "\nplanner:\n  dispatch: off\n") =>
        TestConfigurations.Valid(TestConfigurations.ThreePhase() + "\n" + File.ReadAllText(RepoPaths.Combine("deploy", "examples", "economics-g13.yaml")) + planner);

    private static (PlanInput Input, PlanSnapshot Plan) Plan()
    {
        var input = WorkedExample();
        return (input, Solve(input));
    }

    private static GoalBoard Board(PlanInput input, DateTimeOffset at)
    {
        var request = input.Goals.Single();
        return new GoalBoard(at, [Switchboard.Domain.Goals.Goal.Of(request, at)], [], []);
    }

    private static JsonElement Answer(ConfigurationResult config, GoalBoard board, PlanState? state) =>
        JsonSerializer.SerializeToElement(PlanEndpoint.Build(config, board, null, null, state), Web);

    private static PlanState Fresh(PlanSnapshot plan, SolveReport? solve = null) =>
        new(plan, false, solve ?? new SolveReport(plan.ComputedAt, TimeSpan.FromMilliseconds(3), SolveResult.Computed, "first", []), [], Parameters().PlanMaxAge);

    [Fact]
    public void A_fresh_plan_is_shown_with_its_id_age_slots_entries_cost_and_last_solve()
    {
        // Why: the endpoint must show what the planner computed and nothing else: the entries are those of the plan's own slots (one per slot, same boundaries), the
        // cost is the plan's own, the plan says when it stops being usable (computed at plus plan_max_age) and the goal carries the plan's verdict and its latest
        // safe start.
        var (input, plan) = Plan();
        var board = Board(input, plan.ComputedAt);

        var json = Answer(Config(), board, Fresh(plan));

        Assert.Equal("fresh", json.GetProperty("status").GetString());
        Assert.Equal(plan.Id, json.GetProperty("id").GetString());
        Assert.Equal(plan.ComputedAt, json.GetProperty("computedAt").GetDateTimeOffset());
        Assert.Equal(plan.ComputedAt + Parameters().PlanMaxAge, json.GetProperty("validUntil").GetDateTimeOffset());
        Assert.Equal(plan.HorizonEnd, json.GetProperty("horizonEnd").GetDateTimeOffset());
        Assert.Equal(60, json.GetProperty("slotMinutes").GetDouble());
        Assert.Equal(plan.ConfigRevision, json.GetProperty("configRevision").GetInt64());
        Assert.False(json.GetProperty("suspended").GetBoolean());
        Assert.Equal(plan.Slots.Length, json.GetProperty("slots").GetArrayLength());

        var cost = json.GetProperty("cost");
        Assert.Equal(plan.Cost.Planned, cost.GetProperty("planned").GetDouble(), 9);
        Assert.Equal(plan.Cost.ReferenceV2, cost.GetProperty("referenceV2").GetDouble(), 9);
        Assert.Equal(plan.Cost.ReferenceAsap, cost.GetProperty("referenceAsap").GetDouble(), 9);
        Assert.Equal("PLN", cost.GetProperty("currency").GetString());

        var device = json.GetProperty("devices").EnumerateArray().Single();
        Assert.Equal("ev_charger", device.GetProperty("device").GetString());
        var entries = device.GetProperty("entries");
        Assert.Equal(plan.Slots.Length, entries.GetArrayLength());
        Assert.Equal(plan.Slots[0].Slot.Start, entries[0].GetProperty("start").GetDateTimeOffset());
        var run = entries.EnumerateArray().Where(e => e.GetProperty("want").GetString() == "run").ToList();
        Assert.Equal(6, run.Count);
        Assert.All(run, e => Assert.Equal("pv", e.GetProperty("class").GetString()));
        Assert.Equal(9.9, run.Sum(e => e.GetProperty("energyKwh").GetDouble()), 9);
        Assert.Equal(0, device.GetProperty("shortfallKwh").GetDouble(), 9);
        Assert.NotEqual(JsonValueKind.Null, device.GetProperty("predictedCompletion").ValueKind);

        var goal = device.GetProperty("goal");
        Assert.Equal("on_track", goal.GetProperty("status").GetString());
        Assert.Equal(9.9, goal.GetProperty("plannedKwh").GetDouble(), 9);
        Assert.NotEqual(JsonValueKind.Null, goal.GetProperty("latestGridStart").ValueKind);
        Assert.False(goal.GetProperty("urgent").GetBoolean());

        var solve = json.GetProperty("lastSolve");
        Assert.Equal("computed", solve.GetProperty("result").GetString());
        Assert.Equal("first", solve.GetProperty("reason").GetString());
        Assert.Equal(3, solve.GetProperty("durationMs").GetDouble(), 6);
    }

    [Fact]
    public void A_plan_older_than_the_maximum_age_is_shown_stale_not_fresh()
    {
        // Why: an old plan must never look current: the status is stale (what dispatch will treat as absent from slice 5), the entries are still there to look at,
        // and the goal falls back to the goal model's own status instead of the plan's verdict.
        var (input, plan) = Plan();
        var later = plan.ComputedAt + Parameters().PlanMaxAge + TimeSpan.FromMinutes(1);

        var json = Answer(Config(), Board(input, later), Fresh(plan));

        Assert.Equal("stale", json.GetProperty("status").GetString());
        Assert.Equal(plan.Slots.Length, json.GetProperty("devices")[0].GetProperty("entries").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("devices")[0].GetProperty("goal").GetProperty("plannedKwh").ValueKind);
    }

    [Fact]
    public void A_suspended_planner_says_so_and_keeps_showing_the_last_plan_as_suspended()
    {
        // Why: with the guard in safe state, the kill switch on or Home Assistant gone, the plan is ignored: status suspended and the flag, not fresh.
        var (input, plan) = Plan();
        var state = Fresh(plan) with { Suspended = true };

        var json = Answer(Config(), Board(input, plan.ComputedAt), state);

        Assert.Equal("suspended", json.GetProperty("status").GetString());
        Assert.True(json.GetProperty("suspended").GetBoolean());
    }

    [Fact]
    public void A_failed_solve_with_no_plan_is_an_error_with_the_violations_and_a_held_plan_carries_the_note()
    {
        // Why: the failure behaviour of the design is visible: no plan and a refused one is error with the verifier's codes; a plan held over a failed solve stays
        // fresh but names the failure in the notes (plan_invalid, solver_timeout) and in lastSolve, so a failing planner is never silent.
        var (input, plan) = Plan();
        var refused = new SolveReport(plan.ComputedAt, TimeSpan.FromMilliseconds(5), SolveResult.Invalid, "cadence", [new PlanViolation(PlanViolationCode.CapacityExceeded, null, null, "over")]);

        var none = Answer(Config(), Board(input, plan.ComputedAt), new PlanState(null, false, refused, [PlanNote.PlanInvalid], Parameters().PlanMaxAge));
        var held = Answer(Config(), Board(input, plan.ComputedAt), new PlanState(plan, false, refused, [PlanNote.PlanInvalid], Parameters().PlanMaxAge));

        Assert.Equal("error", none.GetProperty("status").GetString());
        Assert.Equal("invalid", none.GetProperty("lastSolve").GetProperty("result").GetString());
        Assert.Equal(["capacity_exceeded"], none.GetProperty("lastSolve").GetProperty("violations").EnumerateArray().Select(v => v.GetString()).ToArray());
        Assert.Equal("fresh", held.GetProperty("status").GetString());
        Assert.Contains("plan_invalid", held.GetProperty("notes").EnumerateArray().Select(n => n.GetString()));
    }

    [Fact]
    public void The_plans_own_notes_are_listed_once_with_the_inputs_notes()
    {
        // Why: the notes say what the plan could not do: a missing forecast (an input note) and a goal at risk (the plan's note) are both in the list, sorted, once.
        var input = WorkedExample(withPv: false);
        var plan = Solve(input with { Goals = [Goal(Car, 9.9, Local(2026, 10, 26, 19), sources: Offpeak)] });
        var board = Board(input, plan.ComputedAt);

        var json = Answer(Config(), board, Fresh(plan));

        var notes = json.GetProperty("notes").EnumerateArray().Select(n => n.GetString()).ToList();
        Assert.Contains("pv_forecast_missing", notes);
        Assert.Equal(notes.Distinct().Count(), notes.Count);
    }

    [Fact]
    public void Without_a_planner_section_a_held_plan_is_not_shown()
    {
        // Why: removing planner: from the file is v2 exactly; the endpoint must not keep serving a plan of a configuration that no longer has one.
        var (input, plan) = Plan();

        var json = Answer(Config(planner: ""), Board(input, plan.ComputedAt), Fresh(plan));

        Assert.Equal("absent", json.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("id").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("cost").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("lastSolve").ValueKind);
        Assert.Equal(0, json.GetProperty("devices")[0].GetProperty("entries").GetArrayLength());
    }
}
