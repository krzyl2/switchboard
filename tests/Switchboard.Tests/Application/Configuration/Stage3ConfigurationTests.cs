using Switchboard.Application.Configuration;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Configuration;

/// <summary>
/// The keys dispatch adds to schema version 1 (devices: draw, state_reports, stop_actuator,
/// rotation_group, circuit_group, type storage; circuit_groups:; settling / foreign_load defaults).
/// Untrusted input like the rest: every key is checked, every problem has its path.
/// </summary>
public sealed class Stage3ConfigurationTests
{
    private const string Heater = """
          - id: heater
            type: onoff
            actuator: switch.heater_1
            phase: L1
            rated_current_a: 6.6
            rated_power_w: 1500
            value_per_kwh: 0.35
        """;

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

    [Fact]
    public void State_reports_on_change_conflicts_with_state_max_age()
    {
        // An on-change state has no age limit by declaration; an age beside it would be ignored.
        AssertError(Heater + "\n    state_reports: on_change\n    state_max_age: 24h", "key_not_allowed", "devices[0].state_max_age");

        var signal = Valid(Heater + "\n    state_reports: on_change").Signals.Single(s => s.Name.Value == "device_heater");
        Assert.Equal(StateFreshness.OnChange, signal.Freshness);
        Assert.Equal(StateFreshness.Age, Valid(Heater).Signals.Single(s => s.Name.Value == "device_heater").Freshness);
        AssertError(Heater + "\n    state_reports: sometimes", "invalid_value", "devices[0].state_reports");
    }

    [Fact]
    public void Draw_creates_measurement_signals_with_their_own_max_age()
    {
        var configuration = Valid(Charger + "\n    draw: { current: sensor.charger_a, power: sensor.charger_w, power_unit: kW, max_age: 45s }");

        var current = configuration.Signals.Single(s => s.Name.Value == "device_charger_current");
        var power = configuration.Signals.Single(s => s.Name.Value == "device_charger_power");
        Assert.Equal((SignalRole.DeviceCurrent, Phase.L1, "A", TimeSpan.FromSeconds(45), StateFreshness.Age), (current.Role, current.Phase!.Value, current.Unit, current.MaxAge, current.Freshness));
        Assert.Equal((SignalRole.DevicePower, "kW"), (power.Role, power.Unit));
        Assert.Equal([Phase.L1], configuration.Devices.Single().ToSpec().CurrentDrawPhases);
        Assert.Equal(TimeSpan.FromSeconds(90), Valid(Charger + "\n    draw: { power: sensor.charger_w }").Signals.Single(s => s.Name.Value == "device_charger_power").MaxAge);
    }

    [Theory]
    [InlineData("draw: { current: switch.charger }", "sensor_domain_required", "devices[0].draw.current")]
    [InlineData("draw: { max_age: 30s }", "missing_key", "devices[0].draw.current")]
    [InlineData("draw: { current: sensor.x, current_unit: W }", "unit_not_allowed_for_kind", "devices[0].draw.current_unit")]
    [InlineData("draw: { current: sensor.x, max_age: 25h }", "out_of_range", "devices[0].draw.max_age")]
    [InlineData("draw: { current: { L2: sensor.x } }", "phase_not_in_topology", "devices[0].draw.current.L2")]
    [InlineData("draw: { current: sensor.meter_current_a }", "duplicate_entity", "devices[0].draw.current")]
    public void Draw_is_checked_like_any_input(string draw, string code, string path)
    {
        // A draw is a direct device sensor (never an actuator or a helper), on the device's own
        // phases, with a sane unit and max_age, and not an entity another signal already reads.
        AssertError(Charger + "\n    " + draw, code, path);
    }

    [Fact]
    public void Three_phase_device_maps_its_draw_per_phase()
    {
        var boiler = Heater.Replace("phase: L1", "phase: three_phase");
        AssertError(boiler + "\n    draw: { current: sensor.boiler }", "wrong_type", "devices[0].draw.current");

        var configuration = Valid(boiler + "\n    draw: { current: { L1: sensor.b1, L2: sensor.b2, L3: sensor.b3 } }");
        Assert.Equal(["device_heater_current_l1", "device_heater_current_l2", "device_heater_current_l3"], configuration.Signals.Where(s => s.Role == SignalRole.DeviceCurrent).Select(s => s.Name.Value));
        AssertError(boiler + "\n    draw: { current: { L1: sensor.b1 } }", "missing_key", "devices[0].draw.current");
    }

