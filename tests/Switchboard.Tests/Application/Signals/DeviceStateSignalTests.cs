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

    private static readonly StopOptions Force = new(OptionText.Parse("1"), OptionText.Parse("0"));

    [Fact]
    public void Stop_option_reads_as_stopped()
    {
        var sample = Interpret(new StateValue("1"));

        Assert.Equal((true, false, "1"), (sample.Fresh, ((RelayApplied)sample.Applied!).On, ((RelayApplied)sample.Applied!).Option?.Value));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("on")]
    [InlineData("off")]
    public void Any_other_option_reads_as_able_to_run(string option)
    {
        // Declared or not ("2" forces charging), any option but the stop option may draw: an
        // undeclared option can never hide a load. Only the declared stop option reads as stopped.
        var sample = Interpret(new StateValue(option));

        Assert.True(((RelayApplied)sample.Applied!).On);
        Assert.Equal(option, ((RelayApplied)sample.Applied!).Option?.Value);
    }

    [Theory]
    [InlineData(SignalState.Unavailable, SignalDetail.StateUnavailable, DeviceStateInvalidReason.Unavailable)]
    [InlineData(SignalState.Unavailable, SignalDetail.EntityMissing, DeviceStateInvalidReason.Missing)]
    [InlineData(SignalState.Unavailable, SignalDetail.AwaitingFirstSample, DeviceStateInvalidReason.Awaiting)]
    public void Unavailable_select_stays_unknown(SignalState state, SignalDetail detail, DeviceStateInvalidReason reason)
    {
        // Unknown counts as possibly running: protection holds the charger off and keeps it a candidate.
        var sample = DeviceStateInterpreter.Interpret(DeviceName.Parse("ev_charger"), DeviceStateSample.StopStage, SignalSourceStatus.Connected, state, detail, null, null, select: Force);

        Assert.Equal((false, (AppliedState?)null, (DeviceStateInvalidReason?)reason), (sample.Fresh, sample.Applied, sample.InvalidReason));
    }

    [Fact]
    public void A_switch_stop_still_reads_on_and_off_only()
    {
        Assert.Equal(new RelayApplied(false), DeviceStateInterpreter.Interpret(DeviceName.Parse("ev_charger"), DeviceStateSample.StopStage, SignalSourceStatus.Connected, SignalState.Fresh, SignalDetail.None, new StateValue("off"), null).Applied);
        Assert.Null(DeviceStateInterpreter.Interpret(DeviceName.Parse("ev_charger"), DeviceStateSample.StopStage, SignalSourceStatus.Connected, SignalState.Fresh, SignalDetail.None, new StateValue("1"), null).Applied);
    }

    [Fact]
    public void The_store_reads_the_select_stop_against_its_declared_stop_option()
    {
        var store = new StoreHarness(TestDevices.Document(SelectStopRig.Charger));

        store.Push("select.charger_force", "1", unit: null);
        Assert.Equal((false, "1"), Stop(store) is RelayApplied stopped ? (stopped.On, stopped.Option?.Value) : default);

        store.Advance(TimeSpan.FromSeconds(1));
        store.Push("select.charger_force", "2", unit: null);
        Assert.Equal((true, "2"), Stop(store) is RelayApplied running ? (running.On, running.Option?.Value) : default);
    }

    [Fact]
    public void Store_publishes_options_and_clears_them_on_disconnect()
    {
        var store = new StoreHarness(TestDevices.Document(SelectStopRig.Charger));
        var force = EntityId.Parse("select.charger_force");
        store.Store.Apply(EntityUpdateBatch.Of(BatchOrigin.Initial, new EntityUpdate(force, "0", null, store.Now, store.Now) { Options = ["0", "1", "2"] }), store.Now);

        Assert.Equal(["0", "1", "2"], store.Store.ReportedOptions()[force]);

        store.Store.ApplySourceStatus(SignalSourceStatus.Disconnected);
        Assert.Empty(store.Store.ReportedOptions());

        store.Store.ApplySourceStatus(SignalSourceStatus.Connected);
        store.Store.Apply(new EntityUpdateBatch(BatchOrigin.Initial, [], [force], []), store.Now);
        Assert.Empty(store.Store.ReportedOptions());
    }

    [Fact]
    public void A_reload_keeps_the_options_and_their_check()
    {
        // A select reports only on change: a configuration reload (a wizard save) must not forget
        // what it offers, or the options check would quietly stop working for hours.
        var yaml = TestDevices.Document(SelectStopRig.Charger);
        var store = new StoreHarness(yaml);
        var force = EntityId.Parse("select.charger_force");
        store.Store.Apply(EntityUpdateBatch.Of(BatchOrigin.Initial, new EntityUpdate(force, "0", null, store.Now, store.Now) { Options = ["0", "2"] }), store.Now);

        store.Store.ApplyConfiguration(TestConfigurations.Parse(yaml + "\n# edited\n"), store.Now);
        Assert.Equal(["0", "2"], store.Store.ReportedOptions()[force]);

        // A reading that changed (here: the select read again under a re-pointed stop option) keeps them too.
        store.Store.ApplyConfiguration(TestConfigurations.Parse(yaml.Replace("stop_option: \"1\"", "stop_option: \"2\"", StringComparison.Ordinal)), store.Now);
        Assert.Equal(["0", "2"], store.Store.ReportedOptions()[force]);
        var holder = new Switchboard.Application.Configuration.ActuatorDirectoryHolder();
        holder.Observe(TestConfigurations.Valid(yaml).Configuration);
        holder.ObserveOptions(store.Store.ReportedOptions());
        Assert.False(holder.IsDeclared(force, "select", Switchboard.Application.Configuration.ActuatorService.SelectOption, null, "1"));
    }

    [Fact]
    public void A_sample_read_again_under_a_changed_definition_keeps_its_options()
    {
        // The store keeps what any entity offers, generically: when a reload changes how an entity is
        // read (here its unit), the held state is read again as an initial state would deliver it,
        // options included, never as a state without them.
        var yaml = TestConfigurations.ThreePhase();
        var store = new StoreHarness(yaml);
        var current = EntityId.Parse("sensor.meter_current_a");
        store.Store.Apply(EntityUpdateBatch.Of(BatchOrigin.Initial, new EntityUpdate(current, "5", "A", store.Now, store.Now) { Options = ["5", "6"] }), store.Now);

        var milliamperes = yaml.Replace("    entity: sensor.meter_current_a\n    kind: current\n    unit: A\n", "    entity: sensor.meter_current_a\n    kind: current\n    unit: mA\n", StringComparison.Ordinal);
        Assert.NotEqual(yaml, milliamperes);
        store.Store.ApplyConfiguration(TestConfigurations.Parse(milliamperes), store.Now);

        Assert.Equal(["5", "6"], store.Store.ReportedOptions()[current]);
    }

    [Fact]
    public void A_stop_option_the_select_does_not_offer_is_a_runtime_warning_at_its_key()
    {
        var store = new StoreHarness(TestDevices.Document(SelectStopRig.Charger));
        store.Store.Apply(EntityUpdateBatch.Of(BatchOrigin.Initial, new EntityUpdate(EntityId.Parse("select.charger_force"), "0", null, store.Now, store.Now) { Options = ["0", "2"] }), store.Now);

        var warning = Assert.Single(store.Store.RuntimeWarnings());

        Assert.Equal(("invalid_value", "devices[0].stop_option"), (warning.Code, warning.Path));
        Assert.Contains("'1'", warning.Message, StringComparison.Ordinal);
        Assert.Contains("'0', '2'", warning.Message, StringComparison.Ordinal);
    }

    private static AppliedState? Stop(StoreHarness store) =>
        store.Snapshot().Protection.DeviceStates.Single(s => s.Device.Value == "ev_charger" && s.Stage == DeviceStateSample.StopStage).Applied;

    private static DeviceStateSample Interpret(SignalValue value) =>
        DeviceStateInterpreter.Interpret(DeviceName.Parse("ev_charger"), DeviceStateSample.StopStage, SignalSourceStatus.Connected, SignalState.Fresh, SignalDetail.None, value, DateTimeOffset.UnixEpoch, select: Force);

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
