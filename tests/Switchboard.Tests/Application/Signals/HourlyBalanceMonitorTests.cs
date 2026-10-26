using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Balance;
using Switchboard.Application.Signals;
using Switchboard.Domain.Balance;
using Switchboard.Host.Web;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Signals;

/// <summary>
/// BAL-01 and BAL-07 through the real chain: Home Assistant counter reports into the signal store,
/// published cycles into the monitor, the monitor into api/balance. The clock is the store's, local
/// time is Poland's, so the hour boundary is the local :00 and not the UTC one.
/// </summary>
public sealed class HourlyBalanceMonitorTests
{
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

    private readonly StoreHarness _store;
    private readonly SignalSnapshotHolder _holder;
    private readonly HourlyBalanceMonitor _monitor;

    public HourlyBalanceMonitorTests()
    {
        _store = new StoreHarness(TestConfigurations.ThreePhase(Both));
        _store.Time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw"));
        _holder = new SignalSnapshotHolder(new SignalDiagnostics(_store.Snapshot(), null, [], []));
        _monitor = new HourlyBalanceMonitor(_holder, new SignalClock(_store.Time), NullLogger<HourlyBalanceMonitor>.Instance);
    }

    private void Cycle() => _holder.Publish(new SignalDiagnostics(_store.Snapshot(), null, [], []));

    private void Report(string export, string import)
    {
        _store.Push("sensor.meter_exported_kwh", export);
        _store.Push("sensor.meter_imported_kwh", import);
        Cycle();
    }

    [Fact]
    public void The_balance_counts_from_the_local_hour_and_reaches_the_api_in_Wh()
    {
        // The harness starts at 12:00:00Z, which is 14:00 in Warsaw in October 2026 (UTC+2): an hour boundary.
        Report("100.000", "200.000");
        _store.Advance(TimeSpan.FromMinutes(20));
        Report("100.500", "200.200");

        var response = BalanceEndpoint.Build(_monitor.Current);

        Assert.True(response.Configured);
        Assert.Equal(300, response.Wh!.Value, 3);
        Assert.Equal("none", response.Reason);
        Assert.Equal(40 * 60, response.SecondsToNextHour!.Value, 1);
    }

    [Fact]
    public void A_start_inside_the_hour_reports_no_baseline_never_a_zero()
    {
        _store.Advance(TimeSpan.FromMinutes(25));
        Report("100.000", "200.000");

        var response = BalanceEndpoint.Build(_monitor.Current);

        Assert.True(response.Configured);
        Assert.Null(response.Wh);
        Assert.Equal("no_baseline", response.Reason);
    }

    [Fact]
    public void The_next_hour_starts_from_the_last_report_before_its_boundary()
    {
        Report("100.000", "200.000");
        _store.Advance(TimeSpan.FromMinutes(59).Add(TimeSpan.FromSeconds(55)));
        Report("100.800", "200.000");
        _store.Advance(TimeSpan.FromSeconds(15));
        Report("100.810", "200.050");

        var response = BalanceEndpoint.Build(_monitor.Current);

        Assert.Equal(-40, response.Wh!.Value, 3);
    }

    [Fact]
    public void Without_mapped_counters_the_balance_is_not_configured()
    {
        var store = new StoreHarness(TestConfigurations.ThreePhase());
        var holder = new SignalSnapshotHolder(new SignalDiagnostics(store.Snapshot(), null, [], []));
        var monitor = new HourlyBalanceMonitor(holder, new SignalClock(store.Time), NullLogger<HourlyBalanceMonitor>.Instance);

        holder.Publish(new SignalDiagnostics(store.Snapshot(), null, [], []));

        var response = BalanceEndpoint.Build(monitor.Current);
        Assert.False(response.Configured);
        Assert.Null(response.Wh);
    }

    [Fact]
    public void A_failure_in_the_monitor_never_reaches_the_publisher_and_keeps_the_last_value()
    {
        Report("100.000", "200.000");
        _store.Advance(TimeSpan.FromMinutes(5));
        Report("100.100", "200.000");
        var before = _monitor.Current;

        // A cycle whose dispatch view is missing makes the monitor throw inside its handler.
        var broken = _store.Snapshot() with { Dispatch = null! };
        var thrown = Record.Exception(() => _holder.Publish(new SignalDiagnostics(broken, null, [], [])));

        Assert.Null(thrown);
        Assert.Equal(100, before!.Reading.Wh!.Value, 3);
        Assert.Same(before, _monitor.Current);
    }
}
