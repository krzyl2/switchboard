using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Signals;

/// <summary>
/// SIG-08: the meter sees net flow, so without the inverter a phase whose load is covered by
/// production looks idle. House load per phase is derived here from primary measurements only.
/// SIG-09: a sample that makes the balance impossible (a negative house load) is rejected; the
/// decision that would have used it is not: it keeps the last accepted value until that ages out.
/// </summary>
public sealed class BalanceAndGenerationTests
{
    private const string PowerA = "sensor.meter_power_a";
    private const string PowerB = "sensor.meter_power_b";
    private const string Inverter = "sensor.inverter_power";

    private readonly StoreHarness _store = new(TestConfigurations.ThreePhase());

    [Fact]
    public void Phase_with_zero_import_and_generation_is_loaded_not_idle()
    {
        ProduceSplit(3000, 4.35, 4.35, 4.35);
        _store.Push(PowerA, "0");

        var l1 = _store.Dispatch(Phase.L1);
        Assert.Equal(0, l1.NetImport!.Value.Value);
        Assert.Equal(1000, l1.Load!.Value.Value, 6);
        Assert.True(l1.DataValid);
    }

    [Fact]
    public void Generation_split_by_inverter_phase_currents()
    {
        ProduceSplit(3000, 2.0, 4.0, 6.0);
        PushAllPhases("0");

        Assert.Equal(500, _store.Dispatch(Phase.L1).Generation!.Value.Value, 6);
        Assert.Equal(1000, _store.Dispatch(Phase.L2).Generation!.Value.Value, 6);
        Assert.Equal(1500, _store.Dispatch(Phase.L3).Generation!.Value.Value, 6);
    }

    [Fact]
    public void Inverter_without_phase_currents_splits_equally()
    {
        var store = new StoreHarness(TestConfigurations.ThreePhase(inverterCurrents: false));
        store.Push(Inverter, "2400");
        store.Push(PowerA, "-500");

        var l1 = store.Dispatch(Phase.L1);
        Assert.Equal(800, l1.Generation!.Value.Value, 6);
        Assert.Equal(1300, l1.Load!.Value.Value, 6);
    }

    [Fact]
    public void Unavailable_inverter_makes_load_unknown_not_equal_to_import()
    {
        // Treating an unknown production as zero would understate the load on a sunny afternoon
        // whenever the inverter integration hiccups.
        _store.Push(Inverter, "unavailable");
        _store.Push(PowerA, "-500");

        var l1 = _store.Dispatch(Phase.L1);
        Assert.Equal(500, l1.NetImport!.Value.Value);
        Assert.Null(l1.Generation);
        Assert.Null(l1.Load);
        Assert.Equal((false, DispatchInvalidReason.GenerationUnknown), (l1.DataValid, l1.InvalidReason));
    }

    [Fact]
    public void Inverter_declared_unavailable_as_zero_counts_as_no_generation_when_asleep()
    {
        var yaml = TestConfigurations.ThreePhase().Replace("role: inverter_power", "role: inverter_power\n    unavailable_as_zero: true", StringComparison.Ordinal);
        var store = new StoreHarness(yaml);
        store.Push(Inverter, "unavailable");
        store.Push(PowerA, "-500");

        Assert.Equal(500, store.Dispatch(Phase.L1).Load!.Value.Value);
        Assert.Equal(SignalState.Unavailable, store.Reading("pv_power").State);

        // An inverter that fell silent while producing is still unknown: its production may go on.
        store.Advance(TimeSpan.FromSeconds(1));
        store.Push(Inverter, "100");
        store.Advance(TimeSpan.FromSeconds(91));
        store.Push(PowerA, "-400");
        Assert.Null(store.Dispatch(Phase.L1).Load);
    }

    [Fact]
    public void Inverter_declared_unavailable_as_zero_that_falls_silent_at_zero_is_asleep()
    {
        // Live 0.4.0: SolarEdge reports 0 W at dusk and then nothing until morning (status sleeping),
        // never unavailable. Counting that silence as unknown left dispatch without data all night,
        // so no device could ever be raised at night (the charger's main use).
        var yaml = TestConfigurations.ThreePhase().Replace("role: inverter_power", "role: inverter_power\n    unavailable_as_zero: true", StringComparison.Ordinal);
        var store = new StoreHarness(yaml);
        store.Push(Inverter, "0");
        store.Advance(TimeSpan.FromSeconds(91));
        store.Push(PowerA, "-500");

        Assert.Equal(SignalState.Stale, store.Reading("pv_power").State);
        Assert.Equal(500, store.Dispatch(Phase.L1).Load!.Value.Value);
        Assert.True(store.Dispatch(Phase.L1).DataValid);
    }

