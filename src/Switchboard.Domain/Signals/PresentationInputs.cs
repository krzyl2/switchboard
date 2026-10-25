using System.Collections.Immutable;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Units;

namespace Switchboard.Domain.Signals;

/// <summary>How the generation share of a store cycle came about (SIG-08), for display only.</summary>
public enum GenerationState
{
    /// <summary>A fresh inverter reading.</summary>
    Measured,

    /// <summary>An inverter declared unavailable_as_zero is asleep (unavailable, or silent after 0 W): counted as 0 W.</summary>
    AsleepCountedAsZero,

    /// <summary>No inverter_power signal is mapped: there is no generation to measure.</summary>
    NotDeclared,

    /// <summary>An inverter is mapped but its reading is not usable: house load is unknown.</summary>
    Unknown,
}

/// <summary>
/// What only the presentation needs from a store cycle, published with it in the same cycle and
/// carrying no entity ID: the nominal voltage (every display conversion between watts and amperes),
/// how generation was obtained, which phases have a voltage signal, and the devices' display names.
/// Protection, dispatch and the effector gate never read it.
/// </summary>
/// <param name="GenerationReportedAt">When the inverter last reported a usable value; null when none is mapped or none arrived.</param>
/// <param name="VoltageMapped">Per phase of the topology: whether a phase_voltage signal is declared.</param>
/// <param name="DisplayNames">The optional <c>name</c> of each device that declares one.</param>
public sealed record PresentationInputs(
    Volts NominalVoltage,
    GenerationState Generation,
    DateTimeOffset? GenerationReportedAt,
    ImmutableDictionary<Phase, bool> VoltageMapped,
    ImmutableDictionary<DeviceName, string> DisplayNames);
