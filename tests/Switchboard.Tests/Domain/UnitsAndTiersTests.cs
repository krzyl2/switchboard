using System.Collections.Immutable;
using System.Reflection;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Tests.Domain;

/// <summary>
/// Current and power are not interchangeable (07-PITFALLS): the unit types make a mix-up a compile
/// error rather than a wrong number.
/// </summary>
public sealed class UnitsTests
{
    private static readonly Type[] UnitTypes = [typeof(Amperes), typeof(Watts), typeof(Volts), typeof(KilowattHours), typeof(Percent)];

    [Fact]
    public void Units_have_no_cross_unit_operators_or_conversions()
    {
        foreach (var unit in UnitTypes)
        {
            var operators = unit.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.IsSpecialName && m.Name.StartsWith("op_", StringComparison.Ordinal));
            foreach (var method in operators)
            {
                Assert.DoesNotContain(method.Name, new[] { "op_Implicit", "op_Explicit" });
                var types = method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType);
                var foreign = types.Where(t => UnitTypes.Contains(t) && t != unit).ToList();
                Assert.True(foreign.Count == 0, $"{unit.Name}.{method.Name} mixes in {string.Join(", ", foreign.Select(t => t.Name))}.");
            }

            var conversions = unit.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => UnitTypes.Contains(m.ReturnType) && m.ReturnType != unit);
            Assert.Empty(conversions);
        }
    }

    [Fact]
    public void Arithmetic_stays_in_the_unit()
    {
        Assert.Equal(new Amperes(7.5), new Amperes(5) + (new Amperes(5) / 2));
        Assert.Equal(new Watts(-200), -new Watts(200));
        Assert.Equal(new Watts(300), Watts.Max(new Watts(300), new Watts(-500)));
        Assert.True(new Amperes(28.25) > new Amperes(25) * 1.13 - new Amperes(0.001));
    }
}

/// <summary>Tier band limits are inclusive (04 §1: "1.25 – 1.45 → 30 s, > 1.45 → shed all").</summary>
public sealed class TierTableTests
{
    private static readonly TierTable Table = new([
        new TierBand(0.90, null, TierReaction.MayAdd),
        new TierBand(1.00, null, TierReaction.Hold),
        new TierBand(1.13, TimeSpan.FromMinutes(15), TierReaction.ReduceOrdinaryCycle),
        new TierBand(1.25, TimeSpan.FromMinutes(3), TierReaction.ReduceNextCycle),
        new TierBand(1.45, TimeSpan.FromSeconds(30), TierReaction.ShedOneImmediately),
        new TierBand(null, TimeSpan.Zero, TierReaction.ShedAllOnPhase),
    ]);

    [Theory]
    [InlineData(1.13, TierReaction.ReduceOrdinaryCycle)]
    [InlineData(1.1300001, TierReaction.ReduceNextCycle)]
    [InlineData(1.45, TierReaction.ShedOneImmediately)]
    [InlineData(1.4500001, TierReaction.ShedAllOnPhase)]
    [InlineData(0.0, TierReaction.MayAdd)]
    [InlineData(9.0, TierReaction.ShedAllOnPhase)]
    public void A_current_exactly_at_a_limit_belongs_to_the_band_below(double multiple, TierReaction expected)
    {
        Assert.Equal(expected, Table.BandFor(multiple).Reaction);
    }
}

/// <summary>
/// SIG-07: the minimum safe reserve follows from how fast the meter refreshes. The derivation lets a
/// kettle land in a band the breaker tolerates for longer than the system needs to react, never in
/// a shed band.
/// </summary>
public sealed class MinimumSafeReserveTests
{
    // The shipped defaults: 2 kW step, 15 s shed latency, 3 σ, no floor, 30 s cycle, two
    // persistence_down boundaries, one poll of current lag.
    private static readonly ReserveDerivationParameters Parameters = new(
        new Watts(2000), TimeSpan.FromSeconds(15), 3, Watts.Zero, TimeSpan.FromSeconds(30), 2, 1);