    [Fact]
    public void Inverter_silent_at_zero_stays_unknown_without_the_declaration()
    {
        // Without unavailable_as_zero a silent inverter is never assumed asleep.
        _store.Push(Inverter, "0");
        _store.Advance(TimeSpan.FromSeconds(91));
        _store.Push(PowerA, "-500");

        Assert.Equal((false, DispatchInvalidReason.GenerationUnknown), (_store.Dispatch(Phase.L1).DataValid, _store.Dispatch(Phase.L1).InvalidReason));
    }

    [Fact]
    public void No_inverter_means_no_generation()
    {
        var store = new StoreHarness(TestConfigurations.ThreePhase(inverter: false));
        store.Push(PowerA, "-700");

        Assert.Equal(0, store.Dispatch(Phase.L1).Generation!.Value.Value);
        Assert.Equal(700, store.Dispatch(Phase.L1).Load!.Value.Value);
    }

    [Fact]
    public void Without_an_inverter_an_export_is_not_an_impossible_load()
    {
        // A PV house that has not mapped its inverter exports all day: with generation counted as
        // 0 W every export sample would look like a negative load, be rejected, and leave the
        // unsigned currents signed by it in cautious mode for the whole export period (07-PITFALLS).
        var store = new StoreHarness(TestConfigurations.ThreePhase(inverter: false));
        store.Push(PowerA, "1400");
        store.Push("sensor.meter_current_a", "6.1");

        Assert.Equal((SignalState.Fresh, SignalDetail.None), (store.Reading("grid_power_l1").State, store.Reading("grid_power_l1").Detail));
        Assert.Equal(-1400, store.Dispatch(Phase.L1).NetImport!.Value.Value);
        Assert.Equal(FlowDirection.Export, store.Protection(Phase.L1).Current!.Direction);
        Assert.Equal((0L, 0L), (store.Snapshot().Balance.Single(b => b.Phase == Phase.L1).Evaluations, store.Snapshot().Rejections.BalanceResidual));
    }

    [Fact]
    public void Inverter_reporting_unavailable_during_export_does_not_reject_phase_power()
    {
        // unavailable_as_zero is for the night; a daytime blip of the integration must not turn
        // every export sample into an impossible load.
        var yaml = TestConfigurations.ThreePhase().Replace("role: inverter_power", "role: inverter_power\n    unavailable_as_zero: true", StringComparison.Ordinal);
        var store = new StoreHarness(yaml);
        store.Push(Inverter, "unavailable");
        store.Push(PowerA, "1400");

        Assert.Equal((SignalState.Fresh, SignalDetail.None), (store.Reading("grid_power_l1").State, store.Reading("grid_power_l1").Detail));
        Assert.Equal(0, store.Snapshot().Rejections.BalanceResidual);
    }

    [Fact]
    public void Inverter_reporting_zero_is_a_measurement_and_still_checked()
    {
        // 0 W from a fresh inverter is a measured zero: 1400 W of export against it is impossible.
        ProduceSplit(0, 0, 0, 0);
        _store.Push(PowerA, "1400");

        Assert.Equal(SignalDetail.BalanceResidual, _store.Reading("grid_power_l1").Detail);
        Assert.Equal(1, _store.Snapshot().Rejections.BalanceResidual);
    }

    [Fact]
    public void Stale_or_rejected_phase_power_is_not_handed_to_dispatch()
    {
        ProduceSplit(0, 0, 0, 0);
        _store.Push(PowerA, "-500");
        _store.Push(PowerB, "-500");
        _store.Advance(TimeSpan.FromSeconds(30));
        _store.Push(PowerB, "-0.6", unit: "kW");
        _store.Advance(TimeSpan.FromSeconds(60));

        Assert.Equal((null, DispatchInvalidReason.PowerNotFresh), (_store.Dispatch(Phase.L1).NetImport, _store.Dispatch(Phase.L1).InvalidReason));
        Assert.Null(_store.Dispatch(Phase.L2).NetImport);
        Assert.Null(_store.Dispatch(Phase.L2).Load);
    }

