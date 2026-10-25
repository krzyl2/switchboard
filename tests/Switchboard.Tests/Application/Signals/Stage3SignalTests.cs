using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Signals;

/// <summary>
/// The signal layer for dispatch: device states that report only on change (fresh while
/// available, never by age), device draws read like any measurement (max_age, stale = no value),
/// circuit-group sub-meters, and the SIG-09 device terms (measured controlled loads only).
/// </summary>
public sealed class Stage3SignalTests
{
    private const string OnChangeHeater = """
          - id: heater_l1
            type: onoff
            actuator: switch.heater_1
            phase: L1
            rated_current_a: 6.6
            rated_power_w: 1500
            value_per_kwh: 0.35
            state_reports: on_change
        """;

    private const string DrawnCharger = """
          - id: ev_charger
            type: modulating
            actuator: number.charger_current
            phase: L1
            min: 6
            max: 20
            step: 1
            value_per_kwh: 2.03
            draw: { current: sensor.charger_draw_current, power: sensor.charger_draw_power, max_age: 60s }
        """;

    [Fact]
    public void On_change_state_is_fresh_while_available_with_no_age_limit()
    {
        // A Modbus relay reports only when it switches: hours after its last report its state is
        // still known, it is never probed, and no staleness deadline is armed for it.
        var store = new StoreHarness(TestDevices.Document(OnChangeHeater));
        store.Push("switch.heater_1", "on");

        store.Advance(TimeSpan.FromHours(30));

        var sample = DeviceSample(store, "heater_l1");
        Assert.Equal((true, (AppliedState?)new RelayApplied(true)), (sample.Fresh, sample.Applied));
        Assert.DoesNotContain("switch.heater_1", store.DueProbes());
        Assert.Equal(SignalState.Fresh, store.Reading("device_heater_l1").State);
    }

    [Theory]
    [InlineData("unavailable", DeviceStateInvalidReason.Unavailable)]
    [InlineData("unknown", DeviceStateInvalidReason.Unavailable)]
    public void On_change_state_goes_unknown_on_unavailable_or_disconnect(string state, DeviceStateInvalidReason reason)
    {
        // Availability is the whole freshness of an on-change state: unavailable or unknown, a
        // disconnected source or a removed entity make it unknown at once (counted as on).
        var store = new StoreHarness(TestDevices.Document(OnChangeHeater));
        store.Push("switch.heater_1", "on");
        store.Advance(TimeSpan.FromSeconds(1));
        store.Push("switch.heater_1", state);
        Assert.Equal((false, reason), (DeviceSample(store, "heater_l1").Fresh, DeviceSample(store, "heater_l1").InvalidReason!.Value));

        store.Advance(TimeSpan.FromSeconds(1));
        store.Push("switch.heater_1", "off");
        store.Store.ApplySourceStatus(SignalSourceStatus.Disconnected);
        Assert.Equal(DeviceStateInvalidReason.SourceDisconnected, DeviceSample(store, "heater_l1").InvalidReason);

        store.Store.ApplySourceStatus(SignalSourceStatus.Connected);
        store.Store.Apply(new EntityUpdateBatch(BatchOrigin.Change, [], [EntityId.Parse("switch.heater_1")], []), store.Now);
        Assert.Equal(DeviceStateInvalidReason.Missing, DeviceSample(store, "heater_l1").InvalidReason);
    }

    [Fact]
    public void Draw_signals_are_measurements_with_their_own_max_age()
    {
        // The charger's draw current (protection) and power (dispatch) are fresh within the draw's
        // 60 s and carry no value once stale; each lists the charger as its dependent (SIG-05).
        var store = new StoreHarness(TestDevices.Document(DrawnCharger));
        store.Push("sensor.charger_draw_current", "15.9");
        store.Push("sensor.charger_draw_power", "3650");

        var draw = Assert.Single(store.Snapshot().Protection.DeviceDraws);
        Assert.Equal((Phase.L1, true, 15.9), (draw.Phase, draw.Fresh, draw.Current!.Value.Value));
        var power = Assert.Single(store.Snapshot().Dispatch.DevicePowers);
        Assert.Equal((true, 3650.0), (power.Fresh, power.Power!.Value.Value));
        Assert.Equal(["ev_charger"], store.Reading("device_ev_charger_current").Dependents.Select(d => d.Value));
        Assert.Equal(SignalRole.DeviceCurrent, store.Reading("device_ev_charger_current").Role);

        store.Advance(TimeSpan.FromSeconds(60));

        var stale = Assert.Single(store.Snapshot().Protection.DeviceDraws);
        Assert.Equal((false, (Amperes?)null), (stale.Fresh, stale.Current));
        Assert.Null(Assert.Single(store.Snapshot().Dispatch.DevicePowers).Power);
    }

