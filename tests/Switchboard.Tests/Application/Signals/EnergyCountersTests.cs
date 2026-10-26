using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Signals;

/// <summary>
/// BAL-01 inputs: the meter's total export and import counters reach dispatch as one pair that is
/// fresh as a whole. A balance built from one fresh and one stale counter would be a silent wrong
/// number on the bill's scale, so a half-fresh pair carries no counters at all.
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
    public void Both_counters_fresh_reach_dispatch_in_kWh_with_the_older_report_time()
    {
        var first = _store.Now;
        _store.Push(Export, "12228.751");
        _store.Advance(TimeSpan.FromSeconds(4));
        _store.Push(Import, "16864.138");

        var energy = _store.Snapshot().Dispatch.Energy!;

        Assert.Equal(12228.751, energy.Counters!.Value.ExportKwh, 6);
        Assert.Equal(16864.138, energy.Counters.Value.ImportKwh, 6);
        Assert.Equal(first, energy.Counters.Value.ReportedAt);
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
    public void One_stale_counter_leaves_the_pair_unusable_and_not_half_used()
    {
        _store.Push(Export, "100");
        _store.Push(Import, "200");
        _store.Advance(TimeSpan.FromSeconds(60));
        _store.Push(Export, "100.1");
        _store.Advance(TimeSpan.FromSeconds(40));

        var energy = _store.Snapshot().Dispatch.Energy!;

        Assert.Null(energy.Counters);
        Assert.Equal(SignalState.Fresh, _store.Reading("energy_export").State);
        Assert.Equal(SignalState.Stale, _store.Reading("energy_import").State);
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
