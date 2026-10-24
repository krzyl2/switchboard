using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Replay;

/// <summary>
/// The committed synthetic recording (tests/fixtures/replay/make-synthetic.py) played through the
/// whole pipeline: the same rules the live recording checks, with every situation at a known time,
/// so they hold on any machine.
/// </summary>
public sealed class SyntheticReplayTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ExportFrom = Start.AddMinutes(12);

    // The example without the charger's draw: the fixture's charger (16 A) is not part of the meter's
    // L1 load, so as a controlled-load term (SIG-09) it would make every L1 balance impossible. These
    // checks are about the meter's signals alone.
    private static string MeterOnly => TestConfigurations.ExampleWithoutDraw;

    [Fact]
    public void Detected_refresh_matches_synthetic_10s_meter_30s_inverter_and_2_5s_charger()
    {
        var harness = new ReplayHarness(ReplayFixtures.SyntheticData, TestConfigurations.ExampleWithCharger);

        harness.RunUntil(ReplayFixtures.SyntheticData.End);

        var signals = harness.Current.Snapshot.Signals.Where(s => s.Name is not null).ToDictionary(s => s.Name!.Value.Value);
        Assert.Equal(10, signals["grid_current_l1"].DetectedRefresh!.Value.TotalSeconds, 0.1);
        Assert.Equal(10, signals["grid_power_l3"].DetectedRefresh!.Value.TotalSeconds, 0.1);
        Assert.Equal(30, signals["pv_power"].DetectedRefresh!.Value.TotalSeconds, 0.1);
        Assert.Equal(2.5, signals["charger_current"].DetectedRefresh!.Value.TotalSeconds, 0.05);
        Assert.Equal(2.5, signals["charger_power"].DetectedRefresh!.Value.TotalSeconds, 0.05);
    }

    [Fact]
    public void Out_of_step_inverter_row_is_rejected()
    {
        var harness = new ReplayHarness(ReplayFixtures.SyntheticData, MeterOnly);

        harness.RunUntil(Start.AddMinutes(18).AddSeconds(1));

        var snapshot = harness.Current.Snapshot;
        var inverter = snapshot.Signals.Single(s => s.Name?.Value == "pv_power");
        Assert.Equal((SignalState.Fresh, SignalDetail.BalanceResidual, 1L), (inverter.State, inverter.Detail, inverter.RejectedCount));
        Assert.Equal(1, snapshot.Rejections.BalanceResidual);
        Assert.All(snapshot.Protection.Phases, p => Assert.True(p.DataValid, "Balance never touches protection data."));
        Assert.All(snapshot.Dispatch.Phases, p => Assert.True(p.DataValid, "The sample is rejected, never the decision: the held production stays in use."));
    }

    [Fact]
    public void Frozen_L2_current_goes_stale_within_max_age_and_recovers()
    {
        var frozenAt = Start.AddMinutes(8).AddMilliseconds(11);
        var harness = new ReplayHarness(ReplayFixtures.SyntheticData, MeterOnly);
        var observed = new Dictionary<DateTimeOffset, PhaseProtection>();

        harness.RunUntil(
            Start.AddMinutes(12),
            [frozenAt.AddSeconds(90).AddTicks(-1), frozenAt.AddSeconds(90), frozenAt.AddMinutes(3).AddTicks(-1), Start.AddMinutes(11).AddMilliseconds(12)],
            (at, diagnostics) => observed[at] = diagnostics.Snapshot.Protection.For(Phase.L2)!);

        Assert.True(observed[frozenAt.AddSeconds(90).AddTicks(-1)].DataValid);
        Assert.Equal(ProtectionInvalidReason.CurrentStale, observed[frozenAt.AddSeconds(90)].InvalidReason);
        Assert.Equal(ProtectionInvalidReason.CurrentStale, observed[frozenAt.AddMinutes(3).AddTicks(-1)].InvalidReason);
        Assert.True(observed[Start.AddMinutes(11).AddMilliseconds(12)].DataValid);
        Assert.True(harness.Current.Snapshot.Protection.IsDataValid(Phase.L1), "Only the frozen phase loses its data.");
    }

    [Fact]
    public void Synthetic_current_direction_follows_power_sign_in_import_and_export_periods()
    {
        var data = ReplayFixtures.SyntheticData;
        var harness = new ReplayHarness(data, MeterOnly);
        var checkpoints = data.Rows
            .Where(r => r.Entity.Value.StartsWith("sensor.solaredge_m1_ac_current_", StringComparison.Ordinal))
            .Select(r => r.Timestamp + TimeSpan.FromSeconds(2))
            .Distinct()
            .ToList();
        var (import, export, cautious, wrong) = (0, 0, 0, 0);

        harness.RunUntil(data.End, checkpoints, (at, diagnostics) =>
        {
            foreach (var phase in diagnostics.Snapshot.Protection.Phases.Where(p => p.Current is not null))
            {
                var expected = at < ExportFrom ? FlowDirection.Import : FlowDirection.Export;
                switch (phase.Current!.Direction)
                {
                    case FlowDirection.Unknown:
                        cautious++;
                        break;
                    case var direction when direction == expected:
                        _ = direction == FlowDirection.Import ? import++ : export++;
                        break;
                    default:
                        wrong++;
                        break;
                }
            }
        });

        Assert.True(import >= 100 && export >= 100, $"Only {import} import and {export} export resolutions; the check would pass vacuously.");
        Assert.Equal(0, wrong);
        Assert.True(cautious < (import + export) / 100.0, $"{cautious} cautious-mode readings out of {import + export + cautious}.");
    }

    [Fact]
    public void Unavailable_rows_make_the_signal_unavailable_and_dispatch_drops_it()
    {
        var harness = new ReplayHarness(ReplayFixtures.SyntheticData, MeterOnly);
        SignalReading? during = null;
        Switchboard.Domain.Units.Volts? voltageDuring = null;

        harness.RunUntil(
            Start.AddMinutes(22),
            [Start.AddMinutes(20).AddSeconds(30)],
            (_, diagnostics) =>
            {
                during = diagnostics.Snapshot.Signals.Single(s => s.Name?.Value == "grid_voltage_l3");
                voltageDuring = diagnostics.Snapshot.Dispatch.For(Phase.L3)!.Voltage;
            });

        Assert.Equal((SignalState.Unavailable, SignalDetail.StateUnavailable), (during!.State, during.Detail));
        Assert.Null(voltageDuring);
        Assert.Equal(SignalState.Fresh, harness.Current.Snapshot.Signals.Single(s => s.Name?.Value == "grid_voltage_l3").State);
    }
}
