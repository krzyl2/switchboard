using Switchboard.Domain.Connection;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Units;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Domain;

/// <summary>
/// The breaker thermal model of 04 §1 (BRK-01, BRK-02, BRK-05). A miniature breaker is a thermal
/// device: currents up to 1.13 × In are carried indefinitely, anything above is a debt repaid while
/// the current is back at or below it, and repeated short excursions add up.
/// </summary>
public sealed class BreakerModelTests
{
    private static readonly TimeSpan Recovery = TimeSpan.FromSeconds(600);

    private static readonly TierTable Tiers = new([
        new TierBand(0.90, null, TierReaction.MayAdd),
        new TierBand(1.00, null, TierReaction.Hold),
        new TierBand(1.13, TimeSpan.FromMinutes(15), TierReaction.ReduceOrdinaryCycle),
        new TierBand(1.25, TimeSpan.FromMinutes(3), TierReaction.ReduceNextCycle),
        new TierBand(1.45, TimeSpan.FromSeconds(30), TierReaction.ShedOneImmediately),
        new TierBand(null, TimeSpan.Zero, TierReaction.ShedAllOnPhase),
    ]);

    private static readonly BreakerProfile Breaker = new(new Amperes(25), BreakerCharacteristic.B, 1.13, Tiers);

    [Theory]
    [InlineData(22.50, 0)]
    [InlineData(22.51, 1)]
    [InlineData(25.00, 1)]
    [InlineData(25.01, 2)]
    [InlineData(28.25, 2)]
    [InlineData(28.26, 3)]
    [InlineData(31.25, 3)]
    [InlineData(31.26, 4)]
    [InlineData(36.25, 4)]
    [InlineData(36.26, 5)]
    [InlineData(-28.25, 2)]
    public void Band_limits_are_inclusive_at_exactly_up_to(double amps, int band)
    {
        // Compared as multiples of In (28.25 A / 25 A is exactly the double 1.13), never as
        // up_to × In amperes, so a current exactly at a limit never rounds into the band above.
        // Export heats the breaker like import: the magnitude decides.
        Assert.Equal(band, Breaker.BandIndex(Breaker.MultipleOf(new Amperes(amps))));
    }

    [Fact]
    public void Unknown_characteristic_continuous_threshold_is_rated_current()
    {
        var setup = new GuardScenario(TestDevices.Document(characteristic: "unknown")).Setup;

        Assert.Equal(1.00, setup.Breaker.ContinuousMultiple);
        Assert.Equal(25, setup.Breaker.ContinuousCurrent.Value);
        Assert.True(setup.Breaker.IsAboveContinuous(25.01 / 25));
    }

    [Fact]
    public void Budget_consumed_at_dt_over_allowed_above_continuous()
    {
        // 1.20 × In lies in the 3 min band: 90 s cost half the budget; 1.40 × In (30 s band) costs
        // the same in 15 s. 1.10 × In is above In but below the continuous multiple: no cost.
        Assert.Equal(0.5, OverloadBudget.Advance(1, 1.20, Breaker, TimeSpan.FromSeconds(90), Recovery), 12);
        Assert.Equal(0.5, OverloadBudget.Advance(1, 1.40, Breaker, TimeSpan.FromSeconds(15), Recovery), 12);
        Assert.Equal(1.0, OverloadBudget.Advance(1, 1.10, Breaker, TimeSpan.FromSeconds(90), Recovery), 12);
    }

    [Fact]
    public void Budget_recovers_at_dt_over_recovery_time_at_or_below_continuous()
    {
        Assert.Equal(0.3, OverloadBudget.Advance(0.2, 0.5, Breaker, TimeSpan.FromSeconds(60), Recovery), 12);
        Assert.Equal(1.0, OverloadBudget.Advance(0.5, 1.0, Breaker, TimeSpan.FromSeconds(300), Recovery), 12);
    }

    [Fact]
    public void Current_exactly_at_continuous_recovers()
    {
        // 04 §1: "while I ≤ I_continuous: budget += dt / recovery_time".
        Assert.Equal(0.6, OverloadBudget.Advance(0.5, 1.13, Breaker, TimeSpan.FromSeconds(60), Recovery), 12);
        Assert.False(Breaker.IsAboveContinuous(28.25 / 25));
    }