    private static readonly TierTable Tiers = new([
        new TierBand(0.90, null, TierReaction.MayAdd),
        new TierBand(1.00, null, TierReaction.Hold),
        new TierBand(1.13, TimeSpan.FromMinutes(15), TierReaction.ReduceOrdinaryCycle),
        new TierBand(1.25, TimeSpan.FromMinutes(3), TierReaction.ReduceNextCycle),
        new TierBand(1.45, TimeSpan.FromSeconds(30), TierReaction.ShedOneImmediately),
        new TierBand(null, TimeSpan.Zero, TierReaction.ShedAllOnPhase),
    ]);

    [Fact]
    public void Maintainer_installation_with_30s_refresh_needs_631_W()
    {
        // The lag poll adds a refresh of blind time (2 × 30 s + 15 s = 75 s), but the 3 min band
        // still tolerates it plus the 30 s cycle (105 s), so the reference figure does not change.
        var derivation = Compute(TimeSpan.FromSeconds(30));

        Assert.Equal(631.5, derivation.Minimum.Value, 6);
        Assert.Equal(1.25, derivation.TierMultiple);
        Assert.Equal(TimeSpan.FromSeconds(75), derivation.BlindTime);
        Assert.False(derivation.BelowMinimum);
    }

    [Fact]
    public void Current_lag_poll_counts_one_more_refresh()
    {
        // The M1 current lags its own power by one poll in ~0.4 % of polls: a kettle can go unseen
        // for two refreshes. At 90 s that is 195 s, past the 3 min band even without the cycle.
        var withoutLag = MinimumSafeReserve.Compute(Phase.L1, Profile(), Tiers, 1.0, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(90), new Amperes(0.1), Parameters with { CurrentLagPolls = 0 });
        var withLag = Compute(TimeSpan.FromSeconds(90));

        Assert.Equal((1.25, 631.5), (withoutLag.TierMultiple, Math.Round(withoutLag.Minimum.Value, 6)));
        Assert.Equal(TimeSpan.FromSeconds(195), withLag.BlindTime);
        Assert.Equal((1.13, 1321.5), (withLag.TierMultiple, Math.Round(withLag.Minimum.Value, 6)));
    }

    [Fact]
    public void Undetected_refresh_at_90s_max_age_now_needs_1321_W()
    {
        // Until the refresh is detected the reference installation's max_age (90 s) stands in for it.
        var derivation = MinimumSafeReserve.Compute(Phase.L1, Profile(), Tiers, 1.0, null, TimeSpan.FromSeconds(90), new Amperes(0.1), Parameters);

        Assert.Equal(1.13, derivation.TierMultiple);
        Assert.Equal(1321.5, derivation.Minimum.Value, 6);
    }

    [Fact]
    public void Reduce_ordinary_waits_persistence_down_cycles()
    {
        // The guard acts on a reduce_ordinary_cycle band only after two boundaries. 420 s refresh:
        // 855 s blind + one 30 s cycle fits the 15 min band, + two cycles (915 s) does not.
        var derivation = Compute(TimeSpan.FromSeconds(420));
        var oneCycle = MinimumSafeReserve.Compute(Phase.L1, Profile(), Tiers, 1.0, TimeSpan.FromSeconds(420), TimeSpan.FromSeconds(90), new Amperes(0.1), Parameters with { PersistenceDownCycles = 1 });

        Assert.Equal(1.00, derivation.TierMultiple);
        Assert.Equal(2069, derivation.Minimum.Value, 6);
        Assert.Equal(1.13, oneCycle.TierMultiple);
    }

    [Fact]
    public void Slower_refresh_never_lowers_minimum_reserve()
    {
        var previous = 0.0;
        for (var seconds = 1; seconds <= 1200; seconds += 7)
        {
            var minimum = Compute(TimeSpan.FromSeconds(seconds)).Minimum.Value;
            Assert.True(minimum >= previous, $"{seconds} s refresh gave {minimum} W, below {previous} W for a faster meter.");
            previous = minimum;
        }
    }

    [Fact]
    public void Reduce_bands_include_the_dispatch_cycle()
    {
        // 75 s refresh: 165 s blind (2 × 75 s + 15 s) fits the 3 min band alone, but not with the
        // 30 s cycle the reduce_next_cycle reaction waits for, so the step must fit under 1.13 × In.
        var derivation = Compute(TimeSpan.FromSeconds(75));

        Assert.Equal(TimeSpan.FromSeconds(165), derivation.BlindTime);
        Assert.Equal(1.13, derivation.TierMultiple);
        Assert.Equal(1321.5, derivation.Minimum.Value, 6);
    }