    [Fact]
    public void Draw_silent_at_zero_is_an_idle_device()
    {
        // Live 0.5.0: the go-e reports its draw only on change, so at rest it says 0 once and then
        // nothing. A silence after exactly 0 is an idle device: trusted as 0, which relieves nothing
        // and lets the house load be computed instead of staying unknown all day.
        var store = new StoreHarness(TestDevices.Document(DrawnCharger));
        store.Push("sensor.charger_draw_current", "0");
        store.Push("sensor.charger_draw_power", "0");

        store.Advance(TimeSpan.FromHours(5));

        var draw = Assert.Single(store.Snapshot().Protection.DeviceDraws);
        Assert.Equal((true, 0.0), (draw.Fresh, draw.Current!.Value.Value));
        var power = Assert.Single(store.Snapshot().Dispatch.DevicePowers);
        Assert.Equal((true, 0.0), (power.Fresh, power.Power!.Value.Value));
        Assert.Equal(SignalState.Stale, store.Reading("device_ev_charger_current").State);

        store.Store.ApplySourceStatus(SignalSourceStatus.Disconnected);
        Assert.False(Assert.Single(store.Snapshot().Protection.DeviceDraws).Fresh);
    }

    [Fact]
    public void Draw_silent_after_a_load_stays_untrusted()
    {
        // A draw frozen at a load would claim relief the device may no longer give (the 0.3.0
        // finding): only an exact zero means idle.
        var store = new StoreHarness(TestDevices.Document(DrawnCharger));
        store.Push("sensor.charger_draw_current", "0.76");
        store.Push("sensor.charger_draw_power", "0.05");

        store.Advance(TimeSpan.FromSeconds(61));

        Assert.Equal((false, (Amperes?)null), (Assert.Single(store.Snapshot().Protection.DeviceDraws).Fresh, Assert.Single(store.Snapshot().Protection.DeviceDraws).Current));
        Assert.Null(Assert.Single(store.Snapshot().Dispatch.DevicePowers).Power);
    }

    [Fact]
    public void Balance_subtracts_measured_controlled_loads()
    {
        // SIG-09 device terms: 500 W of house load on L1 cannot hold a charger measured at 1 kW, so
        // the phase power sample that makes the balance impossible is rejected.
        var store = new StoreHarness(TestDevices.Document(DrawnCharger));
        Produce(store, 600);
        store.Push("sensor.meter_power_a", "-300");
        store.Advance(TimeSpan.FromSeconds(1));
        store.Push("sensor.charger_draw_power", "1000");
        store.Advance(TimeSpan.FromSeconds(1));

        store.Push("sensor.meter_power_a", "-310");

        Assert.Equal(SignalDetail.BalanceResidual, store.Reading("grid_power_l1").Detail);
        Assert.Equal(510 - 1000, store.Snapshot().Balance.Single(b => b.Phase == Phase.L1).Residual!.Value.Value, 6);
    }

    [Fact]
    public void Balance_never_uses_a_model_value_for_a_heater()
    {
        // A heater reported on without a draw signal contributes nothing to the check (its rated
        // 1.5 kW is a model value: with its thermostat open it draws 0 W and would reject every
        // true sample).
        var store = new StoreHarness(TestDevices.Document());
        store.Push("switch.heater_1", "on");
        Produce(store, 600);

        store.Push("sensor.meter_power_a", "-300");

        Assert.Equal(SignalDetail.None, store.Reading("grid_power_l1").Detail);
        Assert.Equal(500, store.Snapshot().Balance.Single(b => b.Phase == Phase.L1).Residual!.Value.Value, 6);
    }

    [Fact]
    public void Stale_device_draw_is_left_out_of_the_balance()
    {
        // A stale draw is no measurement: leaving it out can only make a sample pass.
        var store = new StoreHarness(TestDevices.Document(DrawnCharger));
        store.Push("sensor.charger_draw_power", "1000");
        store.Advance(TimeSpan.FromSeconds(61));
        Produce(store, 600);

        store.Push("sensor.meter_power_a", "-300");

        Assert.Equal(SignalDetail.None, store.Reading("grid_power_l1").Detail);
    }

    [Fact]
    public void Device_draws_never_trigger_the_check_and_are_never_rejected()
    {
        // The balance tests the main meter's samples; a device draw that does not fit is not refused.
        var store = new StoreHarness(TestDevices.Document(DrawnCharger));
        Produce(store, 600);
        store.Push("sensor.meter_power_a", "-300");

        store.Push("sensor.charger_draw_power", "5000");

        Assert.Equal(SignalDetail.None, store.Reading("device_ev_charger_power").Detail);
        Assert.Equal(5000, Assert.Single(store.Snapshot().Dispatch.DevicePowers).Power!.Value.Value);
    }

