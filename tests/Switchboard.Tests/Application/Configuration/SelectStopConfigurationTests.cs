using Switchboard.Application.Configuration;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Configuration;

/// <summary>
/// The select stop of 0.7.1 (schema version 1, additive keys): a modulating device's stop_actuator
/// may be a select or input_select, and then names the option that stops the device and the one that
/// resumes it. Both are required there and refused anywhere else, read as written (1 and "1" are the
/// same option), different from each other, and plain text; a switch stop keeps working exactly as
/// in 0.7.0. The parser reports every problem at its key; it never throws on user input.
/// </summary>
public sealed class SelectStopConfigurationTests
{
    private const string Charger = """
          - id: charger
            type: modulating
            actuator: number.charger_current
            phase: L1
            min: 6
            max: 16
            step: 1
            value_per_kwh: 2
        """;

    private const string Heater = """
          - id: heater
            type: onoff
            actuator: switch.heater_1
            phase: L1
            rated_current_a: 6.6
            rated_power_w: 1500
            value_per_kwh: 0.35
        """;

    private static string SelectStop(string options = "\n    stop_option: \"1\"\n    resume_option: \"0\"") => Charger + "\n    stop_actuator: select.charger_force" + options;

    [Theory]
    [InlineData("select.charger_force")]
    [InlineData("input_select.charger_mode")]
    public void A_select_or_input_select_is_a_stop_actuator_with_its_two_options(string entity)
    {
        var configuration = Valid(Charger + $"\n    stop_actuator: {entity}\n    stop_option: \"1\"\n    resume_option: \"0\"");

        var charger = configuration.Devices.Single();
        Assert.Equal(new StopOptions(OptionText.Parse("1"), OptionText.Parse("0")), charger.StopOptions);
        Assert.True(charger.ToSpec().CanSwitchOff);
        Assert.Equal(charger.StopOptions, charger.ToSpec().StopOptions);
        Assert.Equal((SignalKind.State, SignalRole.DeviceState), configuration.Signals.Single(s => s.Name.Value == "device_charger_stop") is var stop ? (stop.Kind, stop.Role) : default);
    }

    [Theory]
    [InlineData("\n    resume_option: \"0\"", "devices[0].stop_option")]
    [InlineData("\n    stop_option: \"1\"", "devices[0].resume_option")]
    [InlineData("", "devices[0].stop_option")]
    public void A_select_stop_needs_both_options(string options, string path)
    {
        // A select without its stop option could never be stopped; without the resume option it
        // could never be given back.
        AssertError(SelectStop(options), ConfigurationIssueCodes.MissingKey, path);
    }

    [Fact]
    public void Equal_options_are_refused_at_resume_option()
    {
        AssertError(SelectStop("\n    stop_option: \"1\"\n    resume_option: 1"), ConfigurationIssueCodes.InvalidValue, "devices[0].resume_option");
    }

    [Theory]
    [InlineData("stop_option")]
    [InlineData("resume_option")]
    public void A_switch_stop_refuses_options(string key)
    {
        // A switch stop is turned off to stop and on to resume: an option next to it would be ignored.
        AssertError(Charger + $"\n    stop_actuator: switch.charger_enable\n    {key}: \"1\"", ConfigurationIssueCodes.KeyNotAllowed, $"devices[0].{key}");
    }

    [Fact]
    public void A_switch_stop_keeps_working_as_in_0_7_0()
    {
        var charger = Valid(Charger + "\n    stop_actuator: switch.charger_enable").Devices.Single();

        Assert.Null(charger.StopOptions);
        Assert.True(charger.ToSpec().CanSwitchOff);
    }

    [Theory]
    [InlineData("stop_option")]
    [InlineData("resume_option")]
    public void Options_without_a_stop_actuator_are_refused(string key)
    {
        AssertError(Charger + $"\n    {key}: \"1\"", ConfigurationIssueCodes.KeyNotAllowed, $"devices[0].{key}");
    }

    [Theory]
    [InlineData("1", "0", "1", "0")]
    [InlineData("\"1\"", "\"0\"", "1", "0")]
    [InlineData("1.0", "0", "1.0", "0")]
    [InlineData("don't charge", "neutral", "don't charge", "neutral")]
    public void Unquoted_numbers_are_read_as_option_text(string stop, string resume, string expectedStop, string expectedResume)
    {
        // A select's options are text: YAML's 1 is the option "1", and 1.0 stays "1.0" (as written:
        // never normalized to another option the select might not offer).
        var options = Valid(SelectStop($"\n    stop_option: {stop}\n    resume_option: {resume}")).Devices.Single().StopOptions!;

        Assert.Equal((expectedStop, expectedResume), (options.StopOption.Value, options.ResumeOption.Value));
    }

