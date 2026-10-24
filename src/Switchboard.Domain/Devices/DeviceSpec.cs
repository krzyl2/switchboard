using System.Collections.Immutable;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Domain.Devices;

/// <summary>The physical model of a device (DISP-01, 03 "Layer 1"); storage arrives with dispatch.</summary>
public enum DeviceType
{
    /// <summary>One relay: on or off.</summary>
    OnOff,

    /// <summary>Several relays switched as stages (N × P), each on its own phase or on all three.</summary>
    Stepped,

    /// <summary>A current setpoint within [min, max] in fixed steps (an EV charger's current number).</summary>
    Modulating,
}

/// <summary>
/// One switchable stage. <see cref="Index"/> is the stage number, starting at 1;
/// <see cref="RatedCurrent"/> is drawn on every phase in <see cref="Phases"/> when the stage is on.
/// </summary>
public sealed record StageSpec(int Index, ImmutableArray<Phase> Phases, Amperes RatedCurrent);

/// <summary>The setpoint range of a modulating device, already in amperes.</summary>
public sealed record ModulatingRange(Amperes Min, Amperes Max, Amperes Step);

/// <summary>
/// A declared device as protection sees it: phases, currents and shedding parameters, never an
/// entity ID or a power. A modulating device has exactly one stage whose rated current is its
/// maximum setpoint.
/// </summary>
/// <param name="ShedPriority">Replaces <paramref name="ValuePerKwh"/> as the shed key when set (DISP-02).</param>
/// <param name="ReactionTime">From a command to the load actually changing; the shorter one sheds first on a tie.</param>
/// <param name="NoShed">A device that physically cannot stop: never a shedding candidate (04 §2).</param>
public sealed record DeviceSpec(
    DeviceName Id,
    DeviceType Type,
    ImmutableArray<StageSpec> Stages,
    ModulatingRange? Range,
    double ValuePerKwh,
    double? ShedPriority,
    TimeSpan ReactionTime,
    bool NoShed,
    TimeSpan MinOn,
    TimeSpan MinOff)
{
    /// <summary>What the shed order sorts by: the priority override, else the value per kWh (DISP-02).</summary>
    public double ShedKey => ShedPriority ?? ValuePerKwh;

    /// <summary>
    /// False for a modulating device: its actuator is a setpoint whose range excludes zero, and no
    /// stop actuator exists before Stage 3. Protection still asks for Off; the record says it cannot happen.
    /// </summary>
    public bool CanSwitchOff => Type != DeviceType.Modulating;

    /// <summary>Every phase any stage draws current on, in phase order.</summary>
    public ImmutableArray<Phase> Phases => [.. Stages.SelectMany(s => s.Phases).Distinct().Order()];

    public bool IsOn(Phase phase) => Stages.Any(s => s.Phases.Contains(phase));
}
