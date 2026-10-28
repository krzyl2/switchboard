using System.Collections.Immutable;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Planning;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Planning;
using Switchboard.Tests.Replay;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Dispatch;

/// <summary>
/// PLAN-14, slice 5 acceptance: with <c>planner.dispatch</c> off, with no plan, and with every way a plan can be unusable (older than <c>plan_max_age</c>, from
/// the future, past its horizon, made for another configuration, another spec or another goal, suspended), the dispatch decides byte for byte as v2 did. The
/// proof is the digest of <see cref="EconomicsBackwardCompatibilityTests"/> (the 0.9-pinned worlds, the synthetic morning in shadow, and, when the recording is
/// there, the 72 h live replay) taken with a real <see cref="PlanView"/> over a plan that says <c>Done</c> for every device, the entry that moves most
/// decisions (it stops what runs and refuses what starts). A control row, the same plan fully valid, must move the digest: otherwise the equality would prove
/// nothing about the plan, only that nothing was reading it.
/// </summary>
public sealed class PlannerDispatchDigestTests(DispatchSyntheticReplayRun run) : IClassFixture<DispatchSyntheticReplayRun>
{
    // The digests of EconomicsBackwardCompatibilityTests (taken at the 0.9 engine; the log line's list version is read as 6 there, see its Add).
    private const string BalanceWorlds = "B604FE86D71EAF34922B01FACC2909A1DB3EF0DDAF849B48F5EA23D831484135";
    private const string SyntheticShadow = "1A552444141477480FE599C63E4E003FB05E74BB5CF2E6E4476DC641DD9CE446";

    private static readonly string[] DeviceNames = ["heater_l1", "heater_l2", "heater_l3", "pool", "ev_charger"];

    public static TheoryData<string> UnusableRows() =>
    [
        "mode_off",
        "no_plan",
        "stale",
        "from_the_future",
        "past_the_horizon",
        "other_configuration",
        "other_spec",
        "other_goal",
        "suspended",
    ];

    /// <summary>
    /// A real plan view over a plan that is <c>Done</c> for every device in every slot of five days, and a currency that agrees with it, except for the one thing
    /// the row spoils. <paramref name="start"/> is the first instant of the run.
    /// </summary>
    internal static PlanView View(string row, DateTimeOffset start)
    {
        var computedAt = row == "stale" ? start - TimeSpan.FromHours(2) : row == "from_the_future" ? start + TimeSpan.FromDays(1) : start - TimeSpan.FromMinutes(1);
        var anchor = row == "past_the_horizon" ? start - TimeSpan.FromDays(10) : computedAt;
        var inputs = PlannerFixtures.Custom(anchor, 24 * 5, _ => (null, null, null, 0));
        var goal = new PlanGoalRef("goal", 1, 7);
        var devices = DeviceNames.Select(name => new DevicePlan(
            DeviceName.Parse(name),
            [],
            [.. inputs.Slots.Select(s => new PlanEntry(s.Slot.Start, s.Slot.End, PlanWant.Done, null, 0, 0, goal))])).ToImmutableArray();
        var plan = new PlanSnapshot(computedAt, 1, inputs, devices, new PlanCost(0, 0, 0), []);

        var holder = new PlanHolder();
        if (row != "no_plan")
        {
            holder.Publish(new PlanState(plan, row == "suspended", null, [], TimeSpan.FromMinutes(45)));
        }

        var currency = new StubCurrency
        {
            Mode = row == "mode_off" ? PlannerDispatchMode.Off : PlannerDispatchMode.Full,
            ConfigRevision = row == "other_configuration" ? 2 : 1,
            AnyGoal = row == "other_goal" ? null : new PlanGoalState(null, true),
        };
        foreach (var name in DeviceNames)
        {
            currency.Specs[DeviceName.Parse(name)] = row == "other_spec" ? 8 : 7;
        }

        return new PlanView(holder, currency, NullLogger<PlanView>.Instance);
    }

    private static string SyntheticDigest(DispatchSyntheticReplayRun run, string row)
    {
        var harness = new GuardReplayHarness(run.Data, DispatchSyntheticReplayRun.HeadroomConfiguration, dispatch: true, plan: View(row, run.Data.Start));
        harness.RunUntil(run.Data.End);
        var text = new StringBuilder();
        foreach (var step in harness.Steps)
        {
            EconomicsBackwardCompatibilityTests.Add(text, step.Decisions, step.Dispatch);
        }

        return EconomicsBackwardCompatibilityTests.Sha(text);
    }