    [Fact]
    public void Stop_actuator_is_validated_like_an_actuator()
    {
        // DISP-01: a stop actuator is a commandable entity, so it gets the actuator rules (domain,
        // declared once) and its own state signal; only a modulating device has one.
        var configuration = Valid(Charger + "\n    stop_actuator: switch.charger_enable");
        Assert.True(configuration.Devices.Single().ToSpec().CanSwitchOff);
        Assert.Equal(SignalKind.State, configuration.Signals.Single(s => s.Name.Value == "device_charger_stop").Kind);

        AssertError(Charger + "\n    stop_actuator: number.charger_enable", "actuator_domain_not_allowed", "devices[0].stop_actuator");
        AssertError(Charger + "\n    stop_actuator: switch.heater_1\n" + Heater, "duplicate_actuator", "devices[1].actuator");
        AssertError(Heater + "\n    stop_actuator: switch.heater_2", "key_not_allowed", "devices[0].stop_actuator");
    }

    [Fact]
    public void Rotation_group_needs_identical_elements_one_per_phase()
    {
        // DISP-10 rotates elements whose order is free: equal ratings and value, distinct phases.
        string Element(string id, string phase, string current = "6.6") =>
            Heater.Replace("id: heater", $"id: {id}").Replace("switch.heater_1", $"switch.{id}").Replace("phase: L1", $"phase: {phase}").Replace("rated_current_a: 6.6", $"rated_current_a: {current}") + "\n    rotation_group: cwu\n";

        Assert.Equal("cwu", Valid(Element("a", "L1") + Element("b", "L2")).Devices[0].RotationGroup!.Value.Value);
        AssertError(Element("a", "L1"), "rotation_group_mismatch", "devices[0].rotation_group");
        AssertError(Element("a", "L1") + Element("b", "L1"), "rotation_group_mismatch", "devices[0].rotation_group");
        AssertError(Element("a", "L1") + Element("b", "L2", "7"), "rotation_group_mismatch", "devices[1].rotation_group");
        AssertError(Element("a", "L1") + Element("b", "L2").Replace("rotation_group: cwu", "rotation_group: cwu\n    shed_priority: 1"), "rotation_group_mismatch", "devices[1].rotation_group");
        AssertError(Charger + "\n    rotation_group: cwu", "key_not_allowed", "devices[0].rotation_group");
    }

    [Fact]
    public void Circuit_group_must_be_declared_and_cover_the_device_phases()
    {
        const string Group = "circuit_groups:\n  - { id: carport, breaker_rating_a: 16, characteristic: B, phases: [L1] }\n";
        var configuration = TestConfigurations.Valid(TestDevices.Document(Charger + "\n    circuit_group: carport", extra: Group)).Configuration!;
        Assert.Equal(["charger"], configuration.Groups.Single().Members.Select(m => m.Value));

        AssertError(Charger + "\n    circuit_group: garage", "unknown_circuit_group", "devices[0].circuit_group", Group);
        AssertError(Charger.Replace("phase: L1", "phase: L2") + "\n    circuit_group: carport", "phase_not_in_topology", "devices[0].circuit_group", Group);
    }

    [Fact]
    public void Circuit_group_sub_meter_needs_its_max_age_and_a_sensor_per_phase()
    {
        const string Base = "circuit_groups:\n  - { id: carport, breaker_rating_a: 16, characteristic: B, phases: [L1, L2]";
        var result = TestConfigurations.Valid(TestDevices.Document(Heater, extra: Base + ", current: { L1: sensor.c1, L2: sensor.c2 }, current_max_age: 60s }\n")).Configuration!;
        Assert.Equal(["device__carport_current_l1", "device__carport_current_l2"], result.Signals.Where(s => s.Role == SignalRole.GroupCurrent).Select(s => s.Name.Value));

        AssertError(Heater, "missing_key", "circuit_groups[0].current_max_age", Base + ", current: { L1: sensor.c1, L2: sensor.c2 } }\n");
        AssertError(Heater, "missing_key", "circuit_groups[0].current", Base + ", current: { L1: sensor.c1 }, current_max_age: 60s }\n");
        AssertError(Heater, "sensor_domain_required", "circuit_groups[0].current.L1", Base + ", current: { L1: switch.c1, L2: sensor.c2 }, current_max_age: 60s }\n");
        AssertError(Heater, "key_not_allowed", "circuit_groups[0].current_max_age", Base + ", current_max_age: 60s }\n");
    }

