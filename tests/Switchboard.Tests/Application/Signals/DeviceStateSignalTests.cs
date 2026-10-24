using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Signals;

/// <summary>
/// SIG-05 completed: a device's applied state is a signal like any other (freshness from
/// last_reported, max_age, unit check), and every signal names the devices it protects, so a
/// stale signal visibly marks its dependent devices unsafe.
/// </summary>
public sealed class DeviceStateSignalTests
{
    private readonly StoreHarness _store = new(TestDevices.Document());

    [Fact]
    public void Stale_signal_lists_its_dependent_devices()
    {
        // The L1 current protects the heater and the charger on L1; a state signal its own device.
        Assert.Equal(["heater_l1", "ev_charger"], Dependents("grid_current_l1"));
        Assert.Equal(["heater_l2"], Dependents("grid_current_l2"));
        Assert.Equal(["heater_l1"], Dependents("device_heater_l1"));
        Assert.Empty(Dependents("grid_power_l1"));
    }

    [Theory]
    [InlineData("three_phase", new string[0])]
    [InlineData("three_phase_summed", new[] { "heater_l1", "heater_l2", "heater_l3", "ev_charger" })]
    public void Total_current_has_dependents_only_where_protection_reads_it(string topology, string[] expected)
    {
        // In three_phase the guard protects from the phase currents and a mapped total is display
        // only: listing every device under it would show them unsafe on /api/signals and /api/guard
        // while the guard keeps them allowed. In three_phase_summed every phase is protected from it.
        var yaml = TestDevices.Document().Replace("topology: three_phase", $"topology: {topology}", StringComparison.Ordinal).Replace(
            "signals:\n",
            "signals:\n  total_current:\n    entity: sensor.meter_total_current\n    kind: current\n    unit: A\n    sign: positive_import\n    max_age: 60s\n    role: total_current\n",
            StringComparison.Ordinal);
        var store = new StoreHarness(yaml);
        store.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(expected, store.Reading("total_current").Dependents.Select(d => d.Value));
    }

    [Fact]
    public void Device_state_goes_stale_after_state_max_age()
    {
        _store.Push("switch.heater_1", "on");
        Assert.Equal(new DeviceStateSample(DeviceName.Parse("heater_l1"), 1, true, null, new RelayApplied(true), _store.Now), Sample("heater_l1"));

        _store.Advance(TimeSpan.FromMinutes(10));

        var stale = Sample("heater_l1");
        Assert.Equal((false, DeviceStateInvalidReason.Stale, (AppliedState?)null), (stale.Fresh, stale.InvalidReason, stale.Applied));
    }

    [Fact]
    public void Relay_state_other_than_on_or_off_is_unrecognized()
    {
        _store.Push("switch.heater_1", "opening");

        Assert.Equal((false, DeviceStateInvalidReason.Unrecognized), (Sample("heater_l1").Fresh, Sample("heater_l1").InvalidReason));
    }

    [Fact]
    public void Modulating_state_in_wrong_unit_is_rejected_not_misread()
    {
        // A setpoint reported in W would otherwise be read as 3680 A; the signal layer refuses the
        // unit instead of guessing, and the device stays unknown (a shed candidate at its maximum).
        _store.Push("number.charger_current", "3680", unit: "W");
        Assert.Equal((false, DeviceStateInvalidReason.Unrecognized), (Sample("ev_charger").Fresh, Sample("ev_charger").InvalidReason));

        _store.Advance(TimeSpan.FromSeconds(1));
        _store.Push("number.charger_current", "16", unit: "A");
        Assert.Equal(new SetpointApplied(new Amperes(16)), Sample("ev_charger").Applied);
    }

    [Fact]
    public void Disconnected_source_leaves_every_device_state_unknown()
    {
        _store.Push("switch.heater_1", "on");

        _store.Store.ApplySourceStatus(SignalSourceStatus.Disconnected);

        Assert.All(_store.Snapshot().Protection.DeviceStates, s => Assert.Equal((false, DeviceStateInvalidReason.SourceDisconnected), (s.Fresh, s.InvalidReason)));
    }

    [Fact]
    public void Protection_inputs_carry_the_setup_of_the_same_cycle()
    {
        var protection = _store.Snapshot().Protection;

        Assert.NotNull(protection.Setup);
        Assert.Equal(4, protection.Setup!.Devices.Length);
        Assert.Equal(25 - (2500 / 230.0), protection.Setup.ReserveThreshold(Phase.L1).Value, 9);
        Assert.Equal(TimeSpan.FromSeconds(90), protection.Setup.CurrentMaxAge[Phase.L1]);
    }

    [Fact]
    public void Invalid_configuration_carries_no_setup_and_no_device_states()
    {
        _store.Store.ApplyConfiguration(TestConfigurations.Parse("schema_version: 2"), _store.Now);

        var protection = _store.Snapshot().Protection;

        Assert.Null(protection.Setup);
        Assert.Empty(protection.DeviceStates);
    }

    private List<string> Dependents(string signal) =>
        [.. _store.Reading(signal).Dependents.Select(d => d.Value)];

    private DeviceStateSample Sample(string device) => _store.Snapshot().Protection.DeviceStates.Single(s => s.Device.Value == device);
}
