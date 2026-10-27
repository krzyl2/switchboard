using System.Collections.Immutable;
using System.Text.Json;
using Switchboard.Application.Configuration;
using Switchboard.Application.Goals;
using Switchboard.Application.Planning;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Planning;
using Switchboard.Domain.Signals;
using Switchboard.Host.Web;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.PlannerFixtures;

namespace Switchboard.Tests.Host;

/// <summary>
/// The SPA reads GET api/plan strictly (web/src/api/plan.ts) and POSTs api/goals (web/src/api/goals.ts): a closed list that drifts from the backend's
/// enum, or a key the endpoint stopped writing, turns the day-plan screen and the device card's goal row "unavailable" instead of wrong. These tests hold the
/// lists and the keys of the committed sample to what the endpoint writes, and the refusal codes of api/goals to the ones the SPA words.
/// </summary>
public sealed class PlanSpaContractTests
{
    private static readonly string Contract = RepoPaths.Combine("web", "src", "api", "plan.ts");
    private static readonly string GoalsContract = RepoPaths.Combine("web", "src", "api", "goals.ts");
    private static readonly string SampleFile = RepoPaths.Combine("web", "src", "api", "plan.sample.json");
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static ConfigurationResult Config() =>
        TestConfigurations.Valid(TestConfigurations.ThreePhase() + "\n" + File.ReadAllText(RepoPaths.Combine("deploy", "examples", "economics-g13.yaml")) + "\nplanner:\n  dispatch: off\n");

    /// <summary>
    /// The worked example's fresh plan with the car's goal from a mapped entity, a heater whose goal is off, and a request the merge set aside: every nested
    /// object of the endpoint's answer is present.
    /// </summary>
    private static JsonElement Full(out DateTimeOffset at)
    {
        var input = WorkedExample();
        var plan = Solve(input);
        at = plan.ComputedAt + TimeSpan.FromMinutes(2);
        var request = input.Goals.Single() with { Origin = GoalOrigin.Entity, DeliveredKwh = null };
        var superseded = request with { Origin = GoalOrigin.Config, RemainingKwh = 4.4, Revision = 2 };
        var board = new GoalBoard(
            at,
            [Switchboard.Domain.Goals.Goal.Of(request, at)],
            [new InactiveGoal(DeviceName.Parse("heater_l1"), GoalInactiveReason.WantOff, GoalInputKind.Want, GoalOrigin.Entity, GoalKind.Energy, false)],
            [superseded]);
        var state = new PlanState(plan, false, new SolveReport(plan.ComputedAt, TimeSpan.FromMilliseconds(3), SolveResult.Computed, "first", []), [], Parameters().PlanMaxAge);
        return JsonSerializer.SerializeToElement(PlanEndpoint.Build(Config(), board, null, null, state), Web);
    }

    [Fact]
    public void Spa_closed_lists_match_the_backend_enums_exactly()
    {
        // Why: a status, note, want or basis the SPA does not know would make the whole screen "unavailable"; one it lists but the backend never sends is
        // dead text. Both directions are held.
        static string[] Names<T>() where T : struct, Enum => [.. Enum.GetValues<T>().Select(ApiText.Snake).Order(StringComparer.Ordinal)];
        static string[] Spa(string name) => [.. SpaContract.List(Contract, name).Order(StringComparer.Ordinal)];

        Assert.Equal(Names<PlanStatus>(), Spa("PLAN_STATUSES"));
        Assert.Equal(Names<PlannerDispatchMode>(), Spa("DISPATCH_MODES"));
        Assert.Equal(Names<GoalKind>(), Spa("GOAL_KINDS"));
        Assert.Equal(Names<GoalOrigin>(), Spa("GOAL_SOURCES"));
        Assert.Equal([.. Names<GoalStatus>().Append("superseded").Order(StringComparer.Ordinal)], Spa("GOAL_STATUSES"));
        Assert.Equal(Names<GoalOnMet>(), Spa("GOAL_ON_MET"));
        Assert.Equal(Names<GoalInactiveReason>(), Spa("INACTIVE_REASONS"));
        Assert.Equal(Names<GoalInputKind>(), Spa("INACTIVE_INPUTS"));
        Assert.Equal(Names<PlanNote>(), Spa("PLAN_NOTES"));
        Assert.Equal(Names<PlanWant>(), Spa("ENTRY_WANTS"));
        Assert.Equal(Names<PvBasis>(), Spa("PV_BASES"));
        Assert.Equal(Names<LoadBasis>(), Spa("LOAD_BASES"));
        Assert.Equal(Names<PlanPriceBasis>(), Spa("PRICE_BASES"));
        Assert.Equal(Names<SolveResult>(), Spa("SOLVE_RESULTS"));
        Assert.Equal(Names<PlanViolationCode>(), Spa("VIOLATION_CODES"));
    }

    [Fact]
    public void Spa_sample_has_exactly_the_keys_the_endpoint_writes()
    {
        var real = Full(out var at);
        var sample = SpaContract.Sample(SampleFile, real, at);

        Assert.Equal(SpaContract.Describe([real]), SpaContract.Describe([sample]));
        // The sample is the interesting case: a fresh plan with entries, a cost, a last solve, an inactive goal and a set-aside request.
        Assert.Equal("fresh", sample.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, sample.GetProperty("cost").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, sample.GetProperty("lastSolve").ValueKind);
        Assert.Equal(2, sample.GetProperty("devices").GetArrayLength());
        Assert.Equal(1, sample.GetProperty("superseded").GetArrayLength());
        Assert.True(sample.GetProperty("slots").GetArrayLength() > 24);
    }

    [Fact]
    public void Spa_goal_refusal_codes_are_the_ones_the_endpoint_sends()
    {
        // Why: each refusal of POST api/goals is worded in the SPA by its code; a code the SPA does not list would show as a generic failure.
        var sent = new[]
        {
            GoalsEndpoint.BadRequest, GoalsEndpoint.UnknownDevice, GoalsEndpoint.NoConfiguration, GoalsEndpoint.NoManualGoal, GoalsEndpoint.SocNotConfigured,
            GoalsEndpoint.TargetOutOfRange, GoalsEndpoint.DeadlinePassed, GoalsEndpoint.DeadlineBeyondHorizon,
        };

        Assert.Equal([.. sent.Order(StringComparer.Ordinal)], [.. SpaContract.List(GoalsContract, "GOAL_REFUSALS").Order(StringComparer.Ordinal)]);
    }
}
