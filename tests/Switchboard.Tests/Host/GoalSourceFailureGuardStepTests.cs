using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Switchboard.Application.Configuration;
using Switchboard.Application.Goals;
using Switchboard.Application.Planning;
using Switchboard.Application.Signals;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Application.Decisions;
using Switchboard.Domain.Planning;
using Switchboard.Domain.Goals;
using Switchboard.Host.Composition;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.PlannerFixtures;

namespace Switchboard.Tests.Host;

/// <summary>
/// EV plan B1 through the real chain: <c>GuardRunner.Step</c> -> dispatch -> <c>PlanView.For</c> -> <c>DiagnosticsPlanCurrency.Goal</c> -> <c>GoalTracker</c> ->
/// <c>GoalEvaluator</c> -> a goal source that throws. The guard loop must keep stepping and the safe state must be exactly what it is without the failure.
/// </summary>
public sealed class GoalSourceFailureGuardStepTests
{
    private const string Tariff = "economics:\n  currency: PLN\n  tariff:\n    default_zone: offpeak\n    zones:\n      - id: offpeak\n        price: 0.60\n        when: []\n  settlement:\n    model: hourly_netting\n    export_price: 0.35\n";

    private const string EnergyGoal = "    goal:\n      kind: energy\n      target_kwh: 12\n      loss_fraction: 0.10\n      deadline: { at: \"06:30\" }\n";

    private sealed class ThrowingSource : IGoalSource
    {
        public bool Throw { get; set; }

        public GoalOffer Goals(GoalContext context) => Throw ? throw new InvalidOperationException("a goal source blew up") : GoalOffer.Empty;
    }

    private sealed class HarnessInputs(GuardRunnerHarness harness) : IGoalInputSource
    {
        public GoalInputView Current => new(harness.Holder.Current.Configuration, GoalInputs.Empty);
    }

    /// <summary>The plan view the dispatch engine holds, bound after the harness exists (the currency reads the harness's snapshot holder).</summary>
    private sealed class LatePlanView : IPlanView
    {
        public IPlanView? Inner { get; set; }

        public Switchboard.Domain.Planning.PlannerDispatchMode Mode => Inner?.Mode ?? Switchboard.Domain.Planning.PlannerDispatchMode.Off;

        public PlanEntryView? For(DeviceName device, DateTimeOffset now) => Inner?.For(device, now);

        public void Observe(DateTimeOffset now) => Inner?.Observe(now);
    }

    private sealed class Rig
    {
        public Rig()
        {
            var devices = TestDevices.Reference.ReplaceLineEndings("\n").TrimEnd('\n') + "\n" + EnergyGoal;
            var late = new LatePlanView();
            Harness = new GuardRunnerHarness(TestDevices.Document(devices, extra: "planner:\n  dispatch: full\n" + Tariff), plan: late);
            Assert.True(Harness.Configuration.Status == ConfigurationStatus.Valid, string.Join("; ", Harness.Configuration.Errors));
            Harness.PushReference();

            var clock = new SignalClock(Harness.Time);
            Tracker = new GoalTracker(
                new HarnessInputs(Harness),
                new GoalEvaluator([Source, new ConfigGoalSource()], Logs.For<GoalEvaluator>()),
                new GoalLedger(NullLogger<GoalLedger>.Instance),
                clock,
                Options.Create(TestStatistics.Options()),
                NullLogger<GoalTracker>.Instance);
            var planner = new DiagnosticsPlannerSource(Harness.Holder, Tracker, null!, clock);
            Currency = new DiagnosticsPlanCurrency(Harness.Holder, planner, Tracker);

            // A plan made, as the planner service would, for the configuration in force, the charger's spec and the goal the tracker states now.
            var now = Harness.Now;
            var configuration = Harness.Holder.Current.Configuration!.Configuration!;
            var spec = Harness.Holder.Current.Snapshot.Protection.Setup!.Devices.Single(d => d.Id == Car);
            var goal = Assert.Single(Tracker.Evaluate(now).Goals).Request;
            var inputs = Inputs(now, PvForecast.None, [goal.UsableDeadline!.Value, goal.Deadline!.Value]);
            var charger = Charger() with { SpecIdentity = DiagnosticsPlannerSource.Identity(spec) };
            var plan = Solve(Input(now, inputs, [charger], [goal], revision: Currency.ConfigRevision!.Value));
            Plans.Publish(new PlanState(plan, false, null, [], TimeSpan.FromMinutes(45)));
            View = new PlanView(Plans, Currency, NullLogger<PlanView>.Instance);
            late.Inner = View;
            Assert.NotEqual(PlanWant.None, plan.EntryAt(Car, now)!.Want);
        }

        public GuardRunnerHarness Harness { get; }

        public ThrowingSource Source { get; } = new();

        public LogCapture Logs { get; } = new();

        public GoalTracker Tracker { get; }

        public DiagnosticsPlanCurrency Currency { get; }

        public PlanHolder Plans { get; } = new();

        public PlanView View { get; }
    }

    [Fact]
    public void A_throwing_goal_source_does_not_stop_the_guard_step_and_the_other_sources_still_serve_the_plan()
    {
        // Why: the plan view asks the goal model during every guard step. Without the isolation the exception leaves GuardRunner.Step before the effector gate runs.
        var rig = new Rig();
        Assert.NotNull(rig.View.For(Car, rig.Harness.Now));

        rig.Source.Throw = true;

        // The currency answers the goal board once per instant, so the failing evaluation needs a later instant than the healthy one above.
        // The config source is healthy, so its goal (and with it the plan entry) is still there: only the failing source's goals are missing.
        Assert.NotNull(rig.View.For(Car, rig.Harness.Now + TimeSpan.FromSeconds(1)));
        Assert.Single(rig.Logs.Entries);

        // The guard steps are what must survive; the failure is still the same streak, so they add no second log line.
        var exception = Record.Exception(() => rig.Harness.Runner.Step());
        Assert.Null(exception);
        rig.Harness.Time.Advance(TimeSpan.FromSeconds(30));
        rig.Harness.PushReference();
        Assert.Null(Record.Exception(() => rig.Harness.Runner.Step()));
        Assert.Single(rig.Logs.Entries);
    }

    [Fact]
    public void The_safe_state_is_the_same_with_a_throwing_goal_source_as_without_one()
    {
        // Why: protection outranks everything. When the meter goes stale the guard holds every controlled device off whatever the planner side is doing; a goal
        // source failing in that very step must not cost a single safe-state decision.
        static List<(DecisionAction Action, string? Device, ReasonCode Reason)> SafeStateDecisions(bool broken)
        {
            var rig = new Rig();
            rig.Source.Throw = broken;
            rig.Harness.Runner.Step();
            rig.Harness.Time.Advance(TimeSpan.FromMinutes(5));
            rig.Harness.Publish();
            rig.Harness.Runner.Step();
            return [.. rig.Harness.Recorder.Decisions
                .Select(d => (d.Action, d.Device?.Value, d.Reason))];
        }

        var without = SafeStateDecisions(broken: false);
        var with = SafeStateDecisions(broken: true);

        Assert.Contains(without, d => d.Action == DecisionAction.SafeStateEntered);
        Assert.Contains(without, d => d.Action == DecisionAction.SafeStateEntered && d.Reason == ReasonCode.GuardStaleSignal);
        Assert.Equal(without, with);
    }
}
