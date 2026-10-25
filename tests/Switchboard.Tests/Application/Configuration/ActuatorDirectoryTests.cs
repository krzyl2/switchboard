using Switchboard.Application.Configuration;
using Switchboard.Application.Effector;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Units;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Configuration;

/// <summary>
/// SAFE-05 at send time: a command becomes a service call only through the actuators the
/// configuration loaded now declares, and the serialized call is checked against them a second time
/// (the entity, its domain, the service, the value within the declared range, on the step grid, in
/// the actuator's own unit). During an invalid configuration only decreases reach the last valid
/// declarations; a device a valid reload removed gets exactly one final off per actuator.
/// </summary>
public sealed class ActuatorDirectoryTests
{
    private const string Devices = """
          - id: heater_l1
            type: onoff
            actuator: switch.heater_1
            phase: L1
            rated_current_a: 6.6
            rated_power_w: 1500
            value_per_kwh: 0.35
          - id: ev_charger
            type: modulating
            actuator: number.charger_current
            stop_actuator: switch.charger_enable
            phase: L1
            min: 6
            max: 20
            step: 1
            value_per_kwh: 2.03
          - id: battery
            type: storage
            actuator: input_number.battery_current
            phase: L2
            min: -10
            max: 16
            step: 1
            value_per_kwh: 1.5
          - id: pump
            type: modulating
            actuator: number.pump_current
            phase: L3
            unit: mA
            min: 6000
            max: 16000
            step: 500
            value_per_kwh: 0.5
        """;

    private static readonly SwitchboardConfiguration Configuration = TestConfigurations.Valid(TestDevices.Document(Devices)).Configuration!;
    private static readonly ActuatorDirectory Directory = ActuatorDirectory.From(Configuration);

    [Theory]
    [InlineData("switch.heater_1", "switch", ActuatorService.TurnOn, null, true)]
    [InlineData("switch.heater_1", "switch", ActuatorService.TurnOff, null, true)]
    [InlineData("switch.heater_1", "switch", ActuatorService.SetValue, 1.0, false)]
    [InlineData("switch.heater_1", "input_boolean", ActuatorService.TurnOff, null, false)]
    [InlineData("switch.heater_2", "switch", ActuatorService.TurnOff, null, false)]
    [InlineData("switch.charger_enable", "switch", ActuatorService.TurnOff, null, true)]
    [InlineData("switch.charger_enable", "switch", ActuatorService.TurnOn, null, true)]
    [InlineData("number.charger_current", "number", ActuatorService.SetValue, 16.0, true)]
    [InlineData("number.charger_current", "number", ActuatorService.SetValue, 20.0, true)]
    [InlineData("number.charger_current", "number", ActuatorService.SetValue, 6.0, true)]
    [InlineData("number.charger_current", "number", ActuatorService.SetValue, 21.0, false)]
    [InlineData("number.charger_current", "number", ActuatorService.SetValue, 5.0, false)]
    [InlineData("number.charger_current", "number", ActuatorService.SetValue, 12.5, false)]
    [InlineData("number.charger_current", "number", ActuatorService.TurnOn, null, false)]
    [InlineData("number.charger_current", "number", ActuatorService.SetValue, null, false)]
    [InlineData("input_number.battery_current", "input_number", ActuatorService.SetValue, 0.0, true)]
    [InlineData("input_number.battery_current", "input_number", ActuatorService.SetValue, 16.0, true)]
    [InlineData("input_number.battery_current", "input_number", ActuatorService.SetValue, -1.0, false)]
    [InlineData("input_number.battery_current", "input_number", ActuatorService.SetValue, -10.0, false)]
    [InlineData("number.pump_current", "number", ActuatorService.SetValue, 16000.0, true)]
    [InlineData("number.pump_current", "number", ActuatorService.SetValue, 6500.0, true)]
    [InlineData("number.pump_current", "number", ActuatorService.SetValue, 16.0, false)]
    [InlineData("number.pump_current", "number", ActuatorService.SetValue, 6250.0, false)]
    public void Only_a_declared_actuator_with_its_own_service_and_a_value_in_range_on_its_grid_is_declared(string entity, string domain, ActuatorService service, double? value, bool declared)
    {
        // A relay takes on and off and never a value; a setpoint takes set_value within [floor, max]
        // in its own unit (mA for the pump: 16 would be 16 mA, below its floor); storage is never
        // commanded into discharge (floor 0) and nothing else is ever accepted.
        Assert.Equal(declared, Directory.IsDeclared(EntityId.Parse(entity), domain, service, value, decreasesOnly: false));
    }

