using System.Globalization;
using Switchboard.Application.Configuration;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Infrastructure.Replay;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Replay;

/// <summary>
/// 07-PITFALLS: a swapped phase assignment is the worst misconfiguration, because protection then
/// sheds a device on a phase it does not load. The recorded 72 h (gitignored; fails loudly when
/// absent) show which meter phase each heater relay really moves: every isolated switching of a
/// declared relay is correlated with the main meter's per-phase power step, and an element must be
/// declared on the phase that stepped. Elements whose thermostat was open (no step anywhere) and
/// switchings that coincide with other changes prove nothing and are only counted.
/// </summary>
[Trait("Category", "LiveReplay")]
public sealed class HeaterPhaseLiveReplayTests(ITestOutputHelper output)
{
    /// <summary>Other relay switchings or charger steps this close make an event not isolated.</summary>
    private static readonly TimeSpan Isolation = TimeSpan.FromSeconds(60);

    [Fact]
    public void Heater_relay_events_move_only_their_declared_phase()
    {
        var result = Correlate(ReplayFixtures.LiveData, TestConfigurations.Example);
        foreach (var line in result.Lines)
        {
            output.WriteLine(line);
        }

        // Not vacuous: the recording holds several clean switchings of the L1 and L2 elements.
        Assert.True(result.Confirmed >= 5, $"Only {result.Confirmed} clean switchings confirm a declared phase; the check would pass vacuously.");
        Assert.Empty(result.Mismatches);
    }

    [Fact]
    public void The_0_3_x_mapping_is_caught_as_swapped()
    {
        // The check must be able to fail: the example as 0.3.x declared it (switch.grzalka_1 on L1,
        // switch.grzalka_2 on L2) contradicts the recording on every clean switching of either.
        var swapped = TestConfigurations.Example.ReplaceLineEndings("\n")
            .Replace("actuator: switch.grzalka_2 ", "actuator: switch.grzalka_X ", StringComparison.Ordinal)
            .Replace("actuator: switch.grzalka_1 ", "actuator: switch.grzalka_2 ", StringComparison.Ordinal)
            .Replace("actuator: switch.grzalka_X ", "actuator: switch.grzalka_1 ", StringComparison.Ordinal);
        Assert.NotEqual(TestConfigurations.Example.ReplaceLineEndings("\n"), swapped);

        var result = Correlate(ReplayFixtures.LiveData, swapped);

        Assert.Contains(result.Mismatches, m => m.StartsWith("switch.grzalka_1 declared L1", StringComparison.Ordinal));
        Assert.Contains(result.Mismatches, m => m.StartsWith("switch.grzalka_2 declared L2", StringComparison.Ordinal));
    }

    private sealed record Correlation(int Confirmed, List<string> Mismatches, List<string> Lines);

