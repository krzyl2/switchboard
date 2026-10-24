using System.Collections.Immutable;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Domain.Guard;

/// <summary>Cycle and persistence parameters the guard reacts on (04 §7); bound from the control defaults file.</summary>
/// <param name="Cycle">Cycle boundaries are multiples of this since the Unix epoch.</param>
/// <param name="PersistenceDownCycles">Boundaries a reduce_ordinary_cycle band (or a reserve request) must persist before acting.</param>
/// <param name="PersistenceUpCycles">Boundaries of good conditions before a limit is released.</param>
/// <param name="MaxStepChangeStages">Ordinary stage changes per device per cycle; protective ones are never limited.</param>
public sealed record GuardTiming(TimeSpan Cycle, int PersistenceDownCycles, int PersistenceUpCycles, int MaxStepChangeStages);

/// <summary>
/// Everything the guard needs from the configuration, in amperes only (SIG-04): the breaker, the
/// reserve per phase already converted to current by the signal layer, the budget thresholds, the
/// timing and the declared devices by logical name. Never a watt, a volt or an entity ID.
/// </summary>
/// <param name="CurrentMaxAge">max_age of the current signal protecting each phase: the budget is never projected past it.</param>
public sealed record ProtectionSetup(
    BreakerProfile Breaker,
    ImmutableArray<Phase> Phases,
    ImmutableDictionary<Phase, Amperes> ReserveCurrent,
    ImmutableDictionary<Phase, TimeSpan> CurrentMaxAge,
    BudgetParameters Budget,
    GuardTiming Timing,
    ImmutableArray<DeviceSpec> Devices)
{
    /// <summary>The circuit groups (CONN-03), each with its own breaker, reserve and members.</summary>
    public ImmutableArray<CircuitGroupSpec> Groups { get; init; } = [];

    /// <summary>In − reserve: above it the guard asks dispatch for a reduction (04 §2 step 4).</summary>
    public Amperes ReserveThreshold(Phase phase) => Breaker.Rating - ReserveCurrent.GetValueOrDefault(phase);

    public DeviceSpec? Device(DeviceName name) => Devices.FirstOrDefault(d => d.Id == name);

    public CircuitGroupSpec? Group(GroupName name) => Groups.FirstOrDefault(g => g.Id == name);
}
