using System.Collections.Immutable;
using Switchboard.Application.Configuration;
using Switchboard.Application.Setup;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Setup;

/// <summary>
/// CONN-04 and SIG-07: the wizard explains the reserve with the user's own numbers, using the
/// runtime's own model (the tier table and MinimumSafeReserve.Compute with the document's control
/// parameters). A kettle (reference_step_w) on a phase filled to the reserve threshold lands at
/// In + (step − R)/V; the band it lands in decides what the user is told. The minimum safe reserve is
/// derived from the detected refresh and shown, never enforced.
/// </summary>
public sealed class ReserveExplanationTests
{
    private static readonly ControlParameters Control = SwitchboardDocumentParser.BindControl(null, TestConfigurations.Defaults)!;

    [Fact]
    public void Kettle_arithmetic_for_3x25A_with_2500W_reserve_stays_under_the_rating()
    {
        // 25 + (2000 − 2500)/230 = 22.83 A = 0.913 × In: the hold band, nothing to do. (The 27.8 A of
        // 04 §1 and the mockup does not follow from the model; reported to the orchestrator.)
        var explanation = ReserveExplanation.Arithmetic(Profile(2500), Control, 2500);

        Assert.Equal(5750, explanation.PhaseW, 6);
        Assert.Equal(3250, explanation.AllocatableW, 6);
        Assert.Equal(28.25, explanation.ContinuousA, 6);
        Assert.Equal(22.83, explanation.WorstCaseA, 2);
        Assert.Equal(0.913, explanation.Multiple, 3);
        Assert.Equal((TierReaction.Hold, (double?)null), (explanation.Reaction, explanation.AllowedS));
    }

    [Fact]
    public void Kettle_at_the_minimum_reserve_lands_in_the_three_minute_band()
    {
        // R = R_min = 631.5 W: 25 + 1368.5/230 = 30.95 A = 1.238 × In: tolerated for 3 min, a
        // reduction at the next cycle. The reserve may be smaller than the largest appliance.
        var explanation = ReserveExplanation.Arithmetic(Profile(631.5), Control, 631.5);

        Assert.Equal(30.95, explanation.WorstCaseA, 2);
        Assert.Equal(1.238, explanation.Multiple, 3);
        Assert.Equal((TierReaction.ReduceNextCycle, (double?)180), (explanation.Reaction, explanation.AllowedS));
    }

    [Fact]
    public void Kettle_with_no_reserve_is_a_shed()
    {
        // A reserve too small for one kettle: the copy must say a kettle would cause a shed.
        var explanation = ReserveExplanation.Arithmetic(Profile(0), Control, 0);

        Assert.Equal(TierReaction.ShedOneImmediately, explanation.Reaction);
    }

    [Fact]
    public void Minimum_safe_reserve_from_10s_refresh_is_632W()
    {
        var report = ReserveExplanation.Compute(Profile(2500), Control, Refresh(TimeSpan.FromSeconds(10)))!;

        Assert.All(report.PerPhase, p => Assert.Equal(631.5, p.MinimumSafeW, 6));
        Assert.All(report.PerPhase, p => Assert.True(p.RefreshDetected));
        Assert.All(report.PerPhase, p => Assert.False(p.BelowMinimum));
    }

    [Fact]
    public void Undetected_refresh_uses_max_age_as_the_worst_case()
    {
        var report = ReserveExplanation.Compute(Profile(1000), Control, Refresh(null))!;

        Assert.All(report.PerPhase, p => Assert.Equal((1321.5, 90.0, false), (Math.Round(p.MinimumSafeW, 6), p.RefreshS, p.RefreshDetected)));
        Assert.All(report.PerPhase, p => Assert.True(p.BelowMinimum));
    }

    [Fact]
    public void Explanation_follows_control_overrides_in_the_file()
    {
        // A document whose control: section lowers reference_step_w changes the explanation too.
        var root = (ConfigMapping)Switchboard.Infrastructure.Configuration.YamlDocumentReader.Read(
            "control:\n  reserve_derivation:\n    reference_step_w: 3000\n",
            ConfigOrigin.Document).Root!;
        var control = SwitchboardDocumentParser.BindControl(root, TestConfigurations.Defaults)!;

        var explanation = ReserveExplanation.Arithmetic(Profile(2500), control, 2500);

        Assert.Equal(3000, explanation.ReferenceStepW);
        Assert.Equal(25 + (500.0 / 230), explanation.WorstCaseA, 6);
    }

    private static ConnectionProfile Profile(double reserve) => new(
        Topology.ThreePhase,
        new Amperes(25),
        BreakerCharacteristic.B,
        new Volts(230),
        ImmutableDictionary<Phase, Watts>.Empty.Add(Phase.L1, new Watts(reserve)).Add(Phase.L2, new Watts(reserve)).Add(Phase.L3, new Watts(reserve)));

    private static Dictionary<Phase, PhaseRefresh> Refresh(TimeSpan? detected) =>
        new[] { Phase.L1, Phase.L2, Phase.L3 }.ToDictionary(p => p, _ => new PhaseRefresh(detected, TimeSpan.FromSeconds(90), 0.1));
}
