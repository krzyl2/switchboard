using Switchboard.Application.Configuration;
using Switchboard.Application.Decisions;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Replay;

/// <summary>
/// VER-04 with the dispatcher: guard, dispatch and effector over the 72 h recorded at the
/// maintainer's installation (gitignored; fails loudly when absent), in shadow mode, with the
/// committed example configuration as the live add-on gets it: heater relays declared on_change in
/// a rotation group, on the meter phases their switching moves, the charger with its own draw
/// (nrg_4 current in A, nrg_7 power in W). Protection must miss no violation (measured current,
/// independent of the load model), dispatch must cause none and never plan an increase into less
/// headroom than its start hysteresis or past the guard, and no intent may repeat or chatter. The
/// shadow numbers are written to the test output for the maintainer's review.
/// </summary>
[Trait("Category", "LiveReplay")]
public sealed class DispatchLiveReplayTests(ITestOutputHelper output)
{
    /// <summary>The committed example: on_change heaters in a rotation group, the charger with its draw (nrg_4 in A, nrg_7 in W).</summary>
    internal static string Stage3Example => TestConfigurations.Example;

    [Fact]
    public void Live_72h_causes_and_misses_no_violation_and_repeats_no_intent()
    {
        var data = ReplayFixtures.LiveData;
        var harness = new GuardReplayHarness(data, Stage3Example, dispatch: true);
        harness.RunUntil(data.End);
        var configuration = TestConfigurations.Valid(Stage3Example).Configuration!;
        var protection = ProtectionSetupFactory.Build(configuration);
        var violations = new ViolationAnalyzer(protection, harness.Observations, harness.Decisions, harness.Steps);
        var dispatch = new DispatchAnalyzer(harness.Steps, harness.Decisions, DispatchSetupFactory.Build(configuration), protection);

        var missed = violations.MissedViolations();
        var silent = violations.SilentActions();
        var planned = dispatch.IncreasesIntoTooLittleHeadroom();
        var against = dispatch.IncreasesAgainstTheGuardOrAWindow();
        var repeated = dispatch.RepeatedIntents();
        var chatter = dispatch.Chatter();
        var caused = dispatch.CausedViolations();

        output.WriteLine($"Replayed {data.Start:O} to {data.End:O}: {harness.Steps.Count} steps, {harness.Decisions.Count} decisions.");
        foreach (var group in harness.Decisions.Where(d => d.Scope == DecisionScope.Device).GroupBy(d => $"{d.Device} {d.Origin} {d.Action} {ReasonCodes.Name(d.Reason)}{(d.RefusedBy is { } r && r != ReasonCode.ModeShadow ? " refused " + ReasonCodes.Name(r) : string.Empty)}").OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            output.WriteLine($"device decision: {group.Key}: {group.Count()}");
        }

        foreach (var group in harness.Decisions.Where(d => d.Scope != DecisionScope.Device).GroupBy(d => $"{d.Scope} {d.Phase} {d.Action} {ReasonCodes.Name(d.Reason)}").OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            output.WriteLine($"event: {group.Key}: {group.Count()}");
        }

        foreach (var line in dispatch.Report())
        {
            output.WriteLine(line);
        }

        foreach (var line in missed.Concat(silent).Concat(planned).Concat(against).Concat(repeated).Concat(chatter).Concat(caused))
        {
            output.WriteLine(line);
        }

        Assert.True(harness.Steps.Count > 10_000, $"Only {harness.Steps.Count} steps; the check would pass vacuously.");
        Assert.Empty(missed);
        Assert.Empty(silent);
        Assert.Empty(planned);
        Assert.Empty(against);
        Assert.Empty(repeated);
        Assert.Empty(chatter);
        Assert.Empty(caused);
        Assert.All(harness.Decisions, d => Assert.False(d.Commanded));
        Assert.Contains(harness.Decisions, d => d.Origin == DecisionOrigin.Dispatch);
        Assert.Contains(dispatch.Changes(), c => c.Increase);
    }

    [Fact]
    public void Charger_draw_as_a_controlled_load_rejects_under_1_percent_of_real_balance_evaluations()
    {
        // SIG-09 device terms on real data: the charger's own power (nrg_7, every 1 to 3 s) is
        // subtracted from the meter's house load (10 to 30 s). It must not make real samples look
        // impossible: a draw counts only when reported within max_skew of the meter sample.
        var data = ReplayFixtures.LiveData;
        var harness = new ReplayHarness(data, Stage3Example);
        harness.RunUntil(data.End);

        var balance = harness.Current.Snapshot.Balance;
        var evaluations = balance.Sum(b => b.Evaluations);
        var rejections = balance.Sum(b => b.Rejections);
        output.WriteLine($"{rejections} rejections in {evaluations} evaluations ({string.Join(", ", balance.Select(b => $"{b.Phase}: {b.Rejections}/{b.Evaluations}"))}).");

        Assert.True(evaluations >= 5_000, $"Only {evaluations} balance evaluations; the check would pass vacuously.");
        Assert.True(rejections < evaluations / 100.0, $"{rejections} rejections in {evaluations} evaluations.");
        Assert.Contains(harness.Current.Snapshot.Signals, s => s.Name?.Value.StartsWith("device_ev_charger_power", StringComparison.Ordinal) == true);
        Assert.All(harness.Current.Snapshot.Signals.Where(s => s.Name?.Value.StartsWith("device_ev_charger", StringComparison.Ordinal) == true), s => Assert.NotEqual(SignalState.Unavailable, s.State));
    }
}
