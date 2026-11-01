using System.Collections.Immutable;
using Switchboard.Application.Configuration;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Domain;

/// <summary>
/// What a device's kind decides, pinned before the relay and setpoint kinds became two types
/// (UPGRADE.md "Urządzenie"): the levels a device can be at, the safe level, what a held stage
/// leaves of a level, the current a level allows, how a step walks stage by stage, whether a device
/// can be switched off, and what makes two declarations the same device. The expectations were
/// recorded from the flag-based model (<c>IsSetpoint</c>, <c>CanSwitchOff</c>, <c>Range!</c>) and
/// must not change with the type split.
/// </summary>
public sealed class DeviceModelCharacterizationTests
{
    private static readonly DeviceSpec Heater = Make("heater", DeviceType.OnOff, [Stage(1, 6.6, Phase.L1)]);

    private static readonly DeviceSpec Boiler = Make("boiler", DeviceType.OnOff, [Stage(1, 6.6, Phase.L1, Phase.L2, Phase.L3)]);

    private static readonly DeviceSpec Stepped = Make("stepped", DeviceType.Stepped, [Stage(1, 6.6, Phase.L1), Stage(2, 4.4, Phase.L1, Phase.L2, Phase.L3), Stage(3, 2.2, Phase.L2)]);

    private static readonly DeviceSpec Charger = Make("charger", DeviceType.Modulating, [Stage(1, 16, Phase.L1)], Range(6, 16, 1));

    private static readonly DeviceSpec StoppableCharger = Make("charger", DeviceType.Modulating, [Stage(1, 16, Phase.L1)], Range(6, 16, 1), stop: true);

    private static readonly DeviceSpec Battery = Make("battery", DeviceType.Storage, [Stage(1, 16, Phase.L2)], Range(-10, 16, 2));

    [Fact]
    public void Only_relays_and_a_modulating_device_with_a_stop_actuator_can_be_switched_off()
    {
        // 04 §2: a storage device is brought to zero charge and never switched off, a modulating
        // one only through its stop actuator; the guard holds the others at their minimum instead.
        Assert.True(Heater.CanSwitchOff);
        Assert.True(Stepped.CanSwitchOff);
        Assert.False(Charger.CanSwitchOff);
        Assert.True(StoppableCharger.CanSwitchOff);
        Assert.False(Battery.CanSwitchOff);
        Assert.False(WithStop(Battery).CanSwitchOff);
    }

    [Fact]
    public void A_setpoint_device_steps_from_its_floor_and_a_storage_floor_is_zero_charge()
    {
        Assert.Equal(6, FloorOf(Charger));
        Assert.Equal(0, FloorOf(Battery));
    }

    [Fact]
    public void The_levels_of_a_device_ascend_by_the_current_they_allow()
    {
        Assert.Equal(["0", "1"], Names(Heater));
        Assert.Equal(["000", "100", "110", "111"], Names(Stepped));
        Assert.Equal(["6 A", "7 A", "8 A", "9 A", "10 A", "11 A", "12 A", "13 A", "14 A", "15 A", "16 A"], Names(Charger));
        Assert.Equal(["disabled", "6 A", "7 A", "8 A", "9 A", "10 A", "11 A", "12 A", "13 A", "14 A", "15 A", "16 A"], Names(StoppableCharger));
        Assert.Equal(["0 A", "2 A", "4 A", "6 A", "8 A", "10 A", "12 A", "14 A", "16 A"], Names(Battery));
    }

    [Fact]
    public void The_safe_and_the_maximum_level_of_each_kind()
    {
        Assert.Equal("0", DeviceLevels.Off(Heater).ToString());
        Assert.Equal("000", DeviceLevels.Off(Stepped).ToString());
        Assert.Equal("111", DeviceLevels.Max(Stepped).ToString());
        Assert.Equal("1", DeviceLevels.Max(Heater).ToString());

        // A modulating device without a stop actuator can only come down to its minimum, still enabled.
        Assert.Equal("6 A", DeviceLevels.Off(Charger).ToString());
        Assert.True(DeviceLevels.Off(Charger).Enabled);
        Assert.Equal("disabled", DeviceLevels.Off(StoppableCharger).ToString());
        Assert.False(DeviceLevels.Off(StoppableCharger).Enabled);
        Assert.Equal("0 A", DeviceLevels.Off(Battery).ToString());
        Assert.True(DeviceLevels.Off(Battery).Enabled);
        Assert.Equal("16 A", DeviceLevels.Max(Charger).ToString());
        Assert.Equal("16 A", DeviceLevels.Max(Battery).ToString());
    }

