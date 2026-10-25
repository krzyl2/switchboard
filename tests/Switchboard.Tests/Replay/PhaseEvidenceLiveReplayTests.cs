using System.Collections.Immutable;
using System.Globalization;
using Switchboard.Application.Configuration;
using Switchboard.Application.Setup.SelfTest;
using Switchboard.Application.Signals;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Infrastructure.Replay;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Replay;

/// <summary>
/// The passive self-test's production rules (StepWindow, PhaseStepClassifier, PhaseEvidence) over
/// the 72 h recorded at the maintainer's house (gitignored; fails loudly when absent), the way the
/// guided watch reads report series: every switching of a declared heater relay by Node-RED, the
/// meter's per-phase power around it. The example's mapping is confirmed with no mismatch, and the
/// 0.3.x mapping (the two elements swapped) is reported as a mismatch on the phase that rose.
/// </summary>
[Trait("Category", "LiveReplay")]
public sealed class PhaseEvidenceLiveReplayTests(ITestOutputHelper output)
{
    [Fact]
    public void Recorded_heater_switchings_confirm_example_mapping()
    {
        var verdicts = Judge(TestConfigurations.Example);
        foreach (var (entity, judgement) in verdicts)
        {
            output.WriteLine($"{entity}: {judgement.Summary}");
            foreach (var observation in judgement.Observations)
            {
                output.WriteLine($"  {observation.Observation.At:O} {(observation.Observation.Direction > 0 ? "on" : "off")}: {observation.Result.Verdict} {observation.Result.Observed} {observation.Result.Reason} {string.Join(", ", observation.Result.Deltas.OrderBy(d => d.Key).Select(d => string.Create(CultureInfo.InvariantCulture, $"{d.Key} {d.Value:+0;-0} W")))}");
            }
        }

        // Not vacuous: several clean switchings of the L1 and L2 elements confirm their phases. One
        // switching of switch.grzalka_1 (off at 2026-10-23 09:00:01) coincides with a 1.3 kW drop on
        // L1 and reads as a single mismatch; the confirmations dominate, so nothing is refused.
        Assert.True(verdicts.Values.Sum(v => v.Summary.Confirmed) >= 5, "Fewer than five confirmed switchings: the check would pass vacuously.");
        Assert.All(verdicts.Values, v => Assert.NotEqual(PhaseStepVerdict.Mismatch, v.Summary.Verdict));
        Assert.Equal(PhaseStepVerdict.Confirmed, verdicts["switch.grzalka_1"].Summary.Verdict);
        Assert.Equal(PhaseStepVerdict.Confirmed, verdicts["switch.grzalka_2"].Summary.Verdict);
    }

    [Fact]
    public void The_0_3_x_mapping_is_reported_mismatch_on_the_phase_that_rose()
    {
        var swapped = TestConfigurations.Example.ReplaceLineEndings("\n")
            .Replace("actuator: switch.grzalka_2 ", "actuator: switch.grzalka_X ", StringComparison.Ordinal)
            .Replace("actuator: switch.grzalka_1 ", "actuator: switch.grzalka_2 ", StringComparison.Ordinal)
            .Replace("actuator: switch.grzalka_X ", "actuator: switch.grzalka_1 ", StringComparison.Ordinal);

        var verdicts = Judge(swapped);

        Assert.Equal((PhaseStepVerdict.Mismatch, Phase.L2), (verdicts["switch.grzalka_1"].Summary.Verdict, verdicts["switch.grzalka_1"].Summary.Observed));
        Assert.Equal((PhaseStepVerdict.Mismatch, Phase.L1), (verdicts["switch.grzalka_2"].Summary.Verdict, verdicts["switch.grzalka_2"].Summary.Observed));
    }

