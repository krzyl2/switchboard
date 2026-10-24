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
            [.. configuration.Devices.Select(d => d.ToSpec())]);
    }
}