    [Fact]
    public void A_relay_command_resolves_to_its_own_entity_and_service()
    {
        var call = Directory.Resolve(Relay("heater_l1", on: false), decreasesOnly: false).Call;

        Assert.Equal(new ActuatorCall(EntityId.Parse("switch.heater_1"), "switch", ActuatorService.TurnOff, null), call);
    }

    [Fact]
    public void A_setpoint_is_sent_in_the_actuators_unit_on_its_grid_and_within_its_range()
    {
        Assert.Equal(16000, Directory.Resolve(Setpoint("pump", 16), decreasesOnly: false).Call!.Value);
        Assert.Equal(12, Directory.Resolve(Setpoint("ev_charger", 12.7), decreasesOnly: false).Call!.Value);
        Assert.Equal(20, Directory.Resolve(Setpoint("ev_charger", 25), decreasesOnly: false).Call!.Value);
        Assert.Equal(6, Directory.Resolve(Setpoint("ev_charger", 2), decreasesOnly: false).Call!.Value);
        Assert.Equal(0, Directory.Resolve(Setpoint("battery", -6), decreasesOnly: false).Call!.Value);
    }

    [Fact]
    public void A_value_within_a_millionth_of_a_step_snaps_to_it_instead_of_dropping_one()
    {
        // 6.3 A with a 0.1 A step is 2.9999… steps above 6 in binary: floored it would become 6.2.
        var range = new ModulatingRange(new Amperes(6), new Amperes(16), new Amperes(0.1));

        Assert.Equal(6.3, ActuatorDirectory.OnGrid(6.3, range, 6), 9);
        Assert.Equal(6.3, ActuatorDirectory.OnGrid(6.37, range, 6), 9);
        Assert.Equal(16, ActuatorDirectory.OnGrid(17, range, 6), 9);
        Assert.Equal(6, ActuatorDirectory.OnGrid(1, range, 6), 9);
    }

    [Fact]
    public void A_device_re_pointed_at_another_entity_is_not_redirected()
    {
        // The command was decided for the actuator of an older configuration: it never lands on the new one.
        var stale = Relay("heater_l1", on: false) with { Revision = Revision("heater_l1") + 1 };

        Assert.Equal(ActuatorRefusal.Superseded, Directory.Resolve(stale, decreasesOnly: false).Refusal);
    }

    [Fact]
    public void An_undeclared_device_or_a_stage_it_does_not_have_is_refused()
    {
        Assert.Equal(ActuatorRefusal.NotDeclared, Directory.Resolve(new DeviceCommand(DeviceName.Parse("boiler"), DeviceCommandKind.Relay, 1, false, null, CommandPriority.Protective), decreasesOnly: false).Refusal);
        Assert.Equal(ActuatorRefusal.NotDeclared, Directory.Resolve(Relay("heater_l1", on: false) with { Stage = 2 }, decreasesOnly: false).Refusal);
        Assert.Equal(ActuatorRefusal.NotDeclared, Directory.Resolve(Relay("heater_l1", on: false) with { Kind = DeviceCommandKind.Enable }, decreasesOnly: false).Refusal);
    }

