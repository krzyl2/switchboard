using Switchboard.Application.Configuration;
using Switchboard.Domain.Connection;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Configuration;

/// <summary>
/// The tier table's safety envelope (DECISIONS, Stage 2; CLAUDE.md "no configuration may raise a
/// load above a phase limit"). control: may tune the table, never move it past the IEC 60898
/// thermal points: above 1.45 × In everything is shed at once, no band above the continuous
/// multiple runs unlimited or adds load, may_add stops at rated current, and nothing claims more
/// continuous current than 1.13 × In (1.00 × In for an unknown characteristic).
/// </summary>
public sealed class EnvelopeTests
{
    private const string Shipped = """
                - { up_to: 0.90, allowed: unlimited, reaction: may_add }
                - { up_to: 1.00, allowed: unlimited, reaction: hold }
                - { up_to: 1.13, allowed: 15m, reaction: reduce_ordinary_cycle }
                - { up_to: 1.25, allowed: 3m, reaction: reduce_next_cycle }
                - { up_to: 1.45, allowed: 30s, reaction: shed_one_immediately }
                - { up_to: null, allowed: 0s, reaction: shed_all_on_phase }
        """;

    [Fact]
    public void Shipped_defaults_satisfy_the_envelope()
    {
        var control = TestConfigurations.Valid(TestConfigurations.ThreePhase()).Configuration!.Control;

        Assert.Equal((1.13, 1.00, 1.00, 1.45), (control.Limits.ContinuousMultipleMax, control.Limits.UnknownContinuousMultipleMax, control.Limits.MayAddUpToMax, control.Limits.OpenBandFromMax));
        Assert.Equal(TimeSpan.FromSeconds(600), control.Limits.BudgetRecoveryTimeMin);
        Assert.True(control.Budget.RecoveryTime >= control.Limits.BudgetRecoveryTimeMin);
        Assert.Equal((0.50, 0.25), (control.Limits.BudgetStopIncreasesBelowMin, control.Limits.BudgetShedOneBelowMin));
        Assert.All(Enum.GetValues<BreakerCharacteristic>(), c => Assert.True(control.Breaker.Tiers.ContainsKey(c)));
        Assert.Equal(ConfigurationStatus.Valid, TestConfigurations.Parse(TestConfigurations.ThreePhase(characteristic: "C")).Status);
        Assert.Equal(ConfigurationStatus.Valid, TestConfigurations.Parse(TestConfigurations.ThreePhase(characteristic: "unknown")).Status);
    }

    [Theory]
    [InlineData("{ up_to: null, allowed: 0s, reaction: shed_all_on_phase }", "{ up_to: null, allowed: 30s, reaction: shed_all_on_phase }")]
    [InlineData("{ up_to: null, allowed: 0s, reaction: shed_all_on_phase }", "{ up_to: null, allowed: 0s, reaction: shed_one_immediately }")]
    public void Open_band_must_shed_everything_at_zero_seconds(string from, string to)
    {
        AssertViolation(Override(Shipped.Replace(from, to)), "control.breaker.tiers.B[5]", to);
    }

    [Fact]
    public void Open_band_must_start_at_or_below_conventional_tripping_current()
    {
        // 1.45 × In is where IEC 60898 guarantees a trip within the conventional time; a band
        // tolerating 2 × In for 30 s keeps load far above it.
        var band = "{ up_to: 2.00, allowed: 30s, reaction: shed_one_immediately }";

        AssertViolation(Override(Shipped.Replace("{ up_to: 1.45, allowed: 30s, reaction: shed_one_immediately }", band)), "control.breaker.tiers.B[4]", band);
    }

    [Fact]
    public void Band_above_continuous_needs_a_finite_duration()
    {
        var bands = """
                    - { up_to: 0.90, allowed: unlimited, reaction: may_add }
                    - { up_to: 1.00, allowed: unlimited, reaction: hold }
                    - { up_to: 1.25, allowed: unlimited, reaction: reduce_next_cycle }
                    - { up_to: 1.45, allowed: 30s, reaction: shed_one_immediately }
                    - { up_to: null, allowed: 0s, reaction: shed_all_on_phase }
            """;

        AssertViolation(Override(bands), "control.breaker.tiers.B[2]", "{ up_to: 1.25, allowed: unlimited");
    }

    [Fact]
    public void Band_above_continuous_may_not_hold()
    {
        // With 1.05 × In continuous, a hold band up to 1.10 × In would sit above the continuous
        // current without ever reducing; a finite duration does not make holding there safe.
        var bands = """
                    - { up_to: 0.90, allowed: unlimited, reaction: may_add }
                    - { up_to: 1.10, allowed: 15m, reaction: hold }
                    - { up_to: 1.25, allowed: 3m, reaction: reduce_next_cycle }
                    - { up_to: 1.45, allowed: 30s, reaction: shed_one_immediately }
                    - { up_to: null, allowed: 0s, reaction: shed_all_on_phase }
            """;

        AssertViolation(Override(bands, continuous: "1.05"), "control.breaker.tiers.B[1]", "{ up_to: 1.10, allowed: 15m, reaction: hold }");
    }

