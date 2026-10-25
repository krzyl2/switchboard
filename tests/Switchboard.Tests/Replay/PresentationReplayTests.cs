using System.Globalization;
using Switchboard.Application.Decisions;
using Switchboard.Application.Guard;
using Switchboard.Application.Presentation;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Signals;
using Switchboard.Infrastructure.Replay;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Replay;

/// <summary>
/// The runtime UI's read side over the committed synthetic morning (make-synthetic-dispatch.py)
/// with the maintainer's example configuration, in shadow mode, through the production guard,
/// dispatch and effector: every figure the dashboard shows is checked against the raw fixture
/// values of the same moment (not against the identity the builder computes), the exposure of
/// dispatch's "ours" against what foreign load detection really used, and the excursion record
/// against the morning's events.
/// </summary>
public sealed class PresentationDayRun
{
    public const string File = "synthetic-dispatch-day.csv";

    // Heater relays as the example maps them: heater_l1 = switch.grzalka_2, heater_l2 = grzalka_1, heater_l3 = grzalka_3.
    private static readonly string[] Heaters = ["switch.grzalka_1", "switch.grzalka_2", "switch.grzalka_3"];
    private static readonly string[] MeterPowers = ["sensor.solaredge_m1_ac_power_a", "sensor.solaredge_m1_ac_power_b", "sensor.solaredge_m1_ac_power_c"];
    private const string Inverter = "sensor.solaredge_ac_power";
    private const string ChargerPower = "sensor.goe_422752_nrg_7";
    private const double HeaterW = 1700;

    public PresentationDayRun()
    {
        Data = ReplayData.Load(RepoPaths.Combine("tests", "fixtures", "replay", File));
        Harness = new GuardReplayHarness(Data, TestConfigurations.Example);
        var rows = Data.Rows;
        var next = 0;
        var raw = new Dictionary<string, string>(StringComparer.Ordinal);
        var reported = new Dictionary<Phase, DateTimeOffset?>();
        Hub = TestStatistics.Hub(logger: Log.For<PresentationHub>());
        Harness.Runner.Stepped += (_, step) =>
        {
            Hub.Observe(step);
            var at = step.Evaluation.At;
            while (next < rows.Length && rows[next].Timestamp <= at)
            {
                raw[rows[next].Entity.Value] = rows[next].State;
                next++;
            }

            // The runner isolates its listeners (and logs to nothing here): a throw in the check
            // itself must show up as a mismatch, never vanish.
            try
            {
                Compare(step, Hub.Power!, raw, reported);
            }
            catch (Exception ex)
            {
                Mismatches.Add($"{at:HH:mm:ss.fff} the check threw: {ex}");
            }
        };
        Harness.RunUntil(Data.End);
    }

    internal ReplayData Data { get; }

    internal GuardReplayHarness Harness { get; }

    /// <summary>What the hub logged: it never throws to the guard, so a failure on some steps is visible only here.</summary>
    internal LogCapture Log { get; } = new();

    internal PresentationHub Hub { get; }

    internal IReadOnlyList<string> HubErrors => [.. Log.Entries.Where(e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Error).Select(e => e.Text)];

    internal List<string> Mismatches { get; } = [];

    internal int GenerationChecks { get; private set; }

    internal int ImportChecks { get; private set; }

    internal int ControlledChecks { get; private set; }

    internal int ExposureChecks { get; private set; }

    internal int StepsWithKnownBalance { get; private set; }

    /// <summary>A moment of the synthetic morning (2026-10-22, UTC).</summary>
    public static DateTimeOffset At(int hours, int minutes) => new(2026, 10, 22, hours, minutes, 0, TimeSpan.Zero);

    private void Compare(GuardStep step, PowerView view, Dictionary<string, string> raw, Dictionary<Phase, DateTimeOffset?> reported)
    {
        var at = step.Evaluation.At;
        var total = view.Total;
        if (total.Generation.Value is { } generation && total.GenerationNote is null && Number(raw, Inverter) is { } inverter)
        {
            GenerationChecks++;
            Check(at, "generation", inverter, generation);
        }

        if (total.NetImport.Value is { } net && MeterPowers.All(e => Number(raw, e) is not null))
        {
            // positive_export: a negative meter power is import.
            ImportChecks++;
            Check(at, "net import", -MeterPowers.Sum(e => Number(raw, e)!.Value), net);
        }

        if (total.Controlled.Value is { } controlled && Number(raw, ChargerPower) is { } charger)
        {
            ControlledChecks++;
            Check(at, "controlled", charger + (Heaters.Count(h => raw.GetValueOrDefault(h) == "on") * HeaterW), controlled);
        }

        if (view.Phases.All(p => p.Balance.Uncontrolled.Value is not null))
        {
            StepsWithKnownBalance++;
        }

        foreach (var phase in step.Dispatch?.Phases ?? [])
        {
            var input = step.Inputs?.Dispatch.For(phase.Phase);
            var advanced = input?.PowerReportedAt is { } now && (reported.GetValueOrDefault(phase.Phase) is not { } before || now > before);
            reported[phase.Phase] = input?.PowerReportedAt ?? reported.GetValueOrDefault(phase.Phase);
            if (advanced && input is { DataValid: true, Load: { } load } && phase.Ours is { } ours)
            {
                ExposureChecks++;
                Check(at, $"foreign {phase.Phase}", load.Value - ours.Value, phase.Foreign?.Value ?? double.NaN);
            }
        }
    }