    [Fact]
    public void During_an_invalid_configuration_only_decreases_reach_the_last_valid_declarations()
    {
        // 04 §8: safe state is every controlled device off, so the offs (and setpoints at their
        // floor) must still reach the devices of the last valid file; nothing may be raised.
        var holder = new ActuatorDirectoryHolder();
        holder.Observe(Configuration);
        holder.Observe(null);

        Assert.NotNull(holder.Resolve(Relay("heater_l1", on: false)).Call);
        Assert.Null(holder.Resolve(Relay("heater_l1", on: true)).Call);
        Assert.NotNull(holder.Resolve(Setpoint("ev_charger", 6)).Call);
        Assert.Null(holder.Resolve(Setpoint("ev_charger", 16)).Call);
        Assert.NotNull(holder.Resolve(new DeviceCommand(DeviceName.Parse("ev_charger"), DeviceCommandKind.Enable, 1, false, null, CommandPriority.Protective) { Revision = Revision("ev_charger") }).Call);
        Assert.True(holder.IsDeclared(EntityId.Parse("switch.heater_1"), "switch", ActuatorService.TurnOff, null));
        Assert.False(holder.IsDeclared(EntityId.Parse("switch.heater_1"), "switch", ActuatorService.TurnOn, null));
        Assert.True(holder.IsDeclared(EntityId.Parse("number.charger_current"), "number", ActuatorService.SetValue, 6));
        Assert.False(holder.IsDeclared(EntityId.Parse("number.charger_current"), "number", ActuatorService.SetValue, 7));
    }

    [Fact]
    public void Before_any_configuration_nothing_is_declared()
    {
        var holder = new ActuatorDirectoryHolder();

        Assert.Null(holder.Resolve(Relay("heater_l1", on: false)).Call);
        Assert.False(holder.IsDeclared(EntityId.Parse("switch.heater_1"), "switch", ActuatorService.TurnOff, null));
    }

    [Fact]
    public void A_removed_device_gets_exactly_one_final_off_per_actuator_and_nothing_else()
    {
        // A device Switchboard started and the user then removed is switched off once more (it would
        // otherwise run on with nobody watching it); after that its entity is undeclared like any other.
        var holder = new ActuatorDirectoryHolder();
        holder.Observe(Configuration);
        var without = TestConfigurations.Valid(TestDevices.Document(string.Join('\n', Devices.ReplaceLineEndings("\n").Split('\n').Skip(7)))).Configuration!;
        Assert.DoesNotContain(without.Devices, d => d.Id.Value == "heater_l1");
        holder.Observe(without);

        var final = Relay("heater_l1", on: false) with { Retiring = true };
        Assert.False(holder.IsDeclared(EntityId.Parse("switch.heater_1"), "switch", ActuatorService.TurnOff, null));
        Assert.Null(holder.Resolve(final with { On = true }).Call);
        var call = holder.Resolve(final).Call;
        Assert.Equal(EntityId.Parse("switch.heater_1"), call!.Entity);
        Assert.True(holder.IsDeclared(EntityId.Parse("switch.heater_1"), "switch", ActuatorService.TurnOff, null));
        Assert.False(holder.IsDeclared(EntityId.Parse("switch.heater_1"), "switch", ActuatorService.TurnOff, null));
        Assert.Null(holder.Resolve(final).Call);
        Assert.Null(holder.Resolve(Relay("heater_l1", on: false)).Call);
    }

    private static long Revision(string device) => Configuration.Devices.Single(d => d.Id.Value == device).ActuatorRevision;

    private static DeviceCommand Relay(string device, bool on) =>
        new(DeviceName.Parse(device), DeviceCommandKind.Relay, 1, on, null, on ? CommandPriority.Ordinary : CommandPriority.Protective) { Revision = Revision(device), Decrease = !on };

    private static DeviceCommand Setpoint(string device, double amps) =>
        new(DeviceName.Parse(device), DeviceCommandKind.Setpoint, 1, true, new Amperes(amps), CommandPriority.Ordinary) { Revision = Revision(device) };
}
