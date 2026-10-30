using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Switchboard.Application.Goals;
using Switchboard.Application.Signals;
using Switchboard.Domain.Goals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Goals;

/// <summary>
/// EV plan B1: <see cref="GoalEvaluator"/> runs inside the guard step (the plan view asks it for the goals in force), so a goal source that throws must offer
/// nothing and nothing more: the other sources are unaffected, the failure is logged once per streak, and the guard never sees the exception.
/// </summary>
public sealed class GoalEvaluatorIsolationTests
{
    private const string EnergyGoal = "      kind: energy\n      target_kwh: 12\n      loss_fraction: 0.10\n      deadline: { at: \"06:30\" }\n";

    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    private sealed class SwitchableSource : IGoalSource
    {
        public Exception? Failure { get; set; }

        public int Calls { get; private set; }

        public GoalOffer Goals(GoalContext context)
        {
            Calls++;
            return Failure is { } failure ? throw failure : GoalOffer.Empty;
        }
    }

    private static GoalContext Context()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 26, 17, 0, 0, TimeSpan.Zero));
        var configuration = TestConfigurations.Valid(TestDevices.Document(
            "  - id: ev_charger\n    type: modulating\n    actuator: number.charger_current\n    phase: L1\n    min: 6\n    max: 16\n    step: 1\n    value_per_kwh: 2.03\n    goal:\n" + EnergyGoal)).Configuration!;
        return new GoalContext(time.GetUtcNow(), configuration, GoalInputs.Empty, Warsaw, new GoalLedger(NullLogger<GoalLedger>.Instance));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_source_that_throws_offers_nothing_and_the_healthy_source_still_answers(bool throwingFirst)
    {
        // Why: the exception used to travel through the plan view into the guard step. Remove the isolation and this fails with the thrown exception, in either order.
        var broken = new SwitchableSource { Failure = new InvalidOperationException("calendar parse") };
        IGoalSource[] sources = throwingFirst ? [broken, new ConfigGoalSource()] : [new ConfigGoalSource(), broken];
        var logs = new LogCapture();
        var evaluator = new GoalEvaluator(sources, logs.For<GoalEvaluator>());

        var board = evaluator.Evaluate(Context());

        Assert.Equal(1, broken.Calls);
        Assert.Equal("ev_charger", Assert.Single(board.Goals).Request.Device.Value);
    }

    [Fact]
    public void A_failing_source_is_logged_once_per_streak_and_again_after_it_recovered()
    {
        // Why: the guard steps every few seconds; the same failure on every step is log spam. A new streak (after a good answer) is news again.
        var broken = new SwitchableSource { Failure = new InvalidOperationException("first") };
        var logs = new LogCapture();
        var evaluator = new GoalEvaluator([broken, new ConfigGoalSource()], logs.For<GoalEvaluator>());
        var context = Context();

        evaluator.Evaluate(context);
        evaluator.Evaluate(context);
        evaluator.Evaluate(context);
        Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Error, logs.Entries[0].Level);
        Assert.Contains("SwitchableSource", logs.Entries[0].Text, StringComparison.Ordinal);
        Assert.Contains("first", logs.Entries[0].Text, StringComparison.Ordinal);

        broken.Failure = null;
        evaluator.Evaluate(context);
        Assert.Single(logs.Entries);

        broken.Failure = new InvalidOperationException("second");
        evaluator.Evaluate(context);
        evaluator.Evaluate(context);
        Assert.Equal(2, logs.Entries.Count);
        Assert.Contains("second", logs.Entries[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failing_source_without_a_logger_still_offers_nothing()
    {
        // Why: the logger is optional (tests and tools build the evaluator bare); isolation must not depend on it.
        var evaluator = new GoalEvaluator([new SwitchableSource { Failure = new InvalidOperationException("x") }, new ConfigGoalSource()]);

        Assert.Single(evaluator.Evaluate(Context()).Goals);
    }

    [Fact]
    public void A_board_is_degraded_when_a_source_throws_or_reports_a_failed_offer_and_not_otherwise()
    {
        // Why: a failed source leaves the board partial; the tracker reads Degraded to avoid concluding "the goal ended" from an absence it caused.
        var broken = new SwitchableSource { Failure = new InvalidOperationException("x") };
        var evaluator = new GoalEvaluator([broken, new ConfigGoalSource()]);
        Assert.True(evaluator.Evaluate(Context()).Degraded);

        broken.Failure = null;
        Assert.False(evaluator.Evaluate(Context()).Degraded);

        var reporting = new GoalEvaluator([new FailedOfferSource(), new ConfigGoalSource()]);
        var board = reporting.Evaluate(Context());
        Assert.True(board.Degraded);
        Assert.Single(board.Goals);
    }

    [Fact]
    public void A_module_owned_goal_spec_is_not_stated_by_the_config_source()
    {
        // Why: the EV module states its synthetic goal itself; if ConfigGoalSource stated it too, the file's reader and the module would double count.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 26, 17, 0, 0, TimeSpan.Zero));
        var configuration = Context().Configuration;
        var device = configuration.Devices.Single(d => d.Goal is not null);
        var owned = configuration with { Devices = [device with { Goal = device.Goal! with { ModuleOwned = true } }] };
        var context = new GoalContext(time.GetUtcNow(), owned, GoalInputs.Empty, Warsaw, new GoalLedger(NullLogger<GoalLedger>.Instance));

        Assert.Equal(GoalOrigin.Module, owned.Devices.Single().Goal!.Origin);
        Assert.Empty(new ConfigGoalSource().Goals(context).Requests);
        Assert.Empty(new ConfigGoalSource().Goals(context).Inactive);
    }

    private sealed class FailedOfferSource : IGoalSource
    {
        public GoalOffer Goals(GoalContext context) => GoalOffer.FailedOffer;
    }

    [Fact]
    public void Cancellation_is_not_a_failure_and_is_not_swallowed()
    {
        // Why: an OperationCanceledException means the caller is shutting down; hiding it would hide the shutdown.
        var cancelled = new SwitchableSource { Failure = new OperationCanceledException() };
        var logs = new LogCapture();
        var evaluator = new GoalEvaluator([cancelled, new ConfigGoalSource()], logs.For<GoalEvaluator>());

        Assert.Throws<OperationCanceledException>(() => evaluator.Evaluate(Context()));
        Assert.Empty(logs.Entries);
    }
}