    private void Check(DateTimeOffset at, string what, double expected, double actual)
    {
        if (!(Math.Abs(expected - actual) <= 1e-6))
        {
            Mismatches.Add(string.Create(CultureInfo.InvariantCulture, $"{at:HH:mm:ss.fff} {what}: fixture {expected}, view {actual}"));
        }
    }

    private static double? Number(Dictionary<string, string> raw, string entity) =>
        raw.TryGetValue(entity, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}

public sealed class PresentationReplayTests(PresentationDayRun run, ITestOutputHelper output) : IClassFixture<PresentationDayRun>
{
    [Fact]
    public void Parts_match_the_raw_fixture_values_on_every_step()
    {
        // Production = the inverter's own reading, grid = −Σ of the meter's phase powers, controlled =
        // the charger's own draw + 1700 W per element Home Assistant reports on: measured on the
        // fixture, independently of the builder. A balance identity cannot fail by construction; these can.
        foreach (var line in run.Mismatches.Take(20))
        {
            output.WriteLine(line);
        }

        Assert.Empty(run.Mismatches);
        Assert.Empty(run.HubErrors);
        Assert.True(run.GenerationChecks > 500, $"{run.GenerationChecks} generation checks");
        Assert.True(run.ImportChecks > 1000, $"{run.ImportChecks} import checks");
        Assert.True(run.ControlledChecks > 100, $"{run.ControlledChecks} controlled checks (the car charges 09:00-10:00)");
        Assert.True(run.StepsWithKnownBalance > 100, $"{run.StepsWithKnownBalance} steps with every part known");
    }

    [Fact]
    public void Exposed_controlled_power_is_what_foreign_load_was_computed_from()
    {
        // Read-only exposure fidelity: on every step where a phase power was newly reported, the
        // foreign load dispatch tracked is exactly that phase's load minus the exposed "ours", so the
        // dashboard's foreign segment and the decisions about foreign load can never disagree.
        Assert.DoesNotContain(run.Mismatches, m => m.Contains("foreign", StringComparison.Ordinal));
        Assert.True(run.ExposureChecks > 1000, $"{run.ExposureChecks} exposure checks");
    }

    [Fact]
    public void L1_excursion_at_0920_carries_the_would_shed()
    {
        // 09:00-10:00 the car charges on L1, and at 09:20 a kettle joins it: L1 is far above its
        // 14.13 A reserve threshold. The recorded excursion covers 09:20, peaks with the kettle, and
        // carries what Switchboard would have done about it (shadow: nothing switched).
        Assert.Empty(run.HubErrors);
        var phase = run.Hub.Excursions.Phases.Single(p => p.Phase == Phase.L1);
        foreach (var e in new[] { phase.Last, phase.Worst }.OfType<Excursion>())
        {
            output.WriteLine($"L1 excursion {e.Start:HH:mm:ss}-{e.End:HH:mm:ss} peak {e.Peak} at {e.PeakAt:HH:mm:ss}, {e.Samples.Length} samples, {e.Decisions.Length} decisions");
        }

        var excursion = new[] { phase.Last, phase.Worst }.First(e => e is not null && e.Start <= PresentationDayRun.At(9, 21) && (e.End ?? DateTimeOffset.MaxValue) >= PresentationDayRun.At(9, 22))!;

        foreach (var decision in excursion.Decisions)
        {
            output.WriteLine($"{decision.At:HH:mm:ss} {decision.Action} {ReasonCodes.Name(decision.Reason)} {decision.Device} commanded={decision.Commanded}");
        }

        Assert.InRange(excursion.PeakAt, PresentationDayRun.At(9, 20), PresentationDayRun.At(9, 23));
        Assert.True(excursion.Peak > excursion.ReserveThreshold, $"peak {excursion.Peak}");
        Assert.Contains(excursion.Decisions, d => d.Action == DecisionAction.RequestReduction);
        Assert.Contains(excursion.Decisions, d => d.Device is not null && !d.Commanded && d.Action is DecisionAction.SetSetpoint or DecisionAction.SwitchOff or DecisionAction.ReduceToMinimum or DecisionAction.Deferred);
        Assert.All(excursion.Decisions, d => Assert.False(d.Commanded));
    }

