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
    public void An_hour_of_pure_import_keeps_the_balance_known_although_the_export_counter_never_reports()
    {
        // Live 0.8.0: while the house imports, the export counter stands still and is not reported
        // again, so it ages past max_age; the balance must not turn unknown because of it.
        _store.Push("sensor.meter_exported_kwh", "100.000");
        _store.Push("sensor.meter_imported_kwh", "200.000");
        Cycle();
        for (var step = 1; step <= 80; step++)
        {
            _store.Advance(TimeSpan.FromSeconds(30));
            _store.Push("sensor.meter_imported_kwh", (200.000 + (step * 0.010)).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            Cycle();
        }

        var response = BalanceEndpoint.Build(_monitor.Current);

        Assert.Equal(-800, response.Wh!.Value, 3);
        Assert.Equal("none", response.Reason);
    }

    [Fact]
    public void A_start_inside_the_hour_counts_from_the_first_report()
    {
        _store.Advance(TimeSpan.FromMinutes(25));
        Report("100.000", "200.000");
        _store.Advance(TimeSpan.FromMinutes(5));
        Report("100.300", "200.000");

        var response = BalanceEndpoint.Build(_monitor.Current);

        Assert.True(response.Configured);
        Assert.Equal(300, response.Wh!.Value, 3);
        Assert.Equal("none", response.Reason);
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

    private sealed class MemoryFile : IBalanceBaselineFile
    {
        public HourBaseline? Stored { get; set; }

        public bool Failing { get; set; }

        public int Saves { get; private set; }

        /// <summary>The thread that is inside Publish right now (0 outside it): a write on it would be file I/O on the publishing thread.</summary>
        public int PublisherThread { get; set; }

        public bool SavedOnPublisherThread { get; private set; }

        /// <summary>Completed by the first write attempt, so a test waits on the signal, not on time.</summary>
        public TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HourBaseline? Load() => Stored;

        public void Save(HourBaseline baseline)
        {
            Attempted.TrySetResult();
            if (Failing)
            {
                throw new IOException("disk full");
            }

            Stored = baseline;
            Saves++;
            SavedOnPublisherThread |= Environment.CurrentManagedThreadId == PublisherThread;
        }
    }

    private (HourlyBalanceMonitor Monitor, SignalSnapshotHolder Holder) Restarted(MemoryFile file)
    {
        var holder = new SignalSnapshotHolder(new SignalDiagnostics(_store.Snapshot(), null, [], []));
        return (new HourlyBalanceMonitor(holder, new SignalClock(_store.Time), NullLogger<HourlyBalanceMonitor>.Instance, file), holder);
    }

    [Fact]
    public async Task A_restart_inside_the_hour_keeps_the_balance_through_the_written_baseline()
    {
        var file = new MemoryFile();
        var (first, firstHolder) = Restarted(file);
        using var stop = new CancellationTokenSource();
        var writer = first.RunAsync(stop.Token);
        _store.Push("sensor.meter_exported_kwh", "100.000");
        _store.Push("sensor.meter_imported_kwh", "200.000");
        file.PublisherThread = Environment.CurrentManagedThreadId;
        firstHolder.Publish(new SignalDiagnostics(_store.Snapshot(), null, [], []));
        file.PublisherThread = 0;
        await file.Attempted.Task.Soon();
        await stop.CancelAsync();
        await writer;

        // The add-on restarts 25 minutes into the hour: a new monitor over the same file.
        _store.Advance(TimeSpan.FromMinutes(25));
        var (second, secondHolder) = Restarted(file);
        _store.Push("sensor.meter_exported_kwh", "100.450");
        _store.Push("sensor.meter_imported_kwh", "200.000");
        secondHolder.Publish(new SignalDiagnostics(_store.Snapshot(), null, [], []));

        Assert.Equal(450, second.Current!.Reading.Wh!.Value, 3);
        Assert.False(file.SavedOnPublisherThread);
    }

    [Fact]
    public void A_baseline_of_an_earlier_hour_in_the_file_is_not_used()
    {
        var file = new MemoryFile { Stored = new HourBaseline(_store.Now.AddHours(-1), 0, 0) };
        _store.Advance(TimeSpan.FromMinutes(25));
        var (monitor, holder) = Restarted(file);
        _store.Push("sensor.meter_exported_kwh", "100.450");
        _store.Push("sensor.meter_imported_kwh", "200.000");

        holder.Publish(new SignalDiagnostics(_store.Snapshot(), null, [], []));

        // Counted from this report, not from the earlier hour's 0/0 (that would read 100450 Wh).
        Assert.Equal(0, BalanceEndpoint.Build(monitor.Current).Wh!.Value, 3);
    }

    [Fact]
    public async Task A_full_disk_never_stops_the_balance_and_the_writer_survives_it()
    {
        var file = new MemoryFile { Failing = true };
        var (monitor, holder) = Restarted(file);
        using var stop = new CancellationTokenSource();
        var writer = monitor.RunAsync(stop.Token);
        _store.Push("sensor.meter_exported_kwh", "100.000");
        _store.Push("sensor.meter_imported_kwh", "200.000");
        holder.Publish(new SignalDiagnostics(_store.Snapshot(), null, [], []));
        _store.Advance(TimeSpan.FromMinutes(5));
        _store.Push("sensor.meter_exported_kwh", "100.100");
        _store.Push("sensor.meter_imported_kwh", "200.000");
        holder.Publish(new SignalDiagnostics(_store.Snapshot(), null, [], []));
        await file.Attempted.Task.Soon();

        Assert.Equal(100, monitor.Current!.Reading.Wh!.Value, 3);
        Assert.False(writer.IsFaulted);
        await stop.CancelAsync();
        await writer;
        Assert.Equal(0, file.Saves);
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
