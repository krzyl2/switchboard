using Switchboard.Application.Configuration;
using Switchboard.Domain.Planning;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Configuration;

/// <summary>
/// PLAN-05: the optional <c>planner:</c> section and the <c>control.planner</c> parameters. The section is data the user types, so a
/// wrong word must be an error and an absent section must be the 0.10 file it always was; the parameters are bound and checked like the
/// other control parameters, with no literal anywhere but the defaults file.
/// </summary>
public sealed class PlannerConfigurationTests
{
    private static readonly string Base = TestConfigurations.ThreePhase();

    private static ConfigurationResult Parse(string planner, string control = "") =>
        TestConfigurations.Parse(TestConfigurations.ThreePhase(control: control) + "\n" + planner);

    [Fact]
    public void A_file_without_a_planner_section_has_no_planner_and_is_a_010_file()
    {
        // Why: no planner: means the planner is off and nothing about the file moves; Planner stays null, not a default section.
        var result = TestConfigurations.Valid(Base);

        Assert.Null(result.Configuration!.Planner);
    }

    [Fact]
    public void An_empty_planner_section_is_the_same_as_none()
    {
        Assert.Null(TestConfigurations.Valid(Base + "\nplanner:\n").Configuration!.Planner);
    }

    [Fact]
    public void A_planner_section_without_dispatch_is_off()
    {
        // Why: the planner's own switch defaults to off (D7): a section that only declares itself must not reach into dispatch.
        var planner = TestConfigurations.Valid(Base + "\nplanner: {}\n").Configuration!.Planner;

        Assert.Equal(PlannerDispatchMode.Off, planner!.Dispatch);
    }

    [Theory]
    [InlineData("off", PlannerDispatchMode.Off)]
    [InlineData("narrow", PlannerDispatchMode.Narrow)]
    [InlineData("full", PlannerDispatchMode.Full)]
    public void Dispatch_reads_its_three_modes(string text, PlannerDispatchMode expected)
    {
        Assert.Equal(expected, TestConfigurations.Valid(Base + $"\nplanner:\n  dispatch: {text}\n").Configuration!.Planner!.Dispatch);
    }

    [Fact]
    public void An_unknown_mode_or_key_is_an_error_not_a_silent_off()
    {
        // Why: a typo in "narrow" that fell back to off would look like a planner that does nothing; naming it is the only honest answer.
        var mode = Parse("planner:\n  dispatch: wide\n");
        var key = Parse("planner:\n  dispach: narrow\n");

        Assert.Equal(ConfigurationStatus.Invalid, mode.Status);
        Assert.Single(mode.Errors, e => e.Code == ConfigurationIssueCodes.InvalidValue && e.Path == "planner.dispatch");
        Assert.Single(key.Errors, e => e.Code == ConfigurationIssueCodes.UnknownKey && e.Path == "planner.dispach");
    }

    [Fact]
    public void The_shipped_planner_parameters_are_the_starting_values_of_15_planner()
    {
        // Why: these are the design's starting values (several with no origin, to review after the shadow week); a silent change of one
        // would change a plan, so the defaults file is pinned to the document that explains them.
        var planner = TestConfigurations.Valid(Base).Configuration!.Control.Planner;

        Assert.Equal((TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(45), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60)), (planner.Cycle, planner.MinReplanInterval, planner.PlanMaxAge, planner.MaxSolveTime, planner.StartDelay));
        Assert.Equal((TimeSpan.FromHours(48), TimeSpan.FromMinutes(60), 0.85, PvShape.HalfSine), (planner.Horizon, planner.Slot, planner.PvDerate, planner.PvShape));
        Assert.Equal((3, 2, 0.05, 0.10), (planner.RefinePasses, planner.AdoptCycles, planner.AdoptMinChangeFraction, planner.ProgressToleranceFraction));
        Assert.Equal(TimeSpan.FromMinutes(30), planner.HardGoal.EscalationMargin);
        Assert.Equal((14.0, 0.8, 400.0), (planner.Load.HalflifeDays, planner.Load.MinObservedFraction, planner.Load.FallbackW));
        Assert.Equal((0.1, 0.10, 0.0, 0.0), (planner.Storage.SocStepKwh, planner.Storage.MinSpread, planner.Storage.DegradationPerKwh, planner.Storage.TerminalValue));
        Assert.Equal((TimeSpan.FromHours(1), 0.0), (planner.Goal.InputMaxAge, planner.Goal.DefaultLossFraction));
    }

    [Fact]
    public void Control_planner_overrides_a_parameter_key_by_key()
    {
        var planner = TestConfigurations.Valid(TestConfigurations.ThreePhase(control: "  planner:\n    slot: 30m\n    pv_derate: 0.7\n    load:\n      fallback_w: 250\n")).Configuration!.Control.Planner;

        Assert.Equal((TimeSpan.FromMinutes(30), 0.7, 250.0), (planner.Slot, planner.PvDerate, planner.Load.FallbackW));
        Assert.Equal(TimeSpan.FromMinutes(15), planner.Cycle);
    }

    [Theory]
    [InlineData("slot: 20m", "control.planner.slot")]
    [InlineData("pv_derate: 1.2", "control.planner.pv_derate")]
    [InlineData("pv_derate: 0", "control.planner.pv_derate")]
    [InlineData("adopt_cycles: 0", "control.planner.adopt_cycles")]
    [InlineData("adopt_min_change_fraction: 2", "control.planner.adopt_min_change_fraction")]
    [InlineData("plan_max_age: 5m", "control.planner.plan_max_age")]
    [InlineData("horizon: 30m", "control.planner.horizon")]
    [InlineData("pv_shape: flat", "control.planner.pv_shape")]
    [InlineData("colour: red", "control.planner.colour")]
    public void A_parameter_out_of_range_is_an_error_naming_the_key(string line, string path)
    {
        // Why: an override must not be able to produce a nonsensical planner (a slot the grid cannot cut, a plan that is stale before the
        // next one exists); the parameters are checked like every other control parameter.
        var result = TestConfigurations.Parse(TestConfigurations.ThreePhase(control: $"  planner:\n    {line}\n"));

        Assert.Equal(ConfigurationStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Path == path);
    }

    [Fact]
    public void A_goal_parameter_out_of_range_is_an_error()
    {
        var result = TestConfigurations.Parse(TestConfigurations.ThreePhase(control: "  planner:\n    goal:\n      default_loss_fraction: 1\n"));

        Assert.Contains(result.Errors, e => e.Path == "control.planner.goal.default_loss_fraction");
    }

    [Fact]
    public void The_planner_section_changes_no_signal_device_or_warning_of_the_file()
    {
        // Why: slice 1 only reads the section; if it moved anything else the file would no longer decide as before.
        var without = TestConfigurations.Valid(TestConfigurations.Example);
        var with = TestConfigurations.Valid(TestConfigurations.Example.TrimEnd() + "\n\nplanner:\n  dispatch: full\n");

        Assert.Equal(without.Configuration!.Signals.Select(s => s.ToString()), with.Configuration!.Signals.Select(s => s.ToString()));
        Assert.Equal(without.Warnings.Select(w => w.ToString()), with.Warnings.Select(w => w.ToString()));
        Assert.Equal(PlannerDispatchMode.Full, with.Configuration.Planner!.Dispatch);
    }
}