    [Fact]
    public void Budget_clamped_to_zero_and_one()
    {
        Assert.Equal(0, OverloadBudget.Advance(0.1, 1.40, Breaker, TimeSpan.FromSeconds(30), Recovery));
        Assert.Equal(1, OverloadBudget.Advance(0.9, 0.2, Breaker, TimeSpan.FromHours(1), Recovery));
    }

    [Fact]
    public void Zero_allowed_band_empties_budget_instantly()
    {
        // Above 1.45 × In the band allows 0 s: one tick there is the top tier, never dt / 0.
        Assert.Equal(0, OverloadBudget.Advance(1, 1.50, Breaker, TimeSpan.FromTicks(1), Recovery));
        Assert.Equal(TimeSpan.FromTicks(1), OverloadBudget.TimeUntilAtOrBelow(1, 0, 1.50, Breaker, Recovery));
    }

    [Fact]
    public void Three_30s_excursions_in_15_min_cost_more_than_one()
    {
        // 04 §1: a bimetal cools slowly. Three 20 s excursions at 1.35 × In (30 s band), 5 min
        // apart, exhaust the budget although each alone leaves a third of it.
        var budget = 1.0;
        for (var i = 0; i < 3; i++)
        {
            budget = OverloadBudget.Advance(budget, 1.35, Breaker, TimeSpan.FromSeconds(20), Recovery);
            if (i < 2)
            {
                budget = OverloadBudget.Advance(budget, 0.4, Breaker, TimeSpan.FromMinutes(5), Recovery);
            }
        }

        var single = OverloadBudget.Advance(1, 1.35, Breaker, TimeSpan.FromSeconds(20), Recovery);
        Assert.Equal(1.0 / 3, single, 12);
        Assert.True(OverloadBudget.IsAtOrBelow(budget, 0), $"Three excursions left {budget}.");
    }

    [Fact]
    public void Time_until_threshold_is_exact()
    {
        // 1.32 × In (30 s band) from a full budget: below 0.25 one tick after 22.5 s, empty at 30 s.
        var below = OverloadBudget.TimeUntilBelow(1, 0.25, 1.32, Breaker, Recovery)!.Value;
        var empty = OverloadBudget.TimeUntilAtOrBelow(1, 0, 1.32, Breaker, Recovery)!.Value;

        Assert.Equal(TimeSpan.FromSeconds(22.5) + TimeSpan.FromTicks(1), below);
        Assert.False(OverloadBudget.IsBelow(OverloadBudget.Advance(1, 1.32, Breaker, below - TimeSpan.FromTicks(1), Recovery), 0.25));
        Assert.True(OverloadBudget.IsBelow(OverloadBudget.Advance(1, 1.32, Breaker, below, Recovery), 0.25));
        Assert.Equal(TimeSpan.FromSeconds(30), empty);
        Assert.False(OverloadBudget.IsAtOrBelow(OverloadBudget.Advance(1, 1.32, Breaker, empty - TimeSpan.FromTicks(1), Recovery), 0));

        // Recovery the other way: from 0.4 at 0.5 × In, back at 0.5 after exactly 60 s.
        Assert.Equal(TimeSpan.FromSeconds(60), OverloadBudget.TimeUntilNotBelow(0.4, 0.5, 0.5, Breaker, Recovery));
        Assert.Null(OverloadBudget.TimeUntilBelow(1, 0.25, 1.0, Breaker, Recovery));
    }

    [Fact]
    public void Recovery_remaining_is_the_missing_budget_times_recovery_time()
    {
        // BRK-04: the entity shows how long a full refill takes from now.
        Assert.Equal(TimeSpan.FromSeconds(150), OverloadBudget.RecoveryRemaining(0.75, Recovery));
        Assert.Equal(TimeSpan.Zero, OverloadBudget.RecoveryRemaining(1, Recovery));
    }

    [Fact]
    public void Profile_refuses_an_unlimited_band_above_continuous()
    {
        // The budget divides by the band's duration above continuous; unlimited there would mean
        // an overload that never costs anything.
        var unsafeTiers = new TierTable([new TierBand(1.20, null, TierReaction.Hold), new TierBand(null, TimeSpan.Zero, TierReaction.ShedAllOnPhase)]);

        Assert.Throws<ArgumentException>(() => new BreakerProfile(new Amperes(25), BreakerCharacteristic.B, 1.13, unsafeTiers));
    }
}