    [Fact]
    public void Fast_meter_never_relies_on_a_shed_band()
    {
        // A 1 s meter tolerates 30 s bands in time, but a kettle landing in "shed one immediately"
        // causes a shed: exactly what the reserve exists to prevent.
        var derivation = Compute(TimeSpan.FromSeconds(1));

        Assert.True(derivation.TierMultiple <= 1.25);
        Assert.True(derivation.Minimum.Value > 0);
    }

    [Fact]
    public void Unknown_characteristic_does_not_rely_on_overload_curve()
    {
        var derivation = Compute(TimeSpan.FromSeconds(30), BreakerCharacteristic.Unknown);

        Assert.Equal(1.00, derivation.TierMultiple);
        Assert.Equal(2069, derivation.Minimum.Value, 6);
    }

    [Fact]
    public void Undetected_refresh_falls_back_to_max_age()
    {
        var derivation = MinimumSafeReserve.Compute(Phase.L1, Profile(), Tiers, 1.0, null, TimeSpan.FromSeconds(150), new Amperes(0.1), Parameters);

        Assert.False(derivation.RefreshDetected);
        Assert.Equal(TimeSpan.FromSeconds(150), derivation.RefreshUsed);
        Assert.Equal(1321.5, derivation.Minimum.Value, 6);
    }

    [Fact]
    public void Reserve_never_below_floor()
    {
        var generous = Parameters with { ReferenceStep = new Watts(100), Floor = new Watts(400) };

        var derivation = MinimumSafeReserve.Compute(Phase.L1, Profile(), Tiers, 1.0, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(90), new Amperes(0), generous);

        Assert.Equal(400, derivation.Minimum.Value);
    }

    [Fact]
    public void Reserve_below_derived_minimum_is_flagged_not_enforced()
    {
        var derivation = MinimumSafeReserve.Compute(Phase.L2, Profile(reserve: 500), Tiers, 1.0, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90), new Amperes(0.1), Parameters);

        Assert.True(derivation.BelowMinimum);
        Assert.Equal(500, derivation.Configured.Value);
    }

    private static ReserveDerivation Compute(TimeSpan refresh, BreakerCharacteristic characteristic = BreakerCharacteristic.B) =>
        MinimumSafeReserve.Compute(Phase.L1, Profile(characteristic), Tiers, 1.0, refresh, TimeSpan.FromSeconds(90), new Amperes(0.1), Parameters);

    private static ConnectionProfile Profile(BreakerCharacteristic characteristic = BreakerCharacteristic.B, double reserve = 2500) =>
        new(
            Topology.ThreePhase,
            new Amperes(25),
            characteristic,
            new Volts(230),
            new[] { Phase.L1, Phase.L2, Phase.L3 }.ToImmutableDictionary(p => p, _ => new Watts(reserve)));
}

/// <summary>SIG-07: refresh detection must not be fooled by repeated values, nor guess from too little.</summary>
public sealed class RefreshIntervalTests
{
    [Fact]
    public void Repeated_values_do_not_double_detected_interval()
    {
        // A 1 s source whose value repeats every other report: HA emits nothing for a repeat, so
        // most intervals are 2 s (as live go-e nrg_4: median 2.006 s, p25 1.004 s).
        var intervals = Enumerable.Range(0, 64).Select(i => TimeSpan.FromSeconds(i % 3 == 0 ? 1 : 2)).ToList();

        Assert.Equal(TimeSpan.FromSeconds(1), RefreshInterval.Estimate(intervals, 0.25, 8));
    }

    [Fact]
    public void Too_few_intervals_report_unknown_not_a_guess()
    {
        var intervals = Enumerable.Repeat(TimeSpan.FromSeconds(10), 7).ToList();

        Assert.Null(RefreshInterval.Estimate(intervals, 0.25, 8));
        Assert.Equal(TimeSpan.FromSeconds(10), RefreshInterval.Estimate([.. intervals, TimeSpan.FromSeconds(10)], 0.25, 8));
    }
}
