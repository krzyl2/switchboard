using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Signals;

/// <summary>
/// BAL-01 inputs: the meter's total export and import counters reach dispatch as one pair. A counter
/// that does not change is not reported again (the export counter stands still while the house imports),
/// so its age proves nothing; the pair is usable while neither counter is unavailable and the meter is
/// alive. Found live in 0.8.0: requiring both counters to be fresh left the balance unknown for most of
/// the day.
/// </summary>
public sealed class EnergyCountersTests
{
    private const string Export = "sensor.meter_exported_kwh";
    private const string Import = "sensor.meter_imported_kwh";

    private const string Both = """
          energy_export:
            entity: sensor.meter_exported_kwh
            kind: energy_counter
            unit: kWh
            max_age: 90s
            role: energy_export
          energy_import:
            entity: sensor.meter_imported_kwh
            kind: energy_counter
            unit: kWh
            max_age: 90s
            role: energy_import

        """;

    private readonly StoreHarness _store = new(TestConfigurations.ThreePhase(Both));

    [Fact]
    public void Both_counters_reach_dispatch_in_kWh_as_of_the_latest_report()
    {
        _store.Push(Export, "12228.751");
        _store.Advance(TimeSpan.FromSeconds(4));
        _store.Push(Import, "16864.138");
        var latest = _store.Now;

        var energy = _store.Snapshot().Dispatch.Energy!;

        Assert.Equal(12228.751, energy.Counters!.Value.ExportKwh, 6);
        Assert.Equal(16864.138, energy.Counters.Value.ImportKwh, 6);
        Assert.Equal(latest, energy.Counters.Value.ReportedAt);
        Assert.Equal(TimeSpan.FromSeconds(90), energy.MaxAge);
    }

    [Fact]
    public void Counters_in_Wh_are_normalised_to_kWh()
    {
        var yaml = TestConfigurations.ThreePhase(Both.Replace("unit: kWh", "unit: Wh"));
        var store = new StoreHarness(yaml);
        store.Push(Export, "12228751");
        store.Push(Import, "16864138");

        var counters = store.Snapshot().Dispatch.Energy!.Counters!.Value;

        Assert.Equal(12228.751, counters.ExportKwh, 6);
        Assert.Equal(16864.138, counters.ImportKwh, 6);
    }

    [Fact]
    public void An_idle_counter_stays_valid_while_the_other_one_keeps_reporting()
    {
        _store.Push(Export, "100");
        _store.Push(Import, "200");
        _store.Advance(TimeSpan.FromSeconds(60));
        _store.Push(Import, "200.1");
        _store.Advance(TimeSpan.FromSeconds(40));
        _store.Push(Import, "200.2");
        var latest = _store.Now;

        var counters = _store.Snapshot().Dispatch.Energy!.Counters!.Value;

        Assert.Equal(SignalState.Stale, _store.Reading("energy_export").State);
        Assert.Equal(100, counters.ExportKwh);
        Assert.Equal(200.2, counters.ImportKwh, 6);
        Assert.Equal(latest, counters.ReportedAt);
    }

    [Fact]
    public void Both_counters_silent_and_no_live_power_is_a_dead_meter_not_a_valid_pair()
    {
        _store.Push(Export, "100");
        _store.Push(Import, "200");
        _store.Advance(TimeSpan.FromSeconds(120));

        Assert.Null(_store.Snapshot().Dispatch.Energy!.Counters);
    }

    [Fact]
    public void A_live_phase_power_keeps_a_pair_that_is_exactly_balanced_valid()
    {
        _store.Push(Export, "100");
        _store.Push(Import, "200");
        _store.Advance(TimeSpan.FromSeconds(120));
        _store.Push("sensor.meter_power_a", "0");
        var latest = _store.Now;

        var counters = _store.Snapshot().Dispatch.Energy!.Counters!.Value;

        Assert.Equal((100, 200), (counters.ExportKwh, counters.ImportKwh));
        Assert.Equal(latest, counters.ReportedAt);
    }

    [Fact]
    public void An_unavailable_counter_makes_the_pair_unusable_however_alive_the_meter_is()
    {
        _store.Push(Export, "100");
        _store.Push(Import, "200");
        _store.Advance(TimeSpan.FromSeconds(1));
        _store.Push("sensor.meter_power_a", "-1500");
        _store.Push(Export, "unavailable");

        Assert.Null(_store.Snapshot().Dispatch.Energy!.Counters);
    }

    [Fact]
    public void Without_a_report_yet_there_are_no_counters()
    {
        Assert.Null(_store.Snapshot().Dispatch.Energy!.Counters);
    }

    [Fact]
    public void Unmapped_counters_mean_no_energy_inputs_and_change_nothing_else()
    {
        var store = new StoreHarness(TestConfigurations.ThreePhase());

        Assert.Null(store.Snapshot().Dispatch.Energy);
        Assert.True(store.Snapshot().ConfigurationValid);
    }
}
