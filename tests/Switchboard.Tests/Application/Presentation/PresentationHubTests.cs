using Microsoft.Extensions.Logging;
using Switchboard.Application.Presentation;
using Switchboard.Application.State;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Presentation;

/// <summary>
/// The hub runs on the guard's thread and isolates its own failures. An isolated failure must still
/// leave the dashboard honest: the power picture of each step is published before the statistics
/// are fed, so a broken accumulator cannot freeze it on an old step (a frozen UI must be
/// distinguishable from a calm system).
/// </summary>
public sealed class PresentationHubTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(10);

    [Fact]
    public void A_failing_accumulator_does_not_freeze_the_power_picture()
    {
        // A restored voltage counter one second short of TimeSpan's end: the first voltage-observed
        // interval overflows inside the accumulator on every step from then on.
        using var log = new LogCapture();
        var hub = TestStatistics.Hub(logger: log.For<PresentationHub>());
        var start = TestSteps.Start;
        var counters = new LifetimeCounters(start, 0, 0, 0, 0, TimeSpan.MaxValue - TimeSpan.FromSeconds(1));
        var state = new StatisticsState(start, TestStatistics.Options().BucketLength, null, [], counters);
        hub.Restore(new PresentationLoad<StatisticsState>(state, StateFileStatus.Loaded, null), new PresentationLoad<ExcursionState>(null, StateFileStatus.Fresh, null), start);
        var volts = new Dictionary<Phase, double?> { [Phase.L1] = 230, [Phase.L2] = 230, [Phase.L3] = 230 };

        var at = start;
        for (var i = 0; i < 4; i++)
        {
            hub.Observe(TestSteps.Step(at, voltages: volts));
            Assert.Equal(at, hub.Power!.EvaluatedAt);
            Assert.Equal(at, hub.LastStep!.Evaluation.At);
            at += Poll;
        }

        Assert.Single(log.Entries, e => e.Level == LogLevel.Error);
    }
}
