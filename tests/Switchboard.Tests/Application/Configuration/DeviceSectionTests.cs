using Switchboard.Application.Configuration;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Configuration;

/// <summary>
/// devices: decides which entity may ever be commanded (SAFE-05) and what protection sheds. It is
/// untrusted input like the rest of the document: a wrong domain, a duplicate actuator or an
/// unreachable range is refused with its path and line, never guessed.
/// </summary>
public sealed class DeviceSectionTests
{
    private static readonly string Heater = """
          - id: heater
            type: onoff
            actuator: switch.heater_1
            phase: L1
            rated_current_a: 6.6
            rated_power_w: 1500
            value_per_kwh: 0.35
        """;

    [Fact]
    public void Reference_devices_parse_into_definitions_and_state_signals()
    {
        var configuration = TestConfigurations.Valid(TestDevices.Document()).Configuration!;

        Assert.Equal(["heater_l1", "heater_l2", "heater_l3", "ev_charger"], configuration.Devices.Select(d => d.Id.Value));
        var charger = configuration.Devices.Single(d => d.Id.Value == "ev_charger");
        Assert.Equal((DeviceType.Modulating, 6.0, 20.0, 1.0), (charger.Type, charger.Range!.Min.Value, charger.Range.Max.Value, charger.Range.Step.Value));
        Assert.Equal(20.0, charger.Stages.Single().RatedCurrent.Value);
        var states = configuration.Signals.Where(s => s.Role == SignalRole.DeviceState).ToList();
        Assert.Equal(["device_heater_l1", "device_heater_l2", "device_heater_l3", "device_ev_charger"], states.Select(s => s.Name.Value));
        Assert.Equal(SignalKind.State, states[0].Kind);
        Assert.Equal((SignalKind.Current, "A"), (states[3].Kind, states[3].Unit));
    }

    [Theory]
    [InlineData("number.heater_1")]
    [InlineData("light.heater_1")]
    [InlineData("binary_sensor.heater_1")]
    public void Relay_actuator_must_be_switch_or_input_boolean(string actuator)
    {
        // A binary_sensor can report a relay's state but can never be commanded.
        AssertDeviceError(Heater.Replace("switch.heater_1", actuator), "actuator_domain_not_allowed", "devices[0].actuator", 3);
    }

    [Fact]
    public void Modulating_actuator_must_be_number()
    {
        var charger = """
              - id: charger
                type: modulating
                actuator: switch.charger
                phase: L1
                min: 6
                max: 16
                step: 1
                value_per_kwh: 2
            """;

        AssertDeviceError(charger, "actuator_domain_not_allowed", "devices[0].actuator", 3);
    }

    [Fact]
    public void Actuator_declared_twice_refused()
    {
        // Two devices commanding one entity would fight; an actuator belongs to exactly one stage.
        var twice = Heater + "\n" + Heater.Replace("id: heater", "id: heater_b").Replace("phase: L1", "phase: L2");

        AssertDeviceError(twice, "duplicate_actuator", "devices[1].actuator", 10);
    }

    [Fact]
    public void State_entity_defaults_to_actuator()
    {
        var configuration = TestConfigurations.Valid(TestDevices.Document(Heater)).Configuration!;

        var stage = configuration.Devices.Single().Stages.Single();
        Assert.Equal(stage.Actuator, stage.StateEntity);
        Assert.Equal(stage.Actuator, configuration.Signals.Single(s => s.Name == stage.StateSignal).Entity);
    }

    [Fact]
    public void State_entity_may_be_a_binary_sensor()
    {
        var configuration = TestConfigurations.Valid(TestDevices.Document(Heater.Replace("    phase: L1", "    state_entity: binary_sensor.heater_1_on\n    phase: L1"))).Configuration!;

        Assert.Equal("binary_sensor.heater_1_on", configuration.Devices.Single().Stages.Single().StateEntity.Value);
    }

    [Fact]
    public void State_entity_mapped_as_signal_refused()
    {
        // One entity feeds one signal: the state would be read twice under two freshness rules.
        var yaml = TestConfigurations.ThreePhase(extra: "  heater_state:\n    entity: switch.heater_1\n    kind: state\n    max_age: 1h\n")
            .TrimEnd('\n') + "\ndevices:\n" + Heater.ReplaceLineEndings("\n");

        var error = Assert.Single(TestConfigurations.Parse(yaml).Errors);
        Assert.Equal(("duplicate_entity", "devices[0].state_entity"), (error.Code, error.Path));
    }

