using Switchboard.Application.Decisions;
using Switchboard.Application.Guard;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Guard;

/// <summary>
/// 04 §2 steps 4 and 5: above In − reserve the guard asks dispatch to reduce, and it publishes a
/// headroom envelope per phase. What it publishes for people (entities, UI) is measured, never
/// hypothetical; what it records does not chatter at a threshold (07-PITFALLS).
/// </summary>
public sealed class GuardEnvelopeTests
{
    private static readonly DateTimeOffset T0 = GuardScenario.Start;

    [Fact]
    public void Reserve_request_after_persistence_down_and_cleared_after_persistence_up()
    {
        // Threshold 25 − 2500/230 = 14.13 A. Two boundaries above ask for a reduction; six below
        // clear it.
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        s.Sample(Phase.L2, 16);
        s.Hold(Phase.L2, 16, TimeSpan.FromSeconds(10), T0 + TimeSpan.FromSeconds(60) - TimeSpan.FromTicks(1));
        Assert.DoesNotContain(s.Events, e => e.Action == DecisionAction.RequestReduction);

        s.RunUntil(T0 + TimeSpan.FromSeconds(60));
        var request = Assert.Single(s.Events, e => e.Action == DecisionAction.RequestReduction);
        Assert.Equal((Phase.L2, ReasonCode.ReserveThreshold), (request.Phase!.Value, request.Reason));
        Assert.True(s.PhaseStatus(Phase.L2).ReductionRequested);

        s.Sample(Phase.L2, 10);
        s.Hold(Phase.L2, 10, TimeSpan.FromSeconds(10), T0 + TimeSpan.FromSeconds(240));
        var cleared = Assert.Single(s.Events, e => e.Action == DecisionAction.ReductionRequestCleared);
        Assert.Equal(ReasonCode.HeadroomAvailable, cleared.Reason);
        Assert.False(s.PhaseStatus(Phase.L2).ReductionRequested);
    }

    [Fact]
    public void Reserve_events_do_not_chatter_at_the_threshold()
    {
        // A current hovering at 14.13 A, above at one boundary and below at the next, for an hour:
        // not a single request is recorded (each side needs consecutive boundaries).
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        for (var k = 0; k < 120; k++)
        {
            s.RunUntil(T0 + TimeSpan.FromSeconds(15 + (30 * k)));
            s.Sample(Phase.L3, k % 2 == 0 ? 14.4 : 13.9);
        }

        s.RunUntil(T0 + TimeSpan.FromHours(1));

        Assert.DoesNotContain(s.Events, e => e.Action is DecisionAction.RequestReduction or DecisionAction.ReductionRequestCleared);
    }

    [Fact]
    public void Shadow_published_headroom_is_measured_not_hypothetical()
    {
        // The heater is held off, but in shadow nothing was applied: the published headroom is from
        // the 30 A really flowing; only the envelope for dispatch counts the heater as gone.
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        s.RunUntil(T0 + TimeSpan.FromSeconds(5));
        s.Sample(Phase.L1, 30);
        s.RunUntil(T0 + TimeSpan.FromSeconds(30));

        var l1 = s.PhaseStatus(Phase.L1);
        Assert.Equal(25 - (2500 / 230.0) - 30, l1.Headroom!.Value.Value, 9);
        Assert.Equal(28.25 - 30, l1.BreakerHeadroom!.Value.Value, 9);
        Assert.Equal(30 - 6.6, l1.EffectiveCurrent!.Value.Value, 9);
        Assert.Equal(25 - (2500 / 230.0) - (30 - 6.6), l1.EnvelopeToReserve!.Value.Value, 9);
    }

    [Fact]
    public void Increases_need_valid_data_may_add_and_headroom()
    {
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 20, 8);

        Assert.True(s.PhaseStatus(Phase.L1).IncreasesAllowed);
        Assert.False(s.PhaseStatus(Phase.L2).IncreasesAllowed);

        s.Invalidate(Phase.L1, ProtectionInvalidReason.CurrentStale);
        Assert.False(s.PhaseStatus(Phase.L1).IncreasesAllowed);
        Assert.True(s.PhaseStatus(Phase.L3).IncreasesAllowed);
    }

    [Fact]
    public void Kill_switch_refuses_every_increase()
    {
        var s = new GuardScenario(TestDevices.Document());
        s.SampleAll(8, 8, 8);
        s.KillSwitch = new KillSwitchStatus(true, s.Now, KillSwitchSource.Api);

        s.Evaluate(GuardTrigger.KillSwitch);

        Assert.All(s.Last.Status.Phases, p => Assert.False(p.IncreasesAllowed));
    }
}