    [Theory]
    [MemberData(nameof(UnusableRows))]
    public void The_balance_worlds_decide_as_v2_when_the_plan_is_off_absent_or_unusable(string row)
    {
        // Why: the maintainer's installed base. A planner that is off, hasn't planned, is stale, or planned for something else must leave every 0.9 world exactly
        // where it was: if any of the rows below moved a decision, "absence is v2" would be a sentence in a document and not a property of the code.
        var start = GuardScenario.Start;

        var digest = EconomicsBackwardCompatibilityTests.BalanceWorldsDigest(View(row, start));

        EconomicsBackwardCompatibilityTests.Same(BalanceWorlds, digest);
    }

    [Theory]
    [MemberData(nameof(UnusableRows))]
    public void The_synthetic_morning_decides_as_v2_when_the_plan_is_off_absent_or_unusable(string row)
    {
        // Why: the same proof where the guard, the effector and dispatch interact (kettles, foreign switching, a lost sample): the plan view is asked at every
        // step of a whole day and never changes an answer.
        EconomicsBackwardCompatibilityTests.Same(SyntheticShadow, SyntheticDigest(run, row));
    }

    [Fact]
    public void The_same_plan_made_valid_does_move_the_digest_so_the_equalities_above_can_fail()
    {
        // Why: control. A Done plan for every device, valid in every respect, stops what runs and refuses what starts; if it did not change the digest, the rows
        // above would be passing for want of a plan that could ever matter.
        Assert.NotEqual(SyntheticShadow, SyntheticDigest(run, "valid"));
        Assert.NotEqual(BalanceWorlds, EconomicsBackwardCompatibilityTests.BalanceWorldsDigest(View("valid", GuardScenario.Start)));
    }

    [Fact]
    public void A_view_that_hands_out_no_entry_in_full_mode_decides_as_v2_and_writes_no_plan_block()
    {
        // Why: a mode of full with nothing in the plan for any device (a goal-less installation, the planner's first minutes) is not "narrow" either: nothing at all.
        var view = new ScriptedPlanView(PlannerDispatchMode.Full);
        var harness = new GuardReplayHarness(run.Data, DispatchSyntheticReplayRun.HeadroomConfiguration, dispatch: true, plan: view);
        harness.RunUntil(run.Data.End);
        var text = new StringBuilder();
        foreach (var step in harness.Steps)
        {
            EconomicsBackwardCompatibilityTests.Add(text, step.Decisions, step.Dispatch);
        }

        EconomicsBackwardCompatibilityTests.Same(SyntheticShadow, EconomicsBackwardCompatibilityTests.Sha(text));
        Assert.True(view.Asked > 0);
        Assert.All(harness.Decisions.Where(d => d.Inputs.Dispatch is not null), d => Assert.Null(d.Inputs.Dispatch!.Plan));
    }
}

[Trait("Category", "LiveReplay")]
public sealed class PlannerLiveDispatchDigestTests
{
    private const string Live = "DB500B213B67B94C712727563568DE19776467F0B785F917747AC5E551F282B6";

    [Theory]
    [InlineData("mode_off")]
    [InlineData("no_plan")]
    [InlineData("stale")]
    [InlineData("other_configuration")]
    [InlineData("suspended")]
    public void The_live_72h_decides_as_v2_when_the_plan_is_off_absent_or_unusable(string row)
    {
        // Why: the recording is the maintainer's real household, the strongest PLAN-14 evidence there is: a plan that dispatch must not use, however perfect it
        // looks, changes none of 72 hours of decisions. Fails loudly when the recording is absent, like every live replay test.
        var data = ReplayFixtures.LiveData;
        var harness = new GuardReplayHarness(data, DispatchLiveReplayTests.Stage3Example, dispatch: true, plan: PlannerDispatchDigestTests.View(row, data.Start));
        harness.RunUntil(data.End);
        var text = new StringBuilder();
        foreach (var step in harness.Steps)
        {
            EconomicsBackwardCompatibilityTests.Add(text, step.Decisions, step.Dispatch);
        }

        EconomicsBackwardCompatibilityTests.Same(Live, EconomicsBackwardCompatibilityTests.Sha(text));
    }
}
