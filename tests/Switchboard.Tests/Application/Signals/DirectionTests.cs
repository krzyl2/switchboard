using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Signals;

/// <summary>
/// SIG-03: an unsigned current says nothing about direction. During a large export a system that
/// reads it as import sheds loads for no reason, which only makes the export larger (07-PITFALLS).
/// Direction comes from the signed power of the same phase and the same meter poll; when that is not
/// available the value counts as import (cautious mode) and protection data stays valid.
/// </summary>
public sealed class DirectionTests
{
    private const string Power = "sensor.meter_power_a";
    private const string Current = "sensor.meter_current_a";

    private readonly StoreHarness _store = new(TestConfigurations.ThreePhase());

    [Fact]
    public void Current_takes_its_sign_from_the_power_of_the_same_poll()
    {
        _store.Push(Power, "-1150");
        _store.Push(Current, "5.0");
        var import = _store.Protection(Phase.L1).Current!;

        _store.Advance(TimeSpan.FromSeconds(30));
        _store.Push(Power, "2070");
        _store.Push(Current, "9.0");
        var export = _store.Protection(Phase.L1).Current!;

        Assert.Equal((FlowDirection.Import, 5.0), (import.Direction, import.Signed.Value));
        Assert.Equal((FlowDirection.Export, -9.0), (export.Direction, export.Signed.Value));
        Assert.Equal(9.0, export.Magnitude.Value);
        Assert.Equal(DirectionSource.SignedSignal, export.DirectionSource);
    }

    [Fact]
    public void Changed_current_waits_for_its_power_from_the_same_poll()
    {
        // The meter writes a poll's current a few milliseconds before its power (88-91% of polls
        // in recorded data); for that moment the previous power is a poll old.
        _store.Push(Power, "-1150");
        _store.Push(Current, "5.0");
        _store.Advance(TimeSpan.FromSeconds(30));

        _store.Push(Current, "9.0");
        var pending = _store.Protection(Phase.L1).Current!;
        _store.Advance(TimeSpan.FromMilliseconds(5));
        _store.Push(Power, "2070");
        var paired = _store.Protection(Phase.L1).Current!;

        Assert.Equal((DirectionSource.Pending, FlowDirection.Unknown, 9.0), (pending.DirectionSource, pending.Direction, pending.Signed.Value));
        Assert.Equal((DirectionSource.SignedSignal, FlowDirection.Export, -9.0), (paired.DirectionSource, paired.Direction, paired.Signed.Value));
    }

    [Fact]
    public void Direction_sample_too_far_apart_means_cautious_mode()
    {
        _store.Push(Power, "2070");
        _store.Advance(TimeSpan.FromSeconds(30));
        _store.Push(Current, "9.0");

        _store.Advance(TimeSpan.FromSeconds(2) - TimeSpan.FromTicks(1));
        Assert.Equal(DirectionSource.Pending, _store.Protection(Phase.L1).Current!.DirectionSource);

        _store.Advance(TimeSpan.FromTicks(1));
        var cautious = _store.Protection(Phase.L1).Current!;
        Assert.Equal((DirectionSource.Unavailable, FlowDirection.Unknown, 9.0), (cautious.DirectionSource, cautious.Direction, cautious.Signed.Value));
    }

    [Fact]
    public void Expired_pairing_asks_for_the_direction_power_and_its_confirmation_resolves_it()
    {
        // The power repeated its value this poll, so HA sent no event; reading its last_reported
        // shows it was reported with the current.
        var powerChanged = _store.Now;
        _store.Push(Power, "2070");
        _store.Advance(TimeSpan.FromSeconds(30));
        _store.Push(Current, "9.0");
        _store.Advance(TimeSpan.FromSeconds(2));

        Assert.Contains(Power, _store.DueProbes());
        _store.ProbeAnswer(Power, "2070", powerChanged, _store.Now - TimeSpan.FromSeconds(2));

        var resolved = _store.Protection(Phase.L1).Current!;
        Assert.Equal((DirectionSource.SignedSignal, FlowDirection.Export), (resolved.DirectionSource, resolved.Direction));
    }