    [Fact]
    public void Impossible_negative_residual_rejects_sample_and_keeps_last_accepted_value()
    {
        ProduceSplit(600, 1, 1, 1);
        _store.Push(PowerA, "-300");
        _store.Advance(TimeSpan.FromSeconds(1));

        // Export of 1500 W on L1 with 200 W of production there: a house load of −1300 W.
        _store.Push(PowerA, "1500");

        var reading = _store.Reading("grid_power_l1");
        Assert.Equal((SignalState.Fresh, SignalDetail.BalanceResidual), (reading.State, reading.Detail));
        Assert.Equal(300, Assert.IsType<PowerValue>(reading.Value).Power.Value);
        Assert.Equal(1, reading.RejectedCount);
        Assert.Equal((true, 300.0), (_store.Dispatch(Phase.L1).DataValid, _store.Dispatch(Phase.L1).NetImport!.Value.Value));
        Assert.Equal(-1300, _store.Snapshot().Balance.Single(b => b.Phase == Phase.L1).Residual!.Value.Value, 6);
    }

    [Fact]
    public void Held_value_after_a_rejected_sample_still_goes_stale_at_max_age()
    {
        // Holding the last accepted value must not keep it alive: it ages from its own report, even
        // while Home Assistant keeps confirming the refused state.
        ProduceSplit(600, 1, 1, 1);
        var held = _store.Now;
        _store.Push(PowerA, "-300");
        _store.Advance(TimeSpan.FromSeconds(1));
        ProduceSplit(600, 1, 1, 1);
        var refused = _store.Now;
        _store.Push(PowerA, "1500");

        _store.AdvanceTo(held + TimeSpan.FromSeconds(90) - TimeSpan.FromTicks(1));
        _store.ProbeAnswer(PowerA, "1500", refused, _store.Now);
        Assert.Equal(SignalState.Fresh, _store.Reading("grid_power_l1").State);

        _store.Advance(TimeSpan.FromTicks(1));
        Assert.Equal((SignalState.Stale, DispatchInvalidReason.PowerNotFresh), (_store.Reading("grid_power_l1").State, _store.Dispatch(Phase.L1).InvalidReason));
    }

    [Fact]
    public void Rejections_counted_per_phase_and_total()
    {
        ProduceSplit(600, 1, 1, 1);
        _store.Push(PowerA, "1500");
        _store.Push(PowerB, "900");
        _store.Advance(TimeSpan.FromSeconds(1));
        _store.Push(PowerB, "1000");

        var snapshot = _store.Snapshot();
        Assert.Equal(1, snapshot.Balance.Single(b => b.Phase == Phase.L1).Rejections);
        Assert.Equal(2, snapshot.Balance.Single(b => b.Phase == Phase.L2).Rejections);
        Assert.Equal(0, snapshot.Balance.Single(b => b.Phase == Phase.L3).Rejections);
        Assert.Equal((3, 3, 0), (snapshot.Rejections.Total, snapshot.Rejections.BalanceResidual, snapshot.Rejections.InvalidSample));
    }

    [Fact]
    public void Positive_residual_is_foreign_load_not_an_error()
    {
        ProduceSplit(600, 1, 1, 1);
        _store.Push(PowerA, "-4000");

        Assert.Equal(SignalState.Fresh, _store.Reading("grid_power_l1").State);
        Assert.Equal(4200, _store.Snapshot().Balance.Single(b => b.Phase == Phase.L1).Residual!.Value.Value, 6);
    }

    [Fact]
    public void Balance_never_rejects_phase_current_so_protection_is_untouched()
    {
        ProduceSplit(600, 1, 1, 1);
        _store.Push(PowerA, "1500");
        _store.Push("sensor.meter_current_a", "6.5");

        Assert.Equal(SignalState.Fresh, _store.Reading("grid_current_l1").State);
        Assert.True(_store.Protection(Phase.L1).DataValid);
    }