    [Fact]
    public void The_morning_is_integrated_and_counted_with_nothing_commanded()
    {
        // Five hours of data time: the buckets cover the morning, the would-sheds of the kettles and
        // the charging hour are counted, and nothing was commanded (shadow).
        Assert.Empty(run.HubErrors);
        var statistics = run.Hub.Statistics;
        var observed = statistics.Buckets.Values.Aggregate(TimeSpan.Zero, (sum, b) => sum + b.Observed);
        Assert.True(observed > TimeSpan.FromHours(4), $"observed {observed}");
        Assert.True(statistics.Lifetime.WouldSheds > 0, "no would-shed counted");
        Assert.Equal(0, statistics.Lifetime.Sheds);
        Assert.True(statistics.VoltageMeasured);
        Assert.Equal(0, statistics.Lifetime.VoltageLosses);
        Assert.True(statistics.Buckets.Values.Sum(b => b.Generation.Value) > 5, "the morning's PV");
    }
}

/// <summary>
/// The import/export fixture with a reserve high enough that both directions cross its magnitude
/// (5 kW: threshold 3.26 A): an importing phase above it raises a banner and an excursion, an
/// exporting one never does, because the guard compares the signed estimate.
/// </summary>
public sealed class PresentationExportTests
{
    [Fact]
    public void An_exporting_phase_raises_no_reserve_banner_and_no_reserve_excursion()
    {
        var yaml = TestConfigurations.Example.ReplaceLineEndings("\n").Replace("reserve_w: { L1: 2500, L2: 2500, L3: 2500 }", "reserve_w: { L1: 5000, L2: 5000, L3: 5000 }", StringComparison.Ordinal);
        Assert.NotEqual(TestConfigurations.Example.ReplaceLineEndings("\n"), yaml);
        var harness = new GuardReplayHarness(ReplayFixtures.SyntheticData, yaml);
        using var log = new LogCapture();
        var hub = TestStatistics.Hub(logger: log.For<PresentationHub>());
        var exportingAbove = 0;
        var importBanners = 0;
        var offenders = new List<string>();
        harness.Runner.Stepped += (_, step) =>
        {
            hub.Observe(step);
            foreach (var phase in hub.Power!.Phases)
            {
                var banner = hub.Power.Banners.Any(b => b.Phase == phase.Phase);
                if (phase.Direction == FlowDirection.Export)
                {
                    exportingAbove += phase.Current is { } current && current.Abs() > phase.ReserveThreshold ? 1 : 0;
                    if (banner || phase.OverReserve)
                    {
                        offenders.Add($"{step.Evaluation.At:HH:mm:ss} {phase.Phase} exports {phase.Current} with a banner");
                    }
                }
                else if (banner)
                {
                    importBanners++;
                }
            }
        };
        harness.RunUntil(ReplayFixtures.SyntheticData.End);

        Assert.DoesNotContain(log.Entries, e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Error);
        Assert.Empty(offenders);
        Assert.True(exportingAbove > 10, $"{exportingAbove} steps of export above the threshold magnitude: the check would pass vacuously");
        Assert.True(importBanners > 10, $"{importBanners} import banners: the check could not fail");
        Assert.All(
            hub.Excursions.Phases.SelectMany(p => new[] { p.Last, p.Worst }).OfType<Excursion>(),
            e => Assert.True(e.Samples.First(s => s.At >= e.Start).Current > e.ReserveThreshold, $"{e.Phase} excursion at {e.Start} starts on export"));
    }
}

/// <summary>The maintainer's 72 h (gitignored; fails loudly when absent): the 30.35 A peak on L1 is the worst excursion kept.</summary>
[Trait("Category", "LiveReplay")]
public sealed class PresentationLiveReplayTests(ITestOutputHelper output)
{
    [Fact]
    public void Live_L1_peak_30_35_A_is_recorded_with_guard_decisions()
    {
        var data = ReplayFixtures.LiveData;
        var harness = new GuardReplayHarness(data, TestConfigurations.Example);
        using var log = new LogCapture();
        var hub = TestStatistics.Hub(logger: log.For<PresentationHub>());
        harness.Runner.Stepped += (_, step) => hub.Observe(step);
        harness.RunUntil(data.End);

        Assert.DoesNotContain(log.Entries, e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Error);
        var worst = hub.Excursions.Phases.Single(p => p.Phase == Phase.L1).Worst!;
        output.WriteLine($"L1 worst: {worst.Start:O} to {worst.End:O}, peak {worst.Peak} at {worst.PeakAt:O}, {worst.Samples.Length} samples (decimated {worst.Decimated}), {worst.Decisions.Length} decisions");
        foreach (var decision in worst.Decisions)
        {
            output.WriteLine($"  {decision.At:O} {decision.Action} {ReasonCodes.Name(decision.Reason)} {decision.Device}");
        }

        Assert.Equal(30.35, worst.Peak.Value, 2);
        Assert.Contains(worst.Samples, s => s.At == worst.PeakAt);
        Assert.NotEmpty(worst.Decisions);
        Assert.True(worst.Samples.Length <= TestStatistics.Options().ExcursionMaxSamples);
    }
}
