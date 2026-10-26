using System.Collections.Immutable;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Signals;

namespace Switchboard.Tests.Domain;

/// <summary>
/// The value order of 14-ECONOMICS, pure: tiers of keys that differ by at least a fraction, adopted by the DISP-10 rule. The same mechanism
/// (<see cref="StableAdoption{T}"/>) carries the phase ranking, so the rule is tested once on the shared code and once on each user.
/// </summary>
public sealed class ValueOrderTests
{
    private static readonly DeviceName A = DeviceName.Parse("a");
    private static readonly DeviceName B = DeviceName.Parse("b");
    private static readonly DeviceName C = DeviceName.Parse("c");

    private static Dictionary<DeviceName, double> Keys(double a, double b, double c = 0) => new() { [A] = a, [B] = b, [C] = c };

    private static int[] Tiers(ImmutableDictionary<DeviceName, int> tiers) => [tiers[A], tiers[B], tiers[C]];

    [Fact]
    public void Keys_further_apart_than_the_gap_are_separate_tiers_best_first()
    {
        // Why: the start order gives energy to the best tier first; tier 0 is the most valuable.
        var tiers = ValueOrder.Propose(Keys(1.0, 3.0, 2.0), 0.05);

        Assert.Equal([2, 0, 1], Tiers(tiers));
    }

    [Fact]
    public void Keys_inside_the_gap_of_the_best_key_of_a_tier_share_it()
    {
        // Why: a price tick that moves two keys a little must not reorder them. Measured from the tier's best key (not chained), so
        // 1.00, 0.96 share a tier and 0.92 (8 % below the best) starts the next one: no drift of a tier across ever smaller steps.
        var tiers = ValueOrder.Propose(Keys(1.00, 0.96, 0.92), 0.05);

        Assert.Equal([0, 0, 1], Tiers(tiers));
    }

    [Fact]
    public void Equal_keys_always_share_a_tier_even_with_a_gap_of_zero()
    {
        // Why: with no gap configured the exact keys decide, but two equal keys are one tier, so the ordinary tie-break (reaction time,
        // rotation, name) applies as in 0.9 and the three heaters of a rotation group stay interchangeable.
        var tiers = ValueOrder.Propose(Keys(0.35, 0.35, 0.35), 0);

        Assert.Equal([0, 0, 0], Tiers(tiers));
        Assert.Equal([0, 1, 1], Tiers(ValueOrder.Propose(Keys(0.36, 0.35, 0.35), 0)));
    }

    [Fact]
    public void The_first_proposal_is_adopted_at_once_a_new_one_only_after_the_stability_cycles_and_a_blip_resets_the_count()
    {
        // Why: DISP-10 for values. Nothing to flap from at the first boundary; later, N agreeing boundaries are needed, and a boundary that
        // proposes anything else (even the old order) starts the count again.
        var order = new ValueOrder();
        var first = order.Observe(Keys(1, 2), 0.05, 3);
        Assert.Equal([1, 0, 0], Tiers(first).Take(2).Append(0).ToArray());

        var flipped = Keys(2, 1);
        Assert.Equal(first, order.Observe(flipped, 0.05, 3));
        Assert.Equal(first, order.Observe(flipped, 0.05, 3));
        Assert.Equal(first, order.Observe(Keys(1, 2), 0.05, 3));
        Assert.Equal(first, order.Observe(flipped, 0.05, 3));
        Assert.Equal(first, order.Observe(flipped, 0.05, 3));
        var adopted = order.Observe(flipped, 0.05, 3);

        Assert.NotEqual(first, adopted);
        Assert.Equal((0, 1), (adopted[A], adopted[B]));
    }

    [Fact]
    public void A_proposal_that_differs_each_time_is_never_adopted()
    {
        // Why: "exactly it" means the same proposal N times in a row: three different new orders in three boundaries are noise.
        var order = new ValueOrder();
        var first = order.Observe(Keys(1, 2, 3), 0.05, 3);

        order.Observe(Keys(3, 2, 1), 0.05, 3);
        order.Observe(Keys(2, 3, 1), 0.05, 3);
        var last = order.Observe(Keys(1, 3, 2), 0.05, 3);

        Assert.Equal(first, last);
    }

    [Fact]
    public void A_reset_adopts_the_next_proposal_at_once()
    {
        // Why: a configuration change is the user's decision, not noise.
        var order = new ValueOrder();
        order.Observe(Keys(1, 2), 0.05, 3);

        order.Reset();
        var next = order.Observe(Keys(2, 1), 0.05, 3);

        Assert.Equal((0, 1), (next[A], next[B]));
    }

    [Fact]
    public void The_phase_ranking_keeps_its_rule_on_the_shared_mechanism()
    {
        // Why: PhaseRanking was refactored onto StableAdoption; its three promises (first at once, N agreeing boundaries, reset) must be unchanged.
        var ranking = new PhaseRanking();
        ImmutableArray<Phase> l123 = [Phase.L1, Phase.L2, Phase.L3];
        ImmutableArray<Phase> l321 = [Phase.L3, Phase.L2, Phase.L1];

        Assert.Equal(l123, ranking.Observe(l123, 3));
        Assert.Equal(l123, ranking.Observe(l321, 3));
        Assert.Equal(l123, ranking.Observe(l321, 3));
        Assert.Equal(l321, ranking.Observe(l321, 3));
        ranking.Reset();
        Assert.True(ranking.Adopted.IsEmpty);
        Assert.Equal(l123, ranking.Observe(l123, 3));
    }
}