    [Fact]
    public void Misaligned_inputs_are_not_compared()
    {
        // Production from 30 s ago against power from now would compare two different moments.
        ProduceSplit(600, 1, 1, 1);
        _store.Advance(TimeSpan.FromSeconds(30));
        _store.Push(PowerA, "1500");

        var balance = _store.Snapshot().Balance.Single(b => b.Phase == Phase.L1);
        Assert.Equal(SignalState.Fresh, _store.Reading("grid_power_l1").State);
        Assert.Equal((0, 1), (balance.Evaluations, balance.SkippedMisaligned));
    }

    [Fact]
    public void Next_consistent_sample_is_accepted()
    {
        ProduceSplit(600, 1, 1, 1);
        _store.Push(PowerA, "1500");
        _store.Advance(TimeSpan.FromSeconds(1));
        _store.Push(PowerA, "150");

        Assert.Equal(SignalState.Fresh, _store.Reading("grid_power_l1").State);
        Assert.Equal(-150, _store.Dispatch(Phase.L1).NetImport!.Value.Value);
    }

    [Fact]
    public void Out_of_step_inverter_sample_is_the_one_rejected()
    {
        // The meter shows export; then the inverter reports a cloud a poll early. The trigger is
        // the inverter sample, so it is the one refused, and the meter keeps its value.
        ProduceSplit(3000, 4.3, 4.3, 4.3);
        PushAllPhases("800");
        _store.Advance(TimeSpan.FromSeconds(1));

        _store.Push(Inverter, "300");

        Assert.Equal((SignalState.Fresh, SignalDetail.BalanceResidual), (_store.Reading("pv_power").State, _store.Reading("pv_power").Detail));
        Assert.Equal(SignalState.Fresh, _store.Reading("grid_power_l1").State);

        // One out-of-step inverter sample must not make generation unknown on every phase.
        Assert.All(_store.Snapshot().Dispatch.Phases, p => Assert.True(p.DataValid));
        Assert.Equal(1000, _store.Dispatch(Phase.L1).Generation!.Value.Value, 6);
    }

    [Fact]
    public void Rejected_power_sample_leaves_an_unchanged_current_signed_by_the_held_power()
    {
        // The current has not changed since the held power signed it, so that pairing still holds.
        ProduceSplit(600, 1, 1, 1);
        _store.Push(PowerA, "-300");
        _store.Push("sensor.meter_current_a", "1.3");
        _store.Advance(TimeSpan.FromSeconds(1));
        _store.Push(PowerA, "1500");

        var current = _store.Protection(Phase.L1).Current!;
        Assert.Equal((DirectionSource.SignedSignal, FlowDirection.Import), (current.DirectionSource, current.Direction));
    }

    [Fact]
    public void Current_changed_with_a_rejected_power_is_not_signed_by_the_older_held_power()
    {
        // The held power belongs to the previous meter poll; the new current does not. After the
        // pairing wait the direction is unknown: cautious mode, counted as import.
        ProduceSplit(600, 1, 1, 1);
        _store.Push(PowerA, "300");
        _store.Push("sensor.meter_current_a", "1.3");
        _store.Advance(TimeSpan.FromSeconds(10));
        ProduceSplit(600, 1, 1, 1);
        _store.Push(PowerA, "1500");
        _store.Push("sensor.meter_current_a", "6.5");
        Assert.Equal(DirectionSource.Pending, _store.Protection(Phase.L1).Current!.DirectionSource);

        _store.Advance(TimeSpan.FromSeconds(2));

        var current = _store.Protection(Phase.L1).Current!;
        Assert.Equal((DirectionSource.Unavailable, FlowDirection.Unknown, 6.5), (current.DirectionSource, current.Direction, current.Signed.Value));
    }

    private void ProduceSplit(double watts, double l1, double l2, double l3)
    {
        _store.Push(Inverter, watts.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _store.Push("sensor.inverter_current_a", l1.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _store.Push("sensor.inverter_current_b", l2.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _store.Push("sensor.inverter_current_c", l3.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private void PushAllPhases(string exportWatts)
    {
        foreach (var letter in new[] { "a", "b", "c" })
        {
            _store.Push($"sensor.meter_power_{letter}", exportWatts);
        }
    }
}
