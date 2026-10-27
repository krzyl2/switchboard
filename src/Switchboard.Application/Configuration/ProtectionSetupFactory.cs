using System.Collections.Immutable;
using Switchboard.Application.Signals;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Configuration;

/// <summary>
/// Turns a valid configuration into what protection reads: amperes, durations and devices by
/// logical name. The reserve (configured in W) is converted to current here, through the signal
/// layer, which is the only place units change.
/// </summary>
public static class ProtectionSetupFactory
{
    public static ProtectionSetup Build(SwitchboardConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connection = configuration.Connection;
        var control = configuration.Control;
        var characteristic = connection.Characteristic;
        var breaker = new BreakerProfile(
            connection.BreakerRating,
            characteristic,
            control.Breaker.ContinuousMultiple[characteristic],
            control.Breaker.Tiers[characteristic]);

        var phases = connection.Phases;
        var summed = connection.Topology == Topology.ThreePhaseSummed;
        var maxAge = phases.ToImmutableDictionary(
            p => p,
            p => configuration.Signals.First(s => summed ? s.Role == SignalRole.TotalCurrent : s.Role == SignalRole.PhaseCurrent && s.Phase == p).MaxAge);

        return new ProtectionSetup(
            breaker,
            phases,
            phases.ToImmutableDictionary(p => p, p => SignalUnits.CurrentAt(connection.Reserve[p], connection.Voltage)),
            maxAge,
            control.Budget,
            new GuardTiming(control.Dispatch.Cycle, control.Dispatch.PersistenceDownCycles, control.Dispatch.PersistenceUpCycles, control.Dispatch.MaxStepChangeStages),
            [.. configuration.Devices.Select(d => d.ToSpec(configuration.VoltagePhases))])
        {
            Groups = [.. configuration.Groups.Select(g => Group(g, configuration, maxAge))],
        };
    }

    /// <summary>
    /// A circuit group through the same breaker model as the main breaker: its own rating and
    /// characteristic, the shipped tier table and continuous multiple of that characteristic (inside
    /// the same safety envelope), its reserve converted at the nominal voltage. An estimated group's
    /// current is recomputed with each main-meter sample, so it ages with the main phase current.
    /// </summary>
    private static CircuitGroupSpec Group(CircuitGroupDefinition group, SwitchboardConfiguration configuration, ImmutableDictionary<Phase, TimeSpan> mainMaxAge)
    {
        var control = configuration.Control;
        var breaker = new BreakerProfile(
            group.BreakerRating,
            group.Characteristic,
            control.Breaker.ContinuousMultiple[group.Characteristic],
            control.Breaker.Tiers[group.Characteristic]);
        return new CircuitGroupSpec(
            group.Id,
            breaker,
            group.Phases,
            group.Phases.ToImmutableDictionary(p => p, p => SignalUnits.CurrentAt(group.Reserve[p], configuration.Connection.Voltage)),
            group.Phases.ToImmutableDictionary(p => p, p => group.CurrentMaxAge ?? mainMaxAge[p]),
            !group.Current.IsEmpty,
            group.Members);
    }
}