    [Fact]
    public void Phase_outside_topology_refused()
    {
        var single = """
            schema_version: 1
            connection:
              topology: single_phase
              breaker_rating_a: 32
              characteristic: B
              voltage_v: 230
            signals:
              grid_current:
                entity: sensor.meter_current
                kind: current
                unit: A
                sign: positive_import
                max_age: 60s
                role: phase_current
                phase: L1
            devices:
              - id: heater
                type: onoff
                actuator: switch.heater_1
                phase: L2
                rated_current_a: 6.6
                rated_power_w: 1500
                value_per_kwh: 0.35
            """.ReplaceLineEndings("\n");

        var error = Assert.Single(TestConfigurations.Parse(single).Errors);
        Assert.Equal(("phase_not_in_topology", "devices[0].phase", 20), (error.Code, error.Path, error.Line));
        Assert.Equal("phase_not_in_topology", Assert.Single(TestConfigurations.Parse(single.Replace("phase: L2", "phase: three_phase")).Errors).Code);
    }

    [Theory]
    [InlineData("min: 6", "min: 0", "devices[0].min")]
    [InlineData("max: 16", "max: 6", "devices[0].max")]
    [InlineData("max: 16", "max: 26", "devices[0].max")]
    [InlineData("step: 1", "step: 0", "devices[0].step")]
    [InlineData("step: 1", "step: 4", "devices[0].step")]
    public void Modulating_range_reachable_in_steps(string from, string to, string path)
    {
        // max must be reachable in whole steps from min, above zero and within the 25 A breaker.
        var charger = """
              - id: charger
                type: modulating
                actuator: number.charger
                phase: L1
                min: 6
                max: 16
                step: 1
                value_per_kwh: 2
            """;

        Assert.True(TestConfigurations.Parse(TestDevices.Document(charger)).Status == ConfigurationStatus.Valid);
        var error = Assert.Single(TestConfigurations.Parse(TestDevices.Document(charger.Replace(from, to))).Errors);
        Assert.Equal(("modulating_range_invalid", path), (error.Code, error.Path));
    }

    [Fact]
    public void Modulating_range_in_milliamperes_is_converted_by_the_signal_layer()
    {
        var charger = """
              - id: charger
                type: modulating
                actuator: number.charger
                phase: L1
                unit: mA
                min: 6000
                max: 16000
                step: 100
                value_per_kwh: 2
            """;

        var device = TestConfigurations.Valid(TestDevices.Document(charger)).Configuration!.Devices.Single();

        Assert.Equal((6.0, 16.0, 0.1), (device.Range!.Min.Value, device.Range.Max.Value, Math.Round(device.Range.Step.Value, 9)));
        Assert.Equal("mA", device.RangeUnit);
    }

    [Fact]
    public void Later_keys_warn_unknown_keys_fail()
    {
        // goal / sources / guards belong to later versions and warn; circuit_group is read since
        // Stage 3, so naming a group that is not declared is an error.
        var later = TestConfigurations.Parse(TestDevices.Document(Heater + "\n    goal: { kind: none }"));
        Assert.Equal(ConfigurationStatus.Valid, later.Status);
        Assert.Equal(["devices[0].goal"], later.Warnings.Select(w => w.Path));
        Assert.All(later.Warnings, w => Assert.Equal("reserved_key_ignored", w.Code));
        AssertDeviceError(Heater + "\n    circuit_group: boiler", "unknown_circuit_group", "devices[0].circuit_group", 8);

        AssertDeviceError(Heater + "\n    colour: red", "unknown_key", "devices[0].colour", 8);
        AssertDeviceError(Heater + "\n    power_max: 1500", "key_not_allowed", "devices[0].power_max", 8);
    }

    [Fact]
    public void Storage_device_parses_with_a_signed_setpoint_state()
    {
        // DISP-01 storage: a bidirectional current setpoint whose charging is load; its state may be
        // negative (a discharge), its draw signed (positive = charging).
        var configuration = TestConfigurations.Valid(TestDevices.Document(Heater + "\n" + Battery)).Configuration!;

        var battery = configuration.Devices.Single(d => d.Id.Value == "battery");
        Assert.Equal((DeviceType.Storage, -10.0, 16.0), (battery.Type, battery.Range!.Min.Value, battery.Range.Max.Value));
        Assert.False(battery.ToSpec().CanSwitchOff);
        Assert.Equal(0, battery.ToSpec().SetpointFloor.Value);
    }

    [Theory]
    [InlineData("min: -10", "min: 1", "devices[1].min")]
    [InlineData("max: 16", "max: 0", "devices[1].max")]
    [InlineData("min: -10", "min: -2.5", "devices[1].step")]
    public void Storage_range_must_include_zero(string from, string to, string path)
    {
        // Zero charge is where protection brings a storage device: it must be in the range and on its step grid.
        AssertDeviceError(Heater + "\n" + Battery.Replace(from, to), "modulating_range_invalid", path);
    }

