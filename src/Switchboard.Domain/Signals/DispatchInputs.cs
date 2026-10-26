using System.Collections.Immutable;
using Switchboard.Domain.Balance;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Units;

namespace Switchboard.Domain.Signals;

public enum DispatchInvalidReason
{
    ConfigurationInvalid,
    SourceDisconnected,
    PowerUnmapped,
    PowerNotFresh,
    GenerationUnknown,
}

/// <summary>
/// Per-phase power picture for dispatch. Every field is null unless all the signals it is computed
/// from are fresh. <see cref="Load"/> is the house load on the phase, derived here from primary
/// measurements (net import plus the phase's share of local generation, SIG-08), never read from a
/// template sensor.
/// </summary>
public sealed record PhaseDispatch(
    Phase Phase,
    bool DataValid,
    DispatchInvalidReason? InvalidReason,
    Watts? NetImport,
    Watts? Generation,
    Watts? Load,
    Volts? Voltage)
{
    /// <summary>When the phase power in use was last reported: what a settling window waits for (DISP-05).</summary>
    public DateTimeOffset? PowerReportedAt { get; init; }
}

/// <summary>
/// The meter's energy counters for the hourly balance (BAL-01). <see cref="Counters"/> is null unless
/// both are fresh; <see cref="MaxAge"/> is the longer a counter may be silent before it counts as stale.
/// </summary>
public sealed record EnergyInputs(EnergyCounters? Counters, TimeSpan MaxAge);

/// <summary>
/// Everything dispatch may read from the signal layer (SIG-04): power, voltage and energy counters, never current.
/// Headroom derived from the measured phase current reaches dispatch only through the guard's
/// envelope, never through this type.
/// </summary>
public sealed record DispatchInputs(long Sequence, DateTimeOffset TakenAt, ImmutableArray<PhaseDispatch> Phases)
{
    /// <summary>The declared power draws of devices, per phase; a stale draw carries no value.</summary>
    public ImmutableArray<DevicePowerSample> DevicePowers { get; init; } = [];

    /// <summary>The energy counters of the hourly balance; null when none are mapped.</summary>
    public EnergyInputs? Energy { get; init; }

    public PhaseDispatch? For(Phase phase) => Phases.FirstOrDefault(p => p.Phase == phase);
}