    [Fact]
    public void Group_rated_above_the_main_breaker_is_accepted_with_a_warning()
    {
        // Real installations do this (a 32 A garage feed behind a 25 A main): both limits are
        // enforced and the main one binds first.
        var result = TestConfigurations.Valid(TestDevices.Document(Heater, extra: "circuit_groups:\n  - { id: garage, breaker_rating_a: 32, characteristic: C, phases: [L1] }\n"));

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(("group_rating_above_main", "circuit_groups[0].breaker_rating_a"), (warning.Code, warning.Path));
    }

    [Fact]
    public void Derived_signal_names_that_collide_are_refused()
    {
        // A stepped "a" derives device_a_stage1; an onoff "a_stage1" derives the same name.
        var stepped = "  - id: a\n    type: stepped\n    value_per_kwh: 0.3\n    stages:\n      - { actuator: switch.a1, phase: L1, rated_current_a: 3, rated_power_w: 690 }\n      - { actuator: switch.a2, phase: L1, rated_current_a: 3, rated_power_w: 690 }\n";
        var clash = Heater.Replace("id: heater", "id: a_stage1").Replace("switch.heater_1", "switch.x");

        AssertError(stepped + clash, "invalid_device_id", "devices[1].id");
    }

    [Fact]
    public void Start_hysteresis_not_above_stop_is_refused()
    {
        // Equal thresholds let a device oscillate on one boundary (07-PITFALLS, chattering).
        var result = TestConfigurations.Parse(TestDevices.Document(Heater, control: "  dispatch:\n    start_hysteresis_w: -200\n"));

        Assert.Contains(result.Errors, e => (e.Code, e.Path) == ("invalid_value", "control.dispatch.start_hysteresis_w"));
    }

    [Fact]
    public void Dispatch_setup_converts_hysteresis_once_at_the_nominal_voltage()
    {
        // 300 W and −200 W at 230 V: 1.30 A and −0.87 A, converted in the signal layer.
        var setup = DispatchSetupFactory.Build(Valid(Heater));

        Assert.Equal(300 / 230.0, setup.StartHysteresis.Value, 9);
        Assert.Equal(-200 / 230.0, setup.StopHysteresis.Value, 9);
        Assert.Equal((TimeSpan.FromSeconds(90), 0.5, 3, TimeSpan.FromMinutes(30)), (setup.Settling.HardTimeout, setup.Settling.VerificationTolerance, setup.Settling.MismatchLimit, setup.Settling.UnresponsiveRetryAfter));
        Assert.Equal((TimeSpan.FromMinutes(10), 1000.0, 0.5), (setup.ForeignLoad.BaselineTimeConstant, setup.ForeignLoad.StepThreshold.Value, setup.ForeignLoad.RearmFraction));
        Assert.Equal(1500, setup.Devices.Single().StagePower[0].Value);
    }

    [Fact]
    public void Unresponsive_retry_shorter_than_hard_timeout_is_refused()
    {
        var result = TestConfigurations.Parse(TestDevices.Document(Heater, control: "  settling:\n    unresponsive_retry_after: 60s\n"));

        Assert.Contains(result.Errors, e => (e.Code, e.Path) == ("out_of_range", "control.settling.unresponsive_retry_after"));
    }

    private static SwitchboardConfiguration Valid(string devices) => TestConfigurations.Valid(TestDevices.Document(devices)).Configuration!;

    private static void AssertError(string devices, string code, string path, string extra = "")
    {
        var result = TestConfigurations.Parse(TestDevices.Document(devices, extra: extra));

        Assert.Equal(ConfigurationStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => (e.Code, e.Path) == (code, path));
    }
}
