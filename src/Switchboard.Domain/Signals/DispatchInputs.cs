using System.Collections.Immutable;
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
    Volts? Voltage);

/// <summary>
/// Everything dispatch may read from the signal layer (SIG-04): power and voltage, never current.
/// Headroom derived from the measured phase current reaches dispatch only through the guard's
/// envelope, never through this type.
/// </summary>
public sealed record DispatchInputs(long Sequence, DateTimeOffset TakenAt, ImmutableArray<PhaseDispatch> Phases)
{
    public PhaseDispatch? For(Phase phase) => Phases.FirstOrDefault(p => p.Phase == phase);
}