    [Fact]
    public void A_reported_level_needs_every_stage_fresh_and_of_the_right_kind()
    {
        Assert.Equal("101", Reported(Stepped, (1, Relay(true)), (2, Relay(false)), (3, Relay(true)))!.ToString());
        Assert.Null(Reported(Stepped, (1, Relay(true)), (2, Relay(false))));
        Assert.Null(Reported(Stepped, (1, Relay(true)), (2, Relay(false)), (3, Setpoint(5))));
        Assert.Null(Reported(Stepped, (1, Relay(true)), (2, Stale()), (3, Relay(true))));

        Assert.Equal("12 A", Reported(Charger, (1, Setpoint(12)))!.ToString());
        Assert.Null(Reported(Charger, (1, Relay(true))));
        Assert.Null(Reported(Charger, (1, Stale())));
        Assert.Null(Reported(Charger));

        // A stop actuator's flag is part of the level; unknown means the device may be running.
        Assert.Equal("12 A", Reported(StoppableCharger, (1, Setpoint(12)), (DeviceStateSample.StopStage, Relay(true)))!.ToString());
        Assert.Equal("disabled", Reported(StoppableCharger, (1, Setpoint(12)), (DeviceStateSample.StopStage, Relay(false)))!.ToString());
        Assert.Null(Reported(StoppableCharger, (1, Setpoint(12))));
        Assert.Null(Reported(StoppableCharger, (1, Setpoint(12)), (DeviceStateSample.StopStage, Stale())));
        Assert.Equal("-4 A", Reported(Battery, (1, Setpoint(-4)))!.ToString());
    }

    [Fact]
    public void Holds_cap_a_level_stage_by_stage_for_relays_and_by_the_floor_for_a_setpoint()
    {
        var off2 = Limits((2, StageLimit.Off));

        Assert.Equal("101", DeviceLevels.Cap(Stepped, DeviceLevel.Relay(true, true, true), off2).ToString());
        Assert.Equal("111", DeviceLevels.Cap(Stepped, DeviceLevel.Relay(true, true, true), Limits()).ToString());
        Assert.Equal("1", DeviceLevels.Cap(Heater, DeviceLevel.Relay(true), Limits((1, StageLimit.AtMinimum))).ToString());
        Assert.Equal("0", DeviceLevels.Cap(Heater, DeviceLevel.Relay(true), Limits((1, StageLimit.Off))).ToString());

        // A setpoint is capped at its floor; Off disables a device that has a stop actuator and only
        // lowers one that has none; a setpoint already below the floor is left there.
        Assert.Equal("6 A", DeviceLevels.Cap(Charger, DeviceLevel.At(new Amperes(14)), Limits((1, StageLimit.AtMinimum))).ToString());
        Assert.Equal("6 A", DeviceLevels.Cap(Charger, DeviceLevel.At(new Amperes(14)), Limits((1, StageLimit.Off))).ToString());
        Assert.True(DeviceLevels.Cap(Charger, DeviceLevel.At(new Amperes(14)), Limits((1, StageLimit.Off))).Enabled);
        Assert.Equal("disabled", DeviceLevels.Cap(StoppableCharger, DeviceLevel.At(new Amperes(14)), Limits((1, StageLimit.Off))).ToString());
        Assert.Equal("6 A", DeviceLevels.Cap(StoppableCharger, DeviceLevel.At(new Amperes(14)), Limits((1, StageLimit.AtMinimum))).ToString());
        Assert.Equal("disabled", DeviceLevels.Cap(StoppableCharger, DeviceLevel.At(new Amperes(14), enabled: false), Limits((1, StageLimit.AtMinimum))).ToString());
        Assert.Equal("0 A", DeviceLevels.Cap(Battery, DeviceLevel.At(new Amperes(8)), Limits((1, StageLimit.AtMinimum))).ToString());
        Assert.Equal("3 A", DeviceLevels.Cap(Charger, DeviceLevel.At(new Amperes(3)), Limits((1, StageLimit.AtMinimum))).ToString());

        // A hold on another stage, or no hold at all, leaves the level alone.
        Assert.Equal("14 A", DeviceLevels.Cap(Charger, DeviceLevel.At(new Amperes(14)), Limits((2, StageLimit.Off))).ToString());
        Assert.Equal("14 A", DeviceLevels.Cap(Charger, DeviceLevel.At(new Amperes(14)), Limits()).ToString());
    }

