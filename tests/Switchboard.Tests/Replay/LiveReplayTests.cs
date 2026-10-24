using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Replay;

/// <summary>
/// One pass over the 72 h recorded at the maintainer's installation (gitignored), shared by the
/// LiveReplay tests: the maintainer's example configuration plus the charger, through the whole
/// pipeline on a virtual clock, with checkpoints two seconds (the pairing wait) after every main
/// meter current report.
/// </summary>
public sealed class LiveReplayRun
{
    /// <summary>
    /// The recording polls the SolarEdge meter and inverter every 30 s until the integration was
    /// reloaded at 2026-10-24 10:01:17 UTC, and every 10 s after that.
    /// </summary>
    public static readonly DateTimeOffset BeforeScanChange = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);

    public LiveReplayRun()
        : this(TestConfigurations.ExampleWithCharger)
    {
    }

    internal LiveReplayRun(string yaml)
    {
        var data = ReplayFixtures.LiveData;
        var harness = new ReplayHarness(data, yaml);
        var checkpoints = data.Rows
            .Where(r => r.Entity.Value.StartsWith("sensor.solaredge_m1_ac_current_", StringComparison.Ordinal))
            .Select(r => r.Timestamp + TimeSpan.FromSeconds(2))
            .Append(BeforeScanChange)
            .Distinct();

        harness.RunUntil(data.End, checkpoints, (at, diagnostics) =>
        {
            if (at == BeforeScanChange)
            {
                AtThirtySeconds = diagnostics;
            }

            var signals = diagnostics.Snapshot.Signals;
            foreach (var phase in diagnostics.Snapshot.Protection.Phases)
            {
                var suffix = phase.Phase.ToString().ToLowerInvariant();
                var power = signals.Single(s => s.Name?.Value == $"grid_power_{suffix}");
                if (phase.Current is not { } current || power.State != SignalState.Fresh)
                {
                    continue;
                }

                switch (current.Direction)
                {
                    case FlowDirection.Import:
                        Import++;
                        break;
                    case FlowDirection.Export:
                        Export++;
                        break;
                    default:
                        Cautious++;
                        break;
                }
            }
        });

        Final = harness.Current;
    }

    public int Import { get; private set; }

    public int Export { get; private set; }

    public int Cautious { get; private set; }

    public Switchboard.Application.Signals.SignalDiagnostics Final { get; }

    public Switchboard.Application.Signals.SignalDiagnostics? AtThirtySeconds { get; private set; }

    public static TimeSpan? Refresh(Switchboard.Application.Signals.SignalDiagnostics diagnostics, string name) =>
        diagnostics.Snapshot.Signals.Single(s => s.Name?.Value == name).DetectedRefresh;
}

[Trait("Category", "LiveReplay")]
public sealed class LiveReplayTests(LiveReplayRun run) : IClassFixture<LiveReplayRun>
{
    [Fact]
    public void Real_M1_current_direction_follows_power_sign_in_import_and_export_periods()
    {
        // The meter writes a poll's current a few ms before its power; pairing by report time keeps
        // cautious mode (which would shed during export for no reason) for genuinely unknown cases.
        Assert.True(run.Import >= 100 && run.Export >= 100, $"Only {run.Import} import and {run.Export} export readings; the check would pass vacuously.");
        var total = run.Import + run.Export + run.Cautious;
        Assert.True(run.Cautious < total / 100.0, $"Cautious mode in {run.Cautious} of {total} readings taken {TimeSpan.FromSeconds(2)} after a current report.");
    }

    [Theory]
    [InlineData("grid_current_l1")]
    [InlineData("grid_current_l2")]
    [InlineData("grid_current_l3")]
    [InlineData("grid_power_l1")]
    [InlineData("pv_power")]
    public void Real_M1_refresh_is_30s_within_1s(string signal)
    {
        Assert.Equal(30, LiveReplayRun.Refresh(run.AtThirtySeconds!, signal)!.Value.TotalSeconds, 1.0);
    }

    [Theory]
    [InlineData("grid_current_l1")]
    [InlineData("grid_power_l2")]
    [InlineData("pv_power")]
    public void Real_M1_refresh_follows_the_scan_interval_change_to_10s(string signal)
    {
        // The poll interval is detected, not configured: after the integration was reloaded with a
        // 10 s scan interval the estimate follows within the refresh window.
        Assert.Equal(10, LiveReplayRun.Refresh(run.Final, signal)!.Value.TotalSeconds, 0.5);
    }

    [Fact]
    public void Real_goe_power_refresh_is_1s_within_0_2s()
    {
        Assert.Equal(1, LiveReplayRun.Refresh(run.Final, "charger_power")!.Value.TotalSeconds, 0.2);
    }

    [Fact]
    public void Real_goe_current_with_repeats_is_still_1s()
    {
        // nrg_4 repeats often (median interval 2 s); the low quantile still finds the 1 s poll.
        Assert.Equal(1, LiveReplayRun.Refresh(run.Final, "charger_current")!.Value.TotalSeconds, 0.2);
    }

    [Fact]
    public void Default_threshold_rejects_under_1_percent_of_real_evaluations()
    {
        var balance = run.Final.Snapshot.Balance;
        var evaluations = balance.Sum(b => b.Evaluations);
        var rejections = balance.Sum(b => b.Rejections);

        Assert.True(evaluations >= 5_000, $"Only {evaluations} balance evaluations; the check would pass vacuously.");
        Assert.True(rejections < evaluations / 100.0, $"{rejections} rejections in {evaluations} evaluations.");
    }

    [Fact]
    public void A_sign_error_in_the_meter_mapping_is_caught_by_the_balance_check()
    {
        // The same recording with the meter power read as positive_import: import looks like export.
        var flipped = new LiveReplayRun(TestConfigurations.ExampleWithCharger.Replace("sign: positive_export", "sign: positive_import", StringComparison.Ordinal));

        var balance = flipped.Final.Snapshot.Balance;
        Assert.True(balance.Sum(b => b.Rejections) > balance.Sum(b => b.Evaluations) / 20.0, "A sign error must show up as balance rejections.");
    }
}