    [Fact]
    public void May_add_is_refused_above_rated_current()
    {
        var bands = """
                    - { up_to: 1.05, allowed: unlimited, reaction: may_add }
                    - { up_to: 1.13, allowed: unlimited, reaction: hold }
                    - { up_to: 1.25, allowed: 3m, reaction: reduce_next_cycle }
                    - { up_to: 1.45, allowed: 30s, reaction: shed_one_immediately }
                    - { up_to: null, allowed: 0s, reaction: shed_all_on_phase }
            """;

        AssertViolation(Override(bands), "control.breaker.tiers.B[0]", "{ up_to: 1.05");
    }

    [Fact]
    public void Continuous_multiple_above_1_13_is_refused()
    {
        var yaml = TestConfigurations.ThreePhase(control: "  breaker:\n    continuous_multiple:\n      B: 1.20\n");

        AssertViolation(yaml, "control.breaker.continuous_multiple.B", "B: 1.20");
    }

    [Fact]
    public void Unknown_cannot_get_credit_above_rated_current()
    {
        // BRK-05: an unknown characteristic forces the continuous threshold to 1.00 × In.
        var yaml = TestConfigurations.ThreePhase(control: "  breaker:\n    continuous_multiple:\n      unknown: 1.05\n");

        AssertViolation(yaml, "control.breaker.continuous_multiple.unknown", "unknown: 1.05");
    }

    [Fact]
    public void Envelope_error_points_at_the_override_line()
    {
        // Lowering B's continuous multiple to 0.95 leaves the shipped hold band (0.90–1.00] above
        // it; the band is the shipped one, so the error names the user's own line.
        var yaml = TestConfigurations.ThreePhase(control: "  breaker:\n    continuous_multiple:\n      B: 0.95\n");

        AssertViolation(yaml, "control.breaker.continuous_multiple.B", "B: 0.95");
    }

    [Fact]
    public void Budget_recovery_faster_than_the_envelope_is_refused()
    {
        // BRK-02: with recovery_time 1 s, one 10 s dip under the continuous current refills the
        // budget, so excursions repeated just under each band's duration would never be shed while
        // the breaker keeps heating. The thermal memory may be made longer, never shorter.
        var yaml = TestConfigurations.ThreePhase(control: "  budget:\n    recovery_time: 1s\n");

        AssertViolation(yaml, "control.budget.recovery_time", "recovery_time: 1s");
        Assert.Equal(ConfigurationStatus.Invalid, TestConfigurations.Parse(TestConfigurations.ThreePhase(control: "  budget:\n    recovery_time: 599s\n")).Status);
        Assert.Equal(ConfigurationStatus.Valid, TestConfigurations.Parse(TestConfigurations.ThreePhase(control: "  budget:\n    recovery_time: 900s\n")).Status);
    }

    [Theory]
    [InlineData("stop_increases_below", "0.49")]
    [InlineData("shed_one_below", "0.10")]
    public void Budget_thresholds_lower_than_shipped_are_refused(string key, string value)
    {
        // BRK-02: shed_one_below 0 would wait for an empty budget before shedding one device, so
        // the breaker heats through the whole margin the threshold exists to keep. Thresholds may
        // react earlier, never later.
        var yaml = TestConfigurations.ThreePhase(control: $"  budget:\n    {key}: {value}\n");

        AssertViolation(yaml, $"control.budget.{key}", $"{key}: {value}");
        Assert.Equal(ConfigurationStatus.Valid, TestConfigurations.Parse(TestConfigurations.ThreePhase(control: "  budget:\n    stop_increases_below: 0.70\n    shed_one_below: 0.40\n")).Status);
    }

    [Fact]
    public void Limits_cannot_be_overridden_from_control()
    {
        var yaml = TestConfigurations.ThreePhase(control: "  limits:\n    continuous_multiple_max: 1.30\n");

        var result = TestConfigurations.Parse(yaml);

        var error = Assert.Single(result.Errors);
        Assert.Equal(("key_not_allowed", "control.limits"), (error.Code, error.Path));
        Assert.Equal(LineOf(yaml, "  limits:"), error.Line);
    }

    [Fact]
    public void Overrides_inside_the_envelope_are_accepted()
    {
        // Tuning is allowed: a shorter 1.13–1.25 band and a lower continuous multiple for C.
        var yaml = TestConfigurations.ThreePhase(control: "  breaker:\n    continuous_multiple:\n      C: 1.10\n    tiers:\n      B:\n" + Shipped.Replace("allowed: 3m", "allowed: 2m").ReplaceLineEndings("\n") + "\n");

        Assert.Equal(ConfigurationStatus.Valid, TestConfigurations.Parse(yaml).Status);
    }

    private static string Override(string bands, string? continuous = null) =>
        TestConfigurations.ThreePhase(control:
            "  breaker:\n"
            + (continuous is null ? string.Empty : $"    continuous_multiple:\n      B: {continuous}\n")
            + "    tiers:\n      B:\n"
            + string.Join('\n', bands.ReplaceLineEndings("\n").Split('\n').Select(l => "        " + l.TrimStart()))
            + "\n");

    private static void AssertViolation(string yaml, string path, string lineText)
    {
        var result = TestConfigurations.Parse(yaml);

        Assert.Equal(ConfigurationStatus.Invalid, result.Status);
        var error = Assert.Single(result.Errors);
        Assert.Equal(("tier_envelope_violated", path), (error.Code, error.Path));
        Assert.Equal(LineOf(yaml, lineText), error.Line);
    }

    private static int LineOf(string yaml, string text) =>
        yaml.Split('\n').Select((line, i) => (line, i)).First(x => x.line.Contains(text, StringComparison.Ordinal)).i + 1;
}