    [Fact]
    public void A_level_allows_the_rated_current_of_stages_on_or_its_enabled_setpoint()
    {
        Assert.Equal(6.6, DeviceLevels.Ceiling(Heater, DeviceLevel.Relay(true), Phase.L1).Value, 9);
        Assert.Equal(0, DeviceLevels.Ceiling(Heater, DeviceLevel.Relay(true), Phase.L2).Value, 9);
        Assert.Equal(0, DeviceLevels.Ceiling(Heater, DeviceLevel.Relay(false), Phase.L1).Value, 9);
        Assert.Equal(6.6 + 4.4, DeviceLevels.Ceiling(Stepped, DeviceLevel.Relay(true, true, false), Phase.L1).Value, 9);
        Assert.Equal(4.4 + 2.2, DeviceLevels.Ceiling(Stepped, DeviceLevel.Relay(false, true, true), Phase.L2).Value, 9);

        Assert.Equal(12, DeviceLevels.Ceiling(Charger, DeviceLevel.At(new Amperes(12)), Phase.L1).Value, 9);
        Assert.Equal(0, DeviceLevels.Ceiling(Charger, DeviceLevel.At(new Amperes(12)), Phase.L2).Value, 9);
        Assert.Equal(0, DeviceLevels.Ceiling(Charger, DeviceLevel.At(new Amperes(12), enabled: false), Phase.L1).Value, 9);

        // Charging is the only load a storage device adds: a negative setpoint allows nothing.
        Assert.Equal(0, DeviceLevels.Ceiling(Battery, DeviceLevel.At(new Amperes(-6)), Phase.L2).Value, 9);
        Assert.Equal(6, DeviceLevels.Ceiling(Battery, DeviceLevel.At(new Amperes(6)), Phase.L2).Value, 9);
    }

    [Fact]
    public void A_level_that_is_not_of_the_devices_kind_allows_and_counts_nothing()
    {
        // A level left over from another declaration of the same name (a relay level for a charger
        // and the reverse) says nothing about this device's stages.
        Assert.Equal(0, DeviceLevels.Ceiling(Charger, DeviceLevel.Relay(true), Phase.L1).Value, 9);
        Assert.Equal(0, DeviceLevels.Ceiling(Heater, DeviceLevel.At(new Amperes(10)), Phase.L1).Value, 9);
        Assert.False(DeviceLevels.StageOn(Heater, DeviceLevel.At(new Amperes(10)), 1));
        Assert.False(DeviceLevels.StageOn(Charger, DeviceLevel.Relay(true), 1));
        Assert.True(DeviceLevels.IsOff(Heater, DeviceLevel.At(new Amperes(10))));
        Assert.True(DeviceLevels.IsOff(Charger, DeviceLevel.Relay(true)));
    }

    [Fact]
    public void A_device_is_off_when_it_draws_nothing_anywhere()
    {
        Assert.True(DeviceLevels.IsOff(Stepped, DeviceLevel.Relay(false, false, false)));
        Assert.False(DeviceLevels.IsOff(Stepped, DeviceLevel.Relay(false, false, true)));
        Assert.True(DeviceLevels.IsOff(StoppableCharger, DeviceLevel.At(new Amperes(6), enabled: false)));
        Assert.False(DeviceLevels.IsOff(StoppableCharger, DeviceLevel.At(new Amperes(6))));
        Assert.True(DeviceLevels.IsOff(Battery, DeviceLevel.At(Amperes.Zero)));
        Assert.Equal(6.6 + 4.4 * 3, DeviceLevels.Total(Stepped, DeviceLevel.Relay(true, true, false)), 9);
    }

