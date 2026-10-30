using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Switchboard.Application.Configuration;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Goals;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Switchboard.Tests.Application.Goals;

/// <summary>
/// EV plan S5.1 (the open point of the S0 review): <c>GoalTracker.Follow</c> runs on whatever board the evaluator returns, and a board
/// built while a source failed is partial. The ledger of the file's energy goal must not begin, move or end an entry on such a board,
/// or a failing EV source would reset (or end) the progress of a goal it has nothing to do with. Energy keeps being counted.
/// </summary>
public sealed class GoalTrackerDegradedBoardTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly DateTimeOffset Start = new(2026, 10, 26, 17, 0, 0, TimeSpan.Zero);
    private static readonly DeviceName Car = DeviceName.Parse("ev_charger");
    private static readonly SignalName WantSignal = SignalName.Parse("device_ev_charger_goal_want");

    private const string GatedGoal = "      kind: energy\n      target_kwh: 12\n      want: { entity: input_boolean.charge_wanted }\n";

    private sealed class Inputs(ConfigurationResult configuration) : IGoalInputSource
    {
        public GoalInputs Samples { get; set; } = GoalInputs.Empty;

        public GoalInputView Current => new(configuration, Samples);

        public void Want(string state) =>
            Samples = new GoalInputs(ImmutableDictionary<SignalName, GoalInputSample>.Empty.Add(
                WantSignal, new GoalInputSample(SignalState.Fresh, SignalDetail.None, new StateValue(state))));
    }

    private sealed class Switchable : IGoalSource
    {
        public bool Throw { get; set; }

        public bool ReportFailed { get; set; }

        /// <summary>Offers a WantOff for the car (what a healthy source may say; with <see cref="ReportFailed"/> the same content comes from a failed one).</summary>
        public bool SayWantOff { get; set; }

        public GoalOffer Goals(GoalContext context) =>
            Throw ? throw new InvalidOperationException("an EV source failed")
            : ReportFailed || SayWantOff ? new GoalOffer([], [new InactiveGoal(Car, GoalInactiveReason.WantOff, GoalInputKind.Want, GoalOrigin.Module, GoalKind.Energy, false)], Failed: ReportFailed)
            : GoalOffer.Empty;
    }

    private sealed class Rig
    {
        public Rig()
        {
            Time = new FakeTimeProvider(Start);
            Time.SetLocalTimeZone(Warsaw);
            var configuration = TestConfigurations.Valid(TestDevices.Document(
                "  - id: ev_charger\n    type: modulating\n    actuator: number.charger_current\n    phase: L1\n    min: 6\n    max: 16\n    step: 1\n    value_per_kwh: 2.03\n    goal:\n" + GatedGoal));
            Source = new Inputs(configuration);
            Source.Want("on");
            Ledger = new GoalLedger(NullLogger<GoalLedger>.Instance);
            Tracker = new GoalTracker(Source, new GoalEvaluator([new ConfigGoalSource(), Extra]), Ledger, new SignalClock(Time), MsOptions.Create(TestStatistics.Options()), NullLogger<GoalTracker>.Instance);
        }

        public FakeTimeProvider Time { get; }

        public Inputs Source { get; }

        public Switchable Extra { get; } = new();

        public GoalLedger Ledger { get; }

        public GoalTracker Tracker { get; }

        public GoalLedgerEntry? Entry => Ledger.Entry(Car);

        public void Step(TimeSpan after, double watts = 2300)
        {
            Time.Advance(after);
            Tracker.Observe(TestSteps.View(Time.GetUtcNow(), watts, 0, ("ev_charger", watts, PowerSource.Measured)));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_failing_source_does_not_end_the_ledger_entry_of_the_file_goal(bool thrown)
    {
        // Why: the want of the file goal goes off during the very step another source fails. A healthy step would end the entry; on a partial
        // board the tracker must not conclude anything, so the progress survives and the next healthy step decides. Remove the Degraded guard
        // in Follow and the entry is gone after the failing step.
        var rig = new Rig();
        rig.Step(TimeSpan.Zero);
        rig.Step(TimeSpan.FromMinutes(1));
        Assert.NotNull(rig.Entry);
        rig.Source.Want("off");
        rig.Extra.Throw = thrown;
        rig.Extra.ReportFailed = !thrown;

        rig.Step(TimeSpan.FromMinutes(1));

        Assert.NotNull(rig.Entry);

        rig.Extra.Throw = false;
        rig.Extra.ReportFailed = false;
        rig.Step(TimeSpan.FromMinutes(1));

        Assert.Null(rig.Entry);
    }

    [Fact]
    public void A_failed_offer_is_not_trusted_for_what_it_says_but_a_healthy_one_is()
    {
        // Why: pins the contrast the test above relies on. The same WantOff inactive goal ends the entry when the source is healthy and is ignored
        // when the source says it failed (a snapshot that is gone must not end a goal).
        var rig = new Rig();
        rig.Step(TimeSpan.Zero);
        rig.Step(TimeSpan.FromMinutes(1));

        rig.Extra.ReportFailed = true;
        rig.Step(TimeSpan.FromMinutes(1));
        Assert.NotNull(rig.Entry);

        rig.Extra.ReportFailed = false;
        rig.Extra.SayWantOff = true;
        rig.Step(TimeSpan.FromMinutes(1));
        Assert.Null(rig.Entry);
    }

    [Fact]
    public void The_energy_is_still_counted_while_a_source_fails_and_the_ledger_goes_on_after_it_recovers()
    {
        // Why: skipping Follow must not skip Integrate; an under-counted ledger plans more energy than needed. After recovery the same entry continues.
        var rig = new Rig();
        rig.Step(TimeSpan.Zero);
        rig.Step(TimeSpan.FromMinutes(1));
        var since = rig.Entry!.Since;
        var before = rig.Entry.DeliveredKwh;

        rig.Extra.Throw = true;
        rig.Step(TimeSpan.FromMinutes(1));
        rig.Step(TimeSpan.FromMinutes(1));
        var during = rig.Entry!.DeliveredKwh;

        rig.Extra.Throw = false;
        rig.Step(TimeSpan.FromMinutes(1));

        Assert.Equal(before + (2 * 2300.0 / 60 / 1000), during, 9);
        Assert.Equal(since, rig.Entry!.Since);
        Assert.Equal(during + (2300.0 / 60 / 1000), rig.Entry.DeliveredKwh, 9);
    }
}