    [Fact]
    public void Newer_direction_power_still_signs_an_unchanged_current()
    {
        // A current that repeats its value produces no event while the power keeps changing.
        _store.Push(Power, "-1150");
        _store.Push(Current, "5.0");
        _store.Advance(TimeSpan.FromSeconds(30));
        _store.Push(Power, "-1180");

        Assert.Equal(DirectionSource.SignedSignal, _store.Protection(Phase.L1).Current!.DirectionSource);
    }

    [Fact]
    public void Stale_direction_signal_means_cautious_mode_counted_as_import()
    {
        _store.Push(Power, "2070");
        _store.Push(Current, "9.0");
        for (var i = 0; i < 4; i++)
        {
            _store.Advance(TimeSpan.FromSeconds(30));
            _store.Push(Current, (9.0 + (i * 0.1)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
        }

        var reading = _store.Protection(Phase.L1);
        Assert.Equal(SignalState.Stale, _store.Reading("grid_power_l1").State);
        Assert.Equal((DirectionSource.Unavailable, FlowDirection.Unknown), (reading.Current!.DirectionSource, reading.Current.Direction));
        Assert.True(reading.Current.Signed.Value > 0, "Cautious mode counts the current as import.");
    }

    [Fact]
    public void Rejected_direction_sample_means_cautious_mode()
    {
        _store.Push(Power, "2070");
        _store.Push(Current, "9.0");
        _store.Advance(TimeSpan.FromSeconds(30));
        _store.Push(Power, "2.1", unit: "kW");

        Assert.Equal(SignalState.Rejected, _store.Reading("grid_power_l1").State);
        Assert.Equal(DirectionSource.Unavailable, _store.Protection(Phase.L1).Current!.DirectionSource);
    }

    [Theory]
    [InlineData("15", FlowDirection.Import, DirectionSource.Deadband)]
    [InlineData("-20", FlowDirection.Import, DirectionSource.Deadband)]
    [InlineData("21", FlowDirection.Export, DirectionSource.SignedSignal)]
    [InlineData("-21", FlowDirection.Import, DirectionSource.SignedSignal)]
    public void Power_inside_deadband_counts_as_import(string exportPower, FlowDirection direction, DirectionSource source)
    {
        _store.Push(Power, exportPower);
        _store.Push(Current, "0.1");

        var reading = _store.Protection(Phase.L1).Current!;
        Assert.Equal((direction, source), (reading.Direction, reading.DirectionSource));
    }

    [Fact]
    public void Cautious_mode_keeps_protection_data_valid()
    {
        // The current itself is fresh; only its sign is unknown. Invalidating protection data here
        // would put the installation in safe state every time a power sample is late.
        _store.Push(Current, "12.0");

        var phase = _store.Protection(Phase.L1);
        Assert.True(phase.DataValid);
        Assert.Equal(12.0, phase.Current!.Signed.Value);
    }

    [Fact]
    public void Declared_absence_of_direction_is_permanent_cautious_mode()
    {
        var yaml = TestConfigurations.ThreePhase().Replace("direction_from: grid_power_l1", "direction_from: none", StringComparison.Ordinal);
        var store = new StoreHarness(yaml);
        store.Push(Power, "2070");
        store.Push(Current, "9.0");

        var reading = store.Protection(Phase.L1).Current!;
        Assert.Equal((DirectionSource.NotConfigured, FlowDirection.Unknown, 9.0), (reading.DirectionSource, reading.Direction, reading.Signed.Value));
        Assert.Contains(store.Configuration.Warnings, w => w.Code == "direction_unavailable_cautious");
    }

    private const string PowerFactor = "sensor.meter_pf_a";

    private static StoreHarness PowerFactorStore() => new(
        TestConfigurations.ThreePhase(extra: "  grid_pf_l1:\n    entity: sensor.meter_pf_a\n    kind: percentage\n    unit: \"%\"\n    sign: positive_import\n    max_age: 90s\n    role: phase_power_factor\n    phase: L1\n")
            .Replace("direction_from: grid_power_l1", "direction_from: grid_pf_l1", StringComparison.Ordinal));

    [Fact]
    public void Wrapped_phase_power_cannot_turn_a_heavy_import_into_export()
    {
        // 2026-10-27 11:45 on the reference install: the meter's phase power wrapped to +3059 W (export
        // by its convention) while L1 drew 14.9 A and imported 3.5 kW. The power factor of the same phase
        // stayed +81 %, so the direction must stay import and protection must keep shedding.
        var store = PowerFactorStore();
        store.Push(Power, "3059.4");
        store.Push(PowerFactor, "81.59");
        store.Push(Current, "14.868");

        var reading = store.Protection(Phase.L1).Current!;
        Assert.Equal((FlowDirection.Import, 14.868), (reading.Direction, reading.Signed.Value));
    }

    [Fact]
    public void Negative_power_factor_counts_as_export_only_after_it_has_persisted()
    {
        var store = PowerFactorStore();
        store.Push(PowerFactor, "-77.1");
        store.Push(Current, "14.7");
        var glitch = store.Protection(Phase.L1).Current!;

        store.Advance(TimeSpan.FromSeconds(10));
        store.Push(PowerFactor, "81.0");
        store.Push(Current, "14.8");
        var recovered = store.Protection(Phase.L1).Current!;

        store.Advance(TimeSpan.FromSeconds(10));
        store.Push(PowerFactor, "-80.0");
        store.Push(Current, "6.0");
        for (var i = 0; i < 2; i++)
        {
            store.Advance(TimeSpan.FromSeconds(10));
            store.Push(PowerFactor, $"-8{i}.0");
            store.Push(Current, $"6.{i + 1}");
        }

        var held = store.Protection(Phase.L1).Current!;
        store.Advance(TimeSpan.FromSeconds(10));
        store.Push(PowerFactor, "-83.0");
        store.Push(Current, "6.4");
        var persisted = store.Protection(Phase.L1).Current!;

        Assert.Equal(FlowDirection.Import, glitch.Direction);
        Assert.Equal(FlowDirection.Import, recovered.Direction);
        Assert.Equal(FlowDirection.Import, held.Direction);
        Assert.Equal((FlowDirection.Export, -6.4), (persisted.Direction, persisted.Signed.Value));
    }

    [Theory]
    [InlineData("3059.4", 2760.0)] // wrapped: true import ≈ reading − 6553.6 W; the dispatch input is U·I·PF, not the register
    [InlineData("0", 2760.0)] // zeroed near a zero net balance while the current is real
    public void Dispatch_net_import_comes_from_voltage_current_and_power_factor_when_the_meter_power_is_wrong(string powerRegister, double expected)
    {
        var store = PowerFactorStore();
        store.Push(Power, powerRegister);
        store.Push("sensor.meter_voltage_a", "230");
        store.Push(PowerFactor, "80");
        store.Push(Current, "15");

        var dispatch = store.Dispatch(Phase.L1);
        Assert.Equal(expected, dispatch.NetImport!.Value.Value, 6);
    }

    [Fact]
    public void A_one_poll_negative_power_factor_never_makes_the_dispatch_phase_export()
    {
        var store = PowerFactorStore();
        store.Push(Power, "3059.4");
        store.Push("sensor.meter_voltage_a", "230");
        store.Push(PowerFactor, "-77");
        store.Push(Current, "15");

        Assert.Equal(230 * 15 * 0.77, store.Dispatch(Phase.L1).NetImport!.Value.Value, 6);
    }

    [Fact]
    public void Dispatch_keeps_the_meter_power_without_a_fresh_power_factor()
    {
        var store = PowerFactorStore();
        store.Push(Power, "-1150"); // positive_export: an import of 1150 W
        store.Push("sensor.meter_voltage_a", "230");
        store.Push(Current, "5");

        Assert.Equal(1150, store.Dispatch(Phase.L1).NetImport!.Value.Value);
    }

    [Fact]
    public void Power_factor_inside_its_deadband_counts_as_import()
    {
        var store = PowerFactorStore();
        store.Push(PowerFactor, "-15");
        store.Push(Current, "0.4");

        var reading = store.Protection(Phase.L1).Current!;
        Assert.Equal((FlowDirection.Import, DirectionSource.Deadband), (reading.Direction, reading.DirectionSource));
    }

    [Fact]
    public void Diagnostics_show_the_signed_value_and_where_the_sign_came_from()
    {
        _store.Push(Power, "2070");
        _store.Push(Current, "9.0");

        var reading = _store.Reading("grid_current_l1");
        Assert.Equal(new CurrentValue(new Switchboard.Domain.Units.Amperes(-9.0)), reading.Value);
        Assert.Equal(new DirectionInfo(DirectionSource.SignedSignal, FlowDirection.Export, SignalName.Parse("grid_power_l1")), reading.Direction);
    }
}