    [Theory]
    [InlineData("\"\"", ConfigurationIssueCodes.InvalidValue)]
    [InlineData("\"a\\tb\"", ConfigurationIssueCodes.InvalidValue)]
    [InlineData("\"a\\nb\"", ConfigurationIssueCodes.InvalidValue)]
    [InlineData("[1]", ConfigurationIssueCodes.WrongType)]
    [InlineData("{ x: 1 }", ConfigurationIssueCodes.WrongType)]
    [InlineData("~", ConfigurationIssueCodes.WrongType)]
    public void Empty_or_control_character_option_is_refused(string value, string code)
    {
        AssertError(SelectStop($"\n    stop_option: {value}\n    resume_option: \"0\""), code, "devices[0].stop_option");
    }

    [Fact]
    public void An_option_longer_than_the_schema_bound_is_refused()
    {
        AssertError(SelectStop($"\n    stop_option: \"{new string('x', OptionText.MaxLength + 1)}\"\n    resume_option: \"0\""), ConfigurationIssueCodes.InvalidValue, "devices[0].stop_option");
    }

    [Theory]
    [InlineData("stop_option")]
    [InlineData("resume_option")]
    public void Options_on_non_modulating_devices_are_refused(string key)
    {
        AssertError(Heater + $"\n    {key}: \"1\"", ConfigurationIssueCodes.KeyNotAllowed, $"devices[0].{key}");
    }

    [Fact]
    public void A_select_that_is_not_a_stop_domain_is_refused_as_an_actuator_domain()
    {
        AssertError(Charger + "\n    stop_actuator: light.charger\n    stop_option: \"1\"\n    resume_option: \"0\"", ConfigurationIssueCodes.ActuatorDomainNotAllowed, "devices[0].stop_actuator");
    }

    [Fact]
    public void The_example_declares_the_go_e_force_state_select()
    {
        // Maintainer 2026-10-25: the go-e is stopped by its force state "1" (don't charge) and given
        // back with "0" (neutral); no go-e switch stops charging.
        var charger = TestConfigurations.Valid(TestConfigurations.Example).Configuration!.Devices.Single(d => d.Id.Value == "ev_charger");

        Assert.Equal("select.goe_422752_frc", charger.StopActuator!.Value.Value);
        Assert.Equal(("1", "0"), (charger.StopOptions!.StopOption.Value, charger.StopOptions.ResumeOption.Value));
    }

    [Fact]
    public void An_unknown_start_policy_is_reported_never_thrown()
    {
        // Cleanup 0.7.1: the start policy has no C# fallback any more; an unknown one is an issue at
        // its key, and the device is not built (a valid draft always has a policy).
        AssertError(Heater + "\n    start: bogus", ConfigurationIssueCodes.InvalidValue, "devices[0].start");
    }

    [Fact]
    public void An_invalid_control_section_next_to_devices_is_reported_never_thrown()
    {
        // Without control parameters no device draft has a start policy: the devices are still read
        // for their own issues and none is built, so the invalid configuration is reported, not thrown.
        var result = TestConfigurations.Parse(TestDevices.Document(Heater, control: "  dispatch:\n    relay_min_on: soon\n"));

        Assert.Equal(ConfigurationStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Path.StartsWith("control.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_changed_option_changes_the_actuator_revision_and_a_switch_stop_keeps_its_own()
    {
        var one = Valid(SelectStop()).Devices.Single().ActuatorRevision;
        var other = Valid(SelectStop("\n    stop_option: \"2\"\n    resume_option: \"0\"")).Devices.Single().ActuatorRevision;
        var swapped = Valid(SelectStop("\n    stop_option: \"0\"\n    resume_option: \"1\"")).Devices.Single().ActuatorRevision;
        var resumeOnly = Valid(SelectStop("\n    stop_option: \"1\"\n    resume_option: \"2\"")).Devices.Single().ActuatorRevision;

        // The stop option decides what the reported state means (stopped or able to run): a new one
        // starts the device afresh. The resume option is only what a resume sends, resolved from the
        // configuration loaded at send time: a new one keeps the restore baseline, so a device
        // Switchboard holds stopped is still resumed (with the option declared now).
        Assert.NotEqual(one, other);
        Assert.NotEqual(one, swapped);
        Assert.Equal(one, resumeOnly);
    }

    private static SwitchboardConfiguration Valid(string devices) => TestConfigurations.Valid(TestDevices.Document(devices)).Configuration!;

    private static void AssertError(string devices, string code, string path)
    {
        var result = TestConfigurations.Parse(TestDevices.Document(devices));

        Assert.Equal(ConfigurationStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => (e.Code, e.Path) == (code, path));
    }
}