    [Fact]
    public void Relay_steps_switch_on_from_the_lowest_stage_and_off_from_the_highest_a_setpoint_in_one_command()
    {
        var off = DeviceLevel.Relay(false, false, false);
        var on = DeviceLevel.Relay(true, true, true);
        Assert.Equal("100", DeviceLevels.Toward(Stepped, off, on, 1).ToString());
        Assert.Equal("110", DeviceLevels.Toward(Stepped, off, on, 2).ToString());
        Assert.Equal("111", DeviceLevels.Toward(Stepped, off, on, 3).ToString());
        Assert.Equal("110", DeviceLevels.Toward(Stepped, on, off, 1).ToString());
        Assert.Equal("100", DeviceLevels.Toward(Stepped, on, off, 2).ToString());
        Assert.Equal("111", DeviceLevels.Toward(Stepped, DeviceLevel.Relay(true, false, true), DeviceLevel.Relay(false, true, true), 1).ToString());
        Assert.Equal("100", DeviceLevels.Toward(Stepped, DeviceLevel.Relay(false), on, 1).ToString());

        // One stage and a setpoint go straight to the target.
        Assert.Equal("1", DeviceLevels.Toward(Heater, DeviceLevel.Relay(false), DeviceLevel.Relay(true), 1).ToString());
        Assert.Equal("16 A", DeviceLevels.Toward(Charger, DeviceLevel.At(new Amperes(6)), DeviceLevel.At(new Amperes(16)), 1).ToString());
        Assert.Equal("disabled", DeviceLevels.Toward(StoppableCharger, DeviceLevel.At(new Amperes(6)), DeviceLevel.At(new Amperes(6), enabled: false), 1).ToString());
    }

    [Fact]
    public void Stage_changes_count_flags_for_relays_and_the_stop_flag_for_a_setpoint()
    {
        Assert.Equal(2, DeviceLevels.StageChanges(Stepped, DeviceLevel.Relay(true, false, true), DeviceLevel.Relay(false, false, false)));
        Assert.Equal(0, DeviceLevels.StageChanges(Stepped, DeviceLevel.Relay(true, false, true), DeviceLevel.Relay(true, false, true)));
        Assert.Equal(0, DeviceLevels.StageChanges(Charger, DeviceLevel.At(new Amperes(6)), DeviceLevel.At(new Amperes(16))));
        Assert.Equal(1, DeviceLevels.StageChanges(StoppableCharger, DeviceLevel.At(new Amperes(6)), DeviceLevel.At(new Amperes(6), enabled: false)));
    }

    [Fact]
    public void Levels_compare_by_content_with_a_setpoint_tolerance_and_count_the_stages_on()
    {
        Assert.Equal(DeviceLevel.Relay(true, false), DeviceLevel.Relay(true, false));
        Assert.NotEqual(DeviceLevel.Relay(true, false), DeviceLevel.Relay(false, true));
        Assert.NotEqual(DeviceLevel.Relay(true), DeviceLevel.Relay(true, false));
        Assert.Equal(DeviceLevel.At(new Amperes(10)), DeviceLevel.At(new Amperes(10 + 1e-12)));
        Assert.NotEqual(DeviceLevel.At(new Amperes(10)), DeviceLevel.At(new Amperes(10.001)));
        Assert.NotEqual(DeviceLevel.At(new Amperes(10)), DeviceLevel.At(new Amperes(10), enabled: false));
        Assert.NotEqual(DeviceLevel.At(new Amperes(10)), DeviceLevel.Relay(true));
        Assert.Equal(DeviceLevel.At(new Amperes(10)).GetHashCode(), DeviceLevel.At(new Amperes(10 + 1e-12)).GetHashCode());
        Assert.Equal(DeviceLevel.Relay(true, false).GetHashCode(), DeviceLevel.Relay(true, false).GetHashCode());
        Assert.Equal(2, DeviceLevel.Relay(true, false, true).StagesOn);
        Assert.Equal(0, DeviceLevel.At(new Amperes(10)).StagesOn);
        Assert.True(DeviceLevel.Relay(true).Enabled);
        Assert.False(DeviceLevel.At(new Amperes(10), enabled: false).Enabled);
        Assert.Equal("10.5 A", DeviceLevel.At(new Amperes(10.5)).ToString());
        Assert.Equal("disabled", DeviceLevel.At(new Amperes(10.5), enabled: false).ToString());
    }

