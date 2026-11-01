using System.Collections.Immutable;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Signals;

/// <summary>Generation per phase (null when unknown) and the report times it was computed from.</summary>
internal sealed record GenerationShares(ImmutableDictionary<Phase, Watts>? Shares, ImmutableArray<DateTimeOffset> InputTimes)
{
    public static GenerationShares Unknown { get; } = new(null, []);

    /// <summary>How the shares came about, for the presentation only; nothing in control reads it.</summary>
    public GenerationState State { get; init; } = GenerationState.Measured;

    /// <summary>When the inverter last reported a usable value (presentation only).</summary>
    public DateTimeOffset? ReportedAt { get; init; }

    public static GenerationShares Known(ImmutableArray<Phase> phases, Func<Phase, double> share, IEnumerable<DateTimeOffset> times) =>
        new(phases.ToImmutableDictionary(p => p, p => new Watts(share(p))), [.. times]);

    public Watts? For(Phase phase) => Shares is not null && Shares.TryGetValue(phase, out var share) ? share : null;

    /// <summary>
    /// SIG-08: each phase's share of local generation, from primary measurements only. With the
    /// inverter's phase currents the production is split by them; without, it sits on the
    /// inverter's declared phase or is split equally. A mapped inverter that is not fresh makes the
    /// share unknown, never zero, unless it is declared unavailable_as_zero and is asleep: Home
    /// Assistant says unavailable, or it fell silent after reporting no production (SolarEdge
    /// reports 0 W at dusk and nothing more until morning). "unknown" (HA has no value yet, as after
    /// a restart) and silence after a production stay unknown. No inverter mapped means no generation.
    /// </summary>
    public static GenerationShares Read(SignalMap map, DateTimeOffset now)
    {
        var phases = map.Configuration!.Connection.Phases;
        if (map.WithRole(SignalRole.InverterPower) is not { } inverter)
        {
            return Known(phases, _ => 0, []) with { State = GenerationState.NotDeclared };
        }

        var (state, detail) = SignalFreshness.Classify(inverter, now);
        var reportedAt = inverter.Accepted?.Effective;
        double production;
        var times = new List<DateTimeOffset>();
        if (state == SignalState.Fresh)
        {
            production = inverter.Magnitude();
            times.Add(inverter.Accepted!.Effective);
        }
        else if (inverter.Definition.UnavailableAsZero
            && ((state == SignalState.Unavailable && detail == SignalDetail.StateUnavailable)
                || (state == SignalState.Stale && inverter.Accepted is not null && inverter.Magnitude() <= 0)))
        {
            return Known(phases, _ => 0, []) with { State = GenerationState.AsleepCountedAsZero, ReportedAt = reportedAt };
        }
        else
        {
            return Unknown with { State = GenerationState.Unknown, ReportedAt = reportedAt };
        }

        var currents = phases.Select(p => map.WithRole(SignalRole.InverterPhaseCurrent, p)).ToArray();
        if (currents.All(c => c is null))
        {
            return (inverter.Definition.Phase is { } own
                ? Known(phases, p => p == own ? production : 0, times)
                : Known(phases, _ => production / phases.Length, times)) with { ReportedAt = reportedAt };
        }

        if (production == 0)
        {
            return Known(phases, _ => 0, times) with { ReportedAt = reportedAt };
        }

        if (currents.Any(c => c is null || !SignalFreshness.IsFresh(c, now)))
        {
            return Unknown with { State = GenerationState.Unknown, ReportedAt = reportedAt };
        }

        var magnitudes = currents.Select(c => Math.Abs(c!.Magnitude())).ToArray();
        times.AddRange(currents.Select(c => c!.Accepted!.Effective));
        var sum = magnitudes.Sum();
        return Known(
            phases,
            p => sum == 0 ? production / phases.Length : production * magnitudes[phases.IndexOf(p)] / sum,
            times) with { ReportedAt = reportedAt };
    }
}
