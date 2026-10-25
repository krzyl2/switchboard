using System.Collections.Immutable;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Domain.Devices;

/// <summary>The physical model of a device (DISP-01, 03 "Layer 1").</summary>
public enum DeviceType
{
    /// <summary>One relay: on or off.</summary>
    OnOff,

    /// <summary>Several relays switched as stages (N × P), each on its own phase or on all three.</summary>
    Stepped,

    /// <summary>A current setpoint within [min, max] in fixed steps (an EV charger's current number).</summary>
    Modulating,

    /// <summary>
    /// A bidirectional current setpoint (a battery inverter): positive charges, and charging is
    /// load. Dispatch and protection only ever target [0, max]; discharge is never commanded.
    /// </summary>
    Storage,
}

/// <summary>
/// One switchable stage. <see cref="Index"/> is the stage number, starting at 1;
/// <see cref="RatedCurrent"/> is drawn on every phase in <see cref="Phases"/> when the stage is on.
/// </summary>
public sealed record StageSpec(int Index, ImmutableArray<Phase> Phases, Amperes RatedCurrent);

/// <summary>The setpoint range of a modulating or storage device, already in amperes.</summary>
public sealed record ModulatingRange(Amperes Min, Amperes Max, Amperes Step);

/// <summary>
/// A declared device as protection sees it: phases, currents and shedding parameters, never an
/// entity ID or a power. A modulating or storage device has exactly one stage whose rated current
/// is its maximum setpoint.
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
    /// <summary>A modulating device with a declared stop actuator (a switch that disables it): it can be switched off.</summary>
    public bool HasStopActuator { get; init; }

    /// <summary>The phases on which a draw-current signal measures this device's own current.</summary>
    public ImmutableArray<Phase> CurrentDrawPhases { get; init; } = [];

    /// <summary>The downstream breaker group this device sits behind (CONN-03); null when none.</summary>
    public GroupName? CircuitGroup { get; init; }

    /// <summary>What dispatch may do beyond restoring what protection shed (<c>start:</c>); it changes no level, so it is not part of the device's identity.</summary>
    public StartPolicy Start { get; init; } = StartPolicy.RestoreOnly;

    /// <summary>
    /// An opaque fingerprint of the entities that command and report this device (actuators, stop
    /// actuator, state entities), computed by the configuration layer: re-pointing a device at
    /// another entity changes it, while no entity ID ever reaches protection.
    /// </summary>
    public long ActuatorRevision { get; init; }

    /// <summary>
    /// The applied state reports only on change (state_reports: on_change): it shows a commanded
    /// level within the reaction time, so an acknowledged command it does not show is unconfirmed.
    /// An age-judged state may take up to its max_age and is never held to that.
    /// </summary>
    public bool StateReportsOnChange { get; init; }

    /// <summary>What the shed order sorts by: the priority override, else the value per kWh (DISP-02).</summary>
    public double ShedKey => ShedPriority ?? ValuePerKwh;

    /// <summary>A modulating or storage device: its actuator is a current setpoint.</summary>
    public bool IsSetpoint => Type is DeviceType.Modulating or DeviceType.Storage;

    /// <summary>
    /// Relays switch off; a modulating device only through its stop actuator (its setpoint range
    /// excludes zero); a storage device is brought to zero charge, never switched off.
    /// </summary>
    public bool CanSwitchOff => Type is DeviceType.OnOff or DeviceType.Stepped || (Type == DeviceType.Modulating && HasStopActuator);

    /// <summary>The lowest setpoint anything targets: the range minimum, zero charge for storage (never discharge).</summary>
    public Amperes SetpointFloor => Type == DeviceType.Storage ? Amperes.Zero : Range?.Min ?? Amperes.Zero;

    /// <summary>Every phase any stage draws current on, in phase order.</summary>
    public ImmutableArray<Phase> Phases => [.. Stages.SelectMany(s => s.Phases).Distinct().Order()];

    public bool IsOn(Phase phase) => Stages.Any(s => s.Phases.Contains(phase));

    public bool HasCurrentDraw(Phase phase) => !CurrentDrawPhases.IsDefault && CurrentDrawPhases.Contains(phase);
}