    [Fact]
    public void Group_sub_meter_is_read_like_a_phase_current()
    {
        // CONN-03: a sub-metered group phase carries its own reading; stale, it has no valid data.
        var yaml = TestDevices.Document(DrawnCharger + "\n    circuit_group: carport", extra: "circuit_groups:\n  - { id: carport, breaker_rating_a: 16, characteristic: B, phases: [L1], current: { L1: sensor.carport_l1 }, current_max_age: 90s }\n");
        var store = new StoreHarness(yaml);
        store.Push("sensor.carport_l1", "12.5");

        var group = Assert.Single(store.Snapshot().Protection.Groups);
        Assert.Equal((true, false, 12.5), (group.DataValid, group.Estimated, group.Current!.Magnitude.Value));
        Assert.Equal(["ev_charger"], store.Reading("device__carport_current_l1").Dependents.Select(d => d.Value));

        store.Advance(TimeSpan.FromSeconds(90));
        Assert.Equal((false, ProtectionInvalidReason.CurrentStale), (store.Snapshot().Protection.Groups.Single().DataValid, store.Snapshot().Protection.Groups.Single().InvalidReason!.Value));
    }

    [Fact]
    public void Negative_group_sub_meter_reading_is_refused_not_read_as_export()
    {
        // A load-only downstream circuit cannot export. A reversed CT (or a meter that signs current
        // by power direction) reads a 20 A load as −20 A: read as an export, protection would never
        // shed the group (shedding on an exporting phase is refused). The sub-meter is unsigned, so
        // the sample is a sign violation, the group has no valid data and its members are held off
        // (Stale_sub_meter_holds_the_members_off).
        var yaml = TestDevices.Document(DrawnCharger + "\n    circuit_group: carport", extra: "circuit_groups:\n  - { id: carport, breaker_rating_a: 16, characteristic: B, phases: [L1], current: { L1: sensor.carport_l1 }, current_max_age: 90s }\n");
        var store = new StoreHarness(yaml);

        store.Push("sensor.carport_l1", "-20");

        var group = Assert.Single(store.Snapshot().Protection.Groups);
        Assert.Equal((false, ProtectionInvalidReason.CurrentRejected), (group.DataValid, group.InvalidReason!.Value));
        Assert.Equal(SignalDetail.SignViolation, store.Reading("device__carport_current_l1").Detail);

        store.Advance(TimeSpan.FromSeconds(10));
        store.Push("sensor.carport_l1", "12.5");
        var valid = store.Snapshot().Protection.Groups.Single();
        Assert.True(valid.DataValid, $"{valid.InvalidReason}");
        Assert.Equal(12.5, valid.Current!.Signed.Value);
        Assert.NotEqual(FlowDirection.Export, valid.Current.Direction);
    }

    [Fact]
    public void Estimated_group_has_no_reading_of_its_own()
    {
        var yaml = TestDevices.Document(DrawnCharger + "\n    circuit_group: carport", extra: "circuit_groups:\n  - { id: carport, breaker_rating_a: 16, characteristic: B, phases: [L1] }\n");
        var store = new StoreHarness(yaml);

        var group = Assert.Single(store.Snapshot().Protection.Groups);
        Assert.Equal((true, true, (CurrentReading?)null), (group.DataValid, group.Estimated, group.Current));
    }

    [Fact]
    public void Storage_setpoint_may_be_negative()
    {
        // A battery's negative setpoint is a discharge, a known state; for a charger it would be nonsense.
        var battery = "  - id: battery\n    type: storage\n    actuator: number.battery_current\n    phase: L1\n    min: -10\n    max: 16\n    step: 1\n    value_per_kwh: 1.5\n";
        var store = new StoreHarness(TestDevices.Document(battery));
        store.Push("number.battery_current", "-4");

        Assert.Equal(new SetpointApplied(new Amperes(-4)), DeviceSample(store, "battery").Applied);
    }

    private static DeviceStateSample DeviceSample(StoreHarness store, string device) =>
        store.Snapshot().Protection.DeviceStates.Single(s => s.Device.Value == device && s.Stage == 1);

    private static void Produce(StoreHarness store, double watts)
    {
        var third = (watts / 3 / 230).ToString(System.Globalization.CultureInfo.InvariantCulture);
        store.Push("sensor.inverter_power", watts.ToString(System.Globalization.CultureInfo.InvariantCulture));
        store.Push("sensor.inverter_current_a", third);
        store.Push("sensor.inverter_current_b", third);
        store.Push("sensor.inverter_current_c", third);
    }
}