    private static Correlation Correlate(ReplayData data, string yaml)
    {
        var configuration = TestConfigurations.Valid(yaml).Configuration!;
        var relays = configuration.Devices
            .Where(d => d.Type == DeviceType.OnOff && d.Stages.Length == 1 && d.Stages[0].Phases.Length == 1 && d.Stages[0].RatedPower is not null)
            .ToDictionary(d => d.Stages[0].Actuator.Value, d => (Phase: d.Stages[0].Phases[0], Rated: d.Stages[0].RatedPower!.Value.Value));
        var meter = configuration.Signals
            .Where(s => s.Role == SignalRole.PhasePower && s.Phase is not null)
            .ToDictionary(s => s.Phase!.Value, s => (Entity: s.Entity.Value, ImportSign: s.Sign == SignConvention.PositiveExport ? -1.0 : 1.0));
        var draws = configuration.Signals.Where(s => s.Role == SignalRole.DeviceCurrent).Select(s => s.Entity.Value).ToHashSet(StringComparer.Ordinal);

        var series = data.Rows
            .Where(r => meter.Values.Any(m => m.Entity == r.Entity.Value) || draws.Contains(r.Entity.Value))
            .Select(r => (Entity: r.Entity.Value, r.Timestamp, Value: double.TryParse(r.State, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN))
            .Where(r => !double.IsNaN(r.Value))
            .GroupBy(r => r.Entity)
            .ToDictionary(g => g.Key, g => g.Select(r => (r.Timestamp, r.Value)).ToList(), StringComparer.Ordinal);
        var transitions = new List<(string Entity, DateTimeOffset At, bool On)>();
        foreach (var relay in data.Rows.Where(r => relays.ContainsKey(r.Entity.Value)).GroupBy(r => r.Entity.Value))
        {
            string? previous = null;
            foreach (var row in relay.Where(r => r.State is "on" or "off"))
            {
                if (previous is not null && previous != row.State)
                {
                    transitions.Add((relay.Key, row.Timestamp, row.State == "on"));
                }

                previous = row.State;
            }
        }

        var confirmed = 0;
        var mismatches = new List<string>();
        var lines = new List<string>();
        foreach (var (entity, at, on) in transitions.OrderBy(t => t.At))
        {
            var (declared, rated) = relays[entity];
            var label = string.Create(CultureInfo.InvariantCulture, $"{entity} {(on ? "on" : "off")} at {at:O}");
            if (transitions.Any(t => t.Entity != entity && (t.At - at).Duration() <= Isolation) || draws.Any(d => Stepped(series, d, at)))
            {
                lines.Add($"{label}: not isolated");
                continue;
            }

            // The step on each phase between the last report before the switching and the second
            // after it (the meter may lag by a poll), as import: an element switched on adds import.
            var steps = meter.ToDictionary(m => m.Key, m => Step(series, m.Value.Entity, at) * m.Value.ImportSign);
            if (steps.Values.Any(s => s is null))
            {
                lines.Add($"{label}: meter not reporting");
                continue;
            }

            // A clean step is the element's own size (clean steps in the recording are 0.95 to 1.02 of
            // its rating); anything between that and quiet is something else changing too.
            var stepped = steps.Where(s => Math.Abs(s.Value!.Value) >= 0.8 * rated && Math.Abs(s.Value!.Value) <= 1.25 * rated).ToList();
            var quiet = steps.Count(s => Math.Abs(s.Value!.Value) <= 0.25 * rated);
            var text = string.Join(", ", steps.OrderBy(s => s.Key).Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Key} {s.Value:+0;-0} W")));
            if (stepped.Count == 0 && quiet == steps.Count)
            {
                lines.Add($"{label}: no step anywhere (thermostat open?): {text}");
            }
            else if (stepped.Count == 1 && quiet == steps.Count - 1 && stepped[0].Value > 0 == on)
            {
                if (stepped[0].Key == declared)
                {
                    confirmed++;
                    lines.Add($"{label}: confirms {declared}: {text}");
                }
                else
                {
                    mismatches.Add($"{entity} declared {declared} but moves {stepped[0].Key}: {(on ? "on" : "off")} at {at:O}: {text}");
                    lines.Add($"{label}: MOVES {stepped[0].Key}, DECLARED {declared}: {text}");
                }
            }
            else
            {
                lines.Add($"{label}: ambiguous: {text}");
            }
        }

        return new Correlation(confirmed, mismatches, lines);
    }

    /// <summary>The change of an entity from its last report at or before <paramref name="at"/> to its second report after it.</summary>
    private static double? Step(Dictionary<string, List<(DateTimeOffset At, double Value)>> series, string entity, DateTimeOffset at)
    {
        if (!series.TryGetValue(entity, out var points))
        {
            return null;
        }

        var before = points.LastOrDefault(p => p.At <= at);
        var after = points.Where(p => p.At > at).Skip(1).FirstOrDefault();
        return before == default || after == default || after.At - at > Isolation ? null : after.Value - before.Value;
    }

    /// <summary>A device draw that changed by more than 1 A around the switching (the charger stepping on L1).</summary>
    private static bool Stepped(Dictionary<string, List<(DateTimeOffset At, double Value)>> series, string entity, DateTimeOffset at) =>
        series.TryGetValue(entity, out var points)
        && points.Where(p => (p.At - at).Duration() <= Isolation).Select(p => p.Value)
            .Append(points.LastOrDefault(p => p.At <= at - Isolation).Value)
            .ToList() is { Count: > 0 } values
        && values.Max() - values.Min() > 1.0;
}