    [Fact]
    public void Two_declarations_are_the_same_device_unless_something_a_level_depends_on_changed()
    {
        Assert.True(SpecIdentity.Same(Heater, Heater));
        Assert.False(SpecIdentity.Same(Heater, Heater with { ValuePerKwh = 9 }));
        Assert.True(SpecIdentity.SameLevelSpace(Heater, Heater with { ValuePerKwh = 9 }));
        Assert.True(SpecIdentity.SameLevelSpace(Charger, Charger with { ReactionTime = TimeSpan.FromSeconds(3) }));
        Assert.False(SpecIdentity.SameLevelSpace(Charger, StoppableCharger));
        Assert.False(SpecIdentity.SameLevelSpace(Charger, Make("charger", DeviceType.Modulating, [Stage(1, 16, Phase.L1)], Range(6, 16, 2))));
        Assert.False(SpecIdentity.SameLevelSpace(Heater, Boiler));
        Assert.False(SpecIdentity.SameLevelSpace(Heater, Make("heater", DeviceType.Stepped, [Stage(1, 6.6, Phase.L1)])));
    }

    [Fact]
    public void The_fingerprint_spells_out_the_declaration()
    {
        // The fingerprint decides whether a reload kept a device: its text is part of the contract.
        Assert.Equal("heater|OnOff|0.3||200000000|False|6000000000|6000000000|False||a0|s1:L1:6.6|d", SpecIdentity.Fingerprint(Heater));
        Assert.Equal("charger|Modulating|0.3||200000000|False|6000000000|6000000000|True||a0|s1:L1:16|r6:16:1|d", SpecIdentity.Fingerprint(StoppableCharger));
        Assert.Equal("battery|Storage|0.3||200000000|False|6000000000|6000000000|False||a0|s1:L2:16|r-10:16:2|d", SpecIdentity.Fingerprint(Battery));
    }

    [Fact]
    public void Rated_power_is_the_model_value_of_relay_stages_on_split_over_their_phases_and_none_for_a_setpoint()
    {
        var stepped = new DispatchDevice(Stepped, [new Watts(1500), new Watts(3000), new Watts(600)], null, [], Amperes.Zero, Watts.Zero);
        var charger = new DispatchDevice(Charger, [], null, [], Amperes.Zero, Watts.Zero);

        Assert.Equal(1500 + (3000 / 3.0), stepped.RatedPower(DeviceLevel.Relay(true, true, false), Phase.L1).Value, 9);
        Assert.Equal(3000 / 3.0, stepped.RatedPower(DeviceLevel.Relay(true, true, false), Phase.L2).Value, 9);
        Assert.Equal(600, stepped.RatedPower(DeviceLevel.Relay(false, false, true), Phase.L2).Value, 9);
        Assert.Equal(0, stepped.RatedPower(DeviceLevel.Relay(false, false, false), Phase.L1).Value, 9);
        Assert.Equal(0, stepped.RatedPower(DeviceLevel.Relay(true), Phase.L2).Value, 9);
        Assert.Equal(0, charger.RatedPower(DeviceLevel.At(new Amperes(16)), Phase.L1).Value, 9);
    }

