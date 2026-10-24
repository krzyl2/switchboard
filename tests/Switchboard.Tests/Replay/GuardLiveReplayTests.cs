using System.Globalization;
using Switchboard.Application.Configuration;
using Switchboard.Application.Decisions;
using Switchboard.Domain.Decisions;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Replay;

/// <summary>
/// VER-04 on real data: the guard over the 72 h recorded at the maintainer's installation
/// (gitignored; fails loudly when absent), with the maintainer's example configuration: heater
/// elements switch.grzalka_1/2/3 (1.5 kW, one per phase) and the go-e charger number.goe_422752_amp
/// (6 to 20 A, on L1), their applied states from the recording. Shadow mode: the guard only records.
/// </summary>
[Trait("Category", "LiveReplay")]
public sealed class GuardLiveReplayTests(ITestOutputHelper output)
{
    [Fact]
    public void Live_recording_no_missed_violation_and_reports_would_shed_counts()
    {
        var data = ReplayFixtures.LiveData;
        var harness = new GuardReplayHarness(data, TestConfigurations.Example);
        harness.RunUntil(data.End);
        var setup = ProtectionSetupFactory.Build(TestConfigurations.Valid(TestConfigurations.Example).Configuration!);
        var analyzer = new ViolationAnalyzer(setup, harness.Observations, harness.Decisions, harness.Steps);

        var missed = analyzer.MissedViolations();
        var unjustified = analyzer.ShedsWithoutOverload();
        var silent = analyzer.SilentActions();

        output.WriteLine($"Replayed {data.Start:O} to {data.End:O}: {harness.Steps.Count} guard steps, {harness.Decisions.Count} decisions.");
        foreach (var (key, count) in analyzer.WouldShedCounts())
        {
            output.WriteLine($"would shed: {key}: {count}");
        }

        foreach (var (phase, (budget, at)) in analyzer.BudgetMinimum())
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"budget minimum {phase}: {budget:0.000} at {at:O}"));
        }

        foreach (var group in harness.Decisions.Where(d => d.Scope == DecisionScope.Device).GroupBy(d => $"{d.Device} {d.Action} {ReasonCodes.Name(d.Reason)}").OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            output.WriteLine($"device decision: {group.Key}: {group.Count()}");
        }

        foreach (var group in harness.Decisions.Where(d => d.Scope != DecisionScope.Device).GroupBy(d => $"{d.Scope} {d.Phase} {d.Action} {ReasonCodes.Name(d.Reason)}").OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            output.WriteLine($"event: {group.Key}: {group.Count()}");
        }

        foreach (var line in missed.Concat(unjustified).Concat(silent))
        {
            output.WriteLine(line);
        }

        Assert.True(harness.Steps.Count > 10_000, $"Only {harness.Steps.Count} guard steps; the check would pass vacuously.");
        Assert.Empty(missed);
        Assert.Empty(unjustified);
        Assert.Empty(silent);
        Assert.All(harness.Decisions, d => Assert.False(d.Commanded));

        // The recording holds one real overload on L1 (peak 30.35 A, above 1.13 × In for minutes):
        // the guard must have reacted to it on L1, and never on L2 or L3.
        Assert.Contains(harness.Decisions, d => d.Phase == Switchboard.Domain.Signals.Phase.L1 && d.Action is DecisionAction.SwitchOff or DecisionAction.ReduceToMinimum && d.Reason is ReasonCode.GuardTierExceeded or ReasonCode.GuardBudgetLow);
        Assert.DoesNotContain(harness.Decisions, d => d.Phase is Switchboard.Domain.Signals.Phase.L2 or Switchboard.Domain.Signals.Phase.L3 && d.Reason is ReasonCode.GuardTierExceeded or ReasonCode.GuardBudgetLow && d.Scope == DecisionScope.Device);
    }
}
