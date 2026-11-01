using System.Collections.Immutable;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Signals;

/// <summary>
/// SIG-09: r(p) = net import + generation − controlled loads, the controlled loads being the
/// fresh measured power draws of declared devices on the phase (a model value, such as a heater's
/// rated power, is never used: a heater whose thermostat opened would then reject every true
/// sample). Phase current is never an input, so protection data is never touched. Only inputs
/// reported within max_skew of the triggering sample are compared; a device draw counts when it
/// was reported within max_skew of the trigger, or has been unchanged since before trigger −
/// max_skew, and is left out otherwise (leaving a term out can only make a sample pass). Device
/// draws never trigger the check and are never rejected by it. Only an impossible negative
/// residual is refused; a positive residual is foreign load, which is normal. Without a measured
/// generation (no inverter mapped, or an inverter asleep counted as zero) nothing is compared:
/// an export is then explained by production nobody measures, not by a broken sample.
/// The per-phase counters belong to the process, not to a document: they survive a reload.
/// </summary>
internal sealed class BalanceChecker
{
    private static readonly ImmutableArray<Phase> AllPhases = [Phase.L1, Phase.L2, Phase.L3];

    private readonly Dictionary<Phase, BalanceState> _balance = AllPhases.ToDictionary(p => p, _ => new BalanceState());

    /// <summary>The roles whose samples are checked against the balance.</summary>
    public static bool IsTrigger(SignalRole role) =>
        role is SignalRole.PhasePower or SignalRole.InverterPower or SignalRole.InverterPhaseCurrent;

    public PhaseBalance For(Phase phase) => _balance[phase].ToPhaseBalance(phase);

    /// <summary>Whether the sample that just became the accepted one of <paramref name="trigger"/> keeps the balance possible.</summary>
    public bool Accepts(SignalMap map, SignalRuntime trigger, DateTimeOffset now)
    {
        var generation = GenerationShares.Read(map, now);
        if (generation.InputTimes.IsEmpty)
        {
            return true;
        }

        var configuration = map.Configuration!;
        var parameters = configuration.Control.Signals.Balance;
        var triggerTime = trigger.Accepted!.SampleReported;
        var phases = trigger.Definition.Role == SignalRole.PhasePower
            ? [trigger.Definition.Phase!.Value]
            : configuration.Connection.Phases;
        var impossible = new List<Phase>();

        foreach (var phase in phases)
        {
            if (map.WithRole(SignalRole.PhasePower, phase) is not { } power
                || !SignalFreshness.IsFresh(power, now)
                || generation.For(phase) is not { } share)
            {
                continue;
            }

            var balance = _balance[phase];
            var inputs = generation.InputTimes.Append(power.Accepted!.Effective);
            if (inputs.Any(t => (t - triggerTime).Duration() > parameters.MaxSkew))
            {
                balance.SkippedMisaligned++;
                continue;
            }

            var residual = power.Magnitude() + share.Value - ControlledLoad(map, phase, triggerTime, parameters.MaxSkew, now);
            balance.Evaluations++;
            balance.LastEvaluated = now;
            balance.Residual = new Watts(residual);
            if (residual < -parameters.ResidualThreshold.Value)
            {
                impossible.Add(phase);
            }
        }

        foreach (var phase in impossible)
        {
            _balance[phase].Rejections++;
            _balance[phase].LastRejected = now;
        }

        return impossible.Count == 0;
    }

    /// <summary>Σ of the fresh device power draws on a phase that are aligned with the trigger (see <see cref="Accepts"/>).</summary>
    private static double ControlledLoad(SignalMap map, Phase phase, DateTimeOffset trigger, TimeSpan maxSkew, DateTimeOffset now)
    {
        var total = 0.0;
        foreach (var (_, drawPhase, name) in map.PowerDraws)
        {
            if (drawPhase != phase || map.ByName(name) is not { } draw || !SignalFreshness.IsFresh(draw, now))
            {
                continue;
            }

            var accepted = draw.Accepted!;
            var aligned = (accepted.Effective - trigger).Duration() <= maxSkew || accepted.SampleReported <= trigger - maxSkew;
            if (aligned)
            {
                total += draw.Magnitude();
            }
        }

        return total;
    }

    private sealed class BalanceState
    {
        public Watts? Residual { get; set; }

        public DateTimeOffset? LastEvaluated { get; set; }

        public DateTimeOffset? LastRejected { get; set; }

        public long Evaluations { get; set; }

        public long Rejections { get; set; }

        public long SkippedMisaligned { get; set; }

        public PhaseBalance ToPhaseBalance(Phase phase) =>
            new(phase, Residual, LastEvaluated, LastRejected, Evaluations, Rejections, SkippedMisaligned);
    }
}