    [Fact]
    public void Every_device_type_parses_into_the_spec_protection_and_dispatch_use()
    {
        const string devices = """
              - id: heater
                type: onoff
                actuator: switch.heater_1
                phase: L1
                rated_current_a: 6.6
                rated_power_w: 1500
                value_per_kwh: 0.35
              - id: boiler
                type: stepped
                value_per_kwh: 0.3
                stages:
                  - { actuator: switch.boiler_1, phase: L1, rated_current_a: 6.6, rated_power_w: 1500 }
                  - { actuator: switch.boiler_2, phase: three_phase, rated_current_a: 4.4, rated_power_w: 3000 }
              - id: charger
                type: modulating
                actuator: number.charger_current
                phase: L1
                min: 6
                max: 16
                step: 1
                value_per_kwh: 2.03
              - id: stopped_charger
                type: modulating
                actuator: number.charger2_current
                phase: L2
                min: 6
                max: 16
                step: 1
                value_per_kwh: 2.03
                stop_actuator: switch.charger2_stop
              - id: battery
                type: storage
                actuator: number.battery_current
                phase: L3
                min: -10
                max: 16
                step: 1
                value_per_kwh: 1.5
            """;

        var specs = TestConfigurations.Valid(TestDevices.Document(devices)).Configuration!.Devices.ToDictionary(d => d.Id.Value, d => d.ToSpec());

        Assert.Equal(
            [
                ("heater", DeviceType.OnOff, 1, true, "0|1"),
                ("boiler", DeviceType.Stepped, 2, true, "00|10|11"),
                ("charger", DeviceType.Modulating, 1, false, "6 A|7 A|8 A|9 A|10 A|11 A|12 A|13 A|14 A|15 A|16 A"),
                ("stopped_charger", DeviceType.Modulating, 1, true, "disabled|6 A|7 A|8 A|9 A|10 A|11 A|12 A|13 A|14 A|15 A|16 A"),
                ("battery", DeviceType.Storage, 1, false, "0 A|1 A|2 A|3 A|4 A|5 A|6 A|7 A|8 A|9 A|10 A|11 A|12 A|13 A|14 A|15 A|16 A"),
            ],
            specs.Select(s => (s.Key, s.Value.Type, s.Value.Stages.Length, s.Value.CanSwitchOff, string.Join('|', DeviceLevels.All(s.Value).Select(l => l.ToString())))));
        Assert.Equal((6.0, 16.0, 1.0), RangeOf(specs["charger"]));
        Assert.Equal((-10.0, 16.0, 1.0), RangeOf(specs["battery"]));
        Assert.Equal(16, specs["charger"].Stages.Single().RatedCurrent.Value);
        Assert.Equal([Phase.L1, Phase.L2, Phase.L3], specs["boiler"].Stages[1].Phases);
        Assert.Equal(0, FloorOf(specs["battery"]));
        Assert.Equal(6, FloorOf(specs["stopped_charger"]));
    }

    private static (double Min, double Max, double Step) RangeOf(DeviceSpec spec) => ((SetpointSpec)spec).Range is var range ? (range.Min.Value, range.Max.Value, range.Step.Value) : default;

    private static double FloorOf(DeviceSpec spec) => ((SetpointSpec)spec).SetpointFloor.Value;

    private static string[] Names(DeviceSpec spec) => [.. DeviceLevels.All(spec).Select(l => l.ToString())];

    private static DeviceLevel? Reported(DeviceSpec spec, params (int Stage, DeviceStateSample Sample)[] samples)
    {
        var byStage = samples.ToDictionary(s => s.Stage, s => s.Sample);
        return DeviceLevels.Reported(spec, stage => byStage.GetValueOrDefault(stage));
    }

    private static DeviceStateSample Relay(bool on) => new(DeviceName.Parse("any"), 0, true, null, new RelayApplied(on), null);

    private static DeviceStateSample Setpoint(double amperes) => new(DeviceName.Parse("any"), 0, true, null, new SetpointApplied(new Amperes(amperes)), null);

    private static DeviceStateSample Stale() => new(DeviceName.Parse("any"), 0, false, DeviceStateInvalidReason.Stale, null, null);

    private static IReadOnlyDictionary<int, StageLimit> Limits(params (int Stage, StageLimit Limit)[] limits) => limits.ToDictionary(l => l.Stage, l => l.Limit);

    private static StageSpec Stage(int index, double rated, params Phase[] phases) => new(index, [.. phases], new Amperes(rated));

    private static ModulatingRange Range(double min, double max, double step) => new(new Amperes(min), new Amperes(max), new Amperes(step));

    private static DeviceSpec WithStop(DeviceSpec spec) => ((SetpointSpec)spec) with { HasStopActuator = true };

    private static DeviceSpec Make(string name, DeviceType type, ImmutableArray<StageSpec> stages, ModulatingRange? range = null, bool stop = false) =>
        range is null
            ? new RelaySpec(DeviceName.Parse(name), type, stages, 0.3, null, TimeSpan.FromSeconds(20), false, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10))
            {
                Start = StartPolicy.RestoreOnly,
            }
            : new SetpointSpec(DeviceName.Parse(name), type, stages, range, 0.3, null, TimeSpan.FromSeconds(20), false, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10))
            {
                Start = StartPolicy.RestoreOnly,
                HasStopActuator = stop,
            };
}