    private static Dictionary<string, PhaseJudgement> Judge(string yaml)
    {
        var data = ReplayFixtures.LiveData;
        var configuration = TestConfigurations.Valid(yaml).Configuration!;
        var defaults = SetupFixtures.Defaults;
        var lag = configuration.Control.Signals.CurrentLagPolls;
        var refreshParameters = configuration.Control.Signals.Refresh;
        var terms = configuration.Signals.Where(s => s.Role == SignalRole.PhasePower && s.Phase is not null)
            .ToImmutableDictionary(s => s.Phase!.Value, s => new MeterTerm(s.Entity, s.Unit!, MeterValues.SignName(s.Sign)));
        var series = terms.ToDictionary(t => t.Key, t => data.Rows.Where(r => r.Entity == t.Value.Entity)
            .Select(r => (r.Timestamp, Value: MeterValues.Read(r.State, t.Value)))
            .Where(p => p.Value is not null)
            .Select(p => new ReportPoint(p.Timestamp, p.Value!.Value))
            .ToList());
        var relays = configuration.Devices.Where(d => d.Type == DeviceType.OnOff && d.Stages.Length == 1 && d.Stages[0].Phases.Length == 1).ToList();
        var transitions = relays
            .SelectMany(d => Transitions(data, d.Stages[0].StateEntity).Select(t => (Device: d, t.At, t.Direction)))
            .OrderBy(t => t.At)
            .ToList();
        var charger = configuration.Signals.Where(s => s.Role == SignalRole.DeviceCurrent).Select(s => s.Entity).ToHashSet();
        var draws = data.Rows.Where(r => charger.Contains(r.Entity))
            .Select(r => (r.Timestamp, Value: double.TryParse(r.State, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN))
            .Where(p => !double.IsNaN(p.Value))
            .Select(p => new ReportPoint(p.Timestamp, p.Value))
            .ToList();
        var sigmaA = configuration.Control.Signals.DefaultSigma[SignalKind.Current];

        var store = new PhaseEvidenceStore(1000);
        foreach (var (device, at, direction) in transitions)
        {
            var stage = device.Stages[0];
            var phaseSeries = series[stage.Phases[0]];
            var before = phaseSeries.Where(p => p.At <= at).TakeLast(refreshParameters.WindowIntervals + 1).ToList();
            var refresh = RefreshInterval.Estimate(before.Zip(before.Skip(1), (a, b) => b.At - a.At).ToList(), refreshParameters.Quantile, refreshParameters.MinIntervals);
            if (StepWindow.Window(defaults.SelfTest.Isolation, lag, refresh) is not { } window)
            {
                continue;
            }

            var quietA = Math.Max(defaults.SelfTest.Classifier.QuietMaxFraction * stage.RatedCurrent.Value, defaults.SelfTest.Classifier.SigmaMultiplier * sigmaA);
            var isolated = !transitions.Any(t => t.Device != device && (t.At - at).Duration() < window) && !StepWindow.Changed(draws, at, window, quietA);
            var phases = series.ToImmutableDictionary(s => s.Key, s => StepWindow.Measure(s.Value, at, window, lag, configuration.Control.Signals.DefaultSigma[SignalKind.Power]));
            store.Add(new PhaseObservation(at, direction, stage.StateEntity, ObservationSource.Recorded, "W", false, false, terms, phases, isolated, null));
        }

        return relays.ToDictionary(
            d => d.Stages[0].StateEntity.Value,
            d => PhaseEvidence.Judge(store.For(d.Stages[0].StateEntity), d.Stages[0].Phases[0], d.Stages[0].RatedPower?.Value, d.Stages[0].RatedCurrent.Value, terms, null, defaults.SelfTest.Classifier));
    }

    private static IEnumerable<(DateTimeOffset At, int Direction)> Transitions(ReplayData data, EntityId entity)
    {
        string? previous = null;
        foreach (var row in data.Rows.Where(r => r.Entity == entity && r.State is "on" or "off"))
        {
            if (previous is not null && previous != row.State)
            {
                yield return (row.Timestamp, row.State == "on" ? 1 : -1);
            }

            previous = row.State;
        }
    }
}