    [Fact]
    public void Defaults_come_from_the_defaults_file()
    {
        // Relays take the dispatch relay lockouts (6 min / 90 s), modulating devices their own
        // (60 s / 60 s); reaction times and state max_age come from control.devices.
        var configuration = TestConfigurations.Valid(TestDevices.Document()).Configuration!;
        var heater = configuration.Devices[0];
        var charger = configuration.Devices[3];

        Assert.Equal((TimeSpan.FromMinutes(6), TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(1)), (heater.MinOn, heater.MinOff, heater.ReactionTime));
        Assert.Equal((TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(20)), (charger.MinOn, charger.MinOff, charger.ReactionTime));
        Assert.Equal(TimeSpan.FromMinutes(10), heater.StateMaxAge);
        Assert.Equal(TimeSpan.FromMinutes(10), configuration.Signals.Single(s => s.Name.Value == "device_heater_l1").MaxAge);

        var overridden = TestConfigurations.Valid(TestDevices.Document(control: "  devices:\n    state_max_age: 3m\n")).Configuration!;
        Assert.Equal(TimeSpan.FromMinutes(3), overridden.Devices[0].StateMaxAge);
    }

    [Fact]
    public void Device_signal_prefix_reserved()
    {
        // device_<id> names the state signals created from devices:; a user signal must not take one.
        var yaml = TestConfigurations.ThreePhase(extra: "  device_heater:\n    entity: sensor.other\n    kind: state\n    max_age: 1h\n");

        var error = Assert.Single(TestConfigurations.Parse(yaml).Errors);
        Assert.Equal(("invalid_signal_name", "signals.device_heater"), (error.Code, error.Path));
    }

    [Theory]
    [InlineData("id: heater", "id: Heater", "invalid_device_id", "devices[0].id")]
    [InlineData("id: heater", "id: heater.one", "invalid_device_id", "devices[0].id")]
    [InlineData("type: onoff", "type: relay", "invalid_value", "devices[0].type")]
    [InlineData("rated_current_a: 6.6", "rated_current_a: 30", "out_of_range", "devices[0].rated_current_a")]
    [InlineData("value_per_kwh: 0.35", "value_per_kwh: -1", "out_of_range", "devices[0].value_per_kwh")]
    public void Every_device_value_is_checked(string from, string to, string code, string path)
    {
        AssertDeviceError(Heater.Replace(from, to), code, path);
    }

    [Fact]
    public void Duplicate_device_id_refused()
    {
        var twice = Heater + "\n" + Heater.Replace("switch.heater_1", "switch.heater_2");

        AssertDeviceError(twice, "duplicate_device_id", "devices[1].id", 8);
    }

    [Fact]
    public void Stepped_device_stages_get_one_state_signal_each()
    {
        var boiler = """
              - id: boiler
                type: stepped
                value_per_kwh: 0.3
                stages:
                  - { actuator: switch.boiler_1, phase: L1, rated_current_a: 6.6, rated_power_w: 1500 }
                  - { actuator: switch.boiler_2, phase: three_phase, rated_current_a: 4.4, rated_power_w: 3000 }
            """;

        var configuration = TestConfigurations.Valid(TestDevices.Document(boiler)).Configuration!;

        var device = configuration.Devices.Single();
        Assert.Equal([1, 2], device.Stages.Select(s => s.Index));
        Assert.Equal([Phase.L1, Phase.L2, Phase.L3], device.Stages[1].Phases);
        Assert.Equal(["device_boiler_stage1", "device_boiler_stage2"], configuration.Signals.Where(s => s.Role == SignalRole.DeviceState).Select(s => s.Name.Value));
        var oneStage = boiler.ReplaceLineEndings("\n")[..boiler.ReplaceLineEndings("\n").LastIndexOf('\n')];
        AssertDeviceError(oneStage, "invalid_value", "devices[0].stages");
    }

    [Fact]
    public void State_max_age_is_bounded_by_the_signal_limit()
    {
        // A frozen state counts as fresh for its whole max_age, so it is bounded like any signal.
        AssertDeviceError(Heater + "\n    state_max_age: 25h", "out_of_range", "devices[0].state_max_age", 8);
    }

    private const string Battery = """
          - id: battery
            type: storage
            actuator: number.battery_current
            phase: L1
            min: -10
            max: 16
            step: 1
            value_per_kwh: 1.5
        """;

    private static void AssertDeviceError(string devices, string code, string path, int? line = null)
    {
        var result = TestConfigurations.Parse(TestDevices.Document(devices));

        Assert.Equal(ConfigurationStatus.Invalid, result.Status);
        Assert.Null(result.Configuration);
        var error = Assert.Single(result.Errors, e => e.Path == path);
        Assert.Equal(code, error.Code);
        if (line is { } expected)
        {
            Assert.Equal(expected, error.Line - DevicesLine + 1);
        }
    }

    // The line of "devices:" in TestDevices.Document; device lines are counted from the first item.
    private static int DevicesLine => TestDevices.Document(Heater).Split('\n').ToList().IndexOf("devices:") + 2;
}
