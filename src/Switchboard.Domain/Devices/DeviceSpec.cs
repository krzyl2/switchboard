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

/// <summary>
/// The setpoint range of a modulating or storage device, already in amperes: finite, with a step
/// above zero and a maximum that is not below the minimum, because every level is counted in steps.
/// </summary>
public sealed record ModulatingRange
{
    public ModulatingRange(Amperes min, Amperes max, Amperes step)
    {
        if (!double.IsFinite(min.Value) || !double.IsFinite(max.Value) || !double.IsFinite(step.Value))
        {
            throw new ArgumentException("A setpoint range is made of finite currents.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(step.Value, nameof(step));
        ArgumentOutOfRangeException.ThrowIfLessThan(max.Value, min.Value, nameof(max));
        Min = min;
        Max = max;
        Step = step;
    }

    public Amperes Min { get; }

    public Amperes Max { get; }

    public Amperes Step { get; }
}

/// <summary>
/// A declared device as protection sees it: phases, currents and shedding parameters, never an
/// entity ID or a power. A device is either a <see cref="RelaySpec"/> (one or more relay stages) or a
/// <see cref="SetpointSpec"/> (a current setpoint with exactly one stage whose rated current is its
/// maximum); <see cref="Type"/> stays the configuration's enum and names the kind within that.
/// </summary>
public abstract record DeviceSpec
{
    private protected DeviceSpec(
        DeviceName id,
        DeviceType type,
        ImmutableArray<StageSpec> stages,
        double valuePerKwh,
        double? shedPriority,
        TimeSpan reactionTime,
        bool noShed,
        TimeSpan minOn,
        TimeSpan minOff)
    {
        Id = id;
        Type = type;
        Stages = stages;
        ValuePerKwh = valuePerKwh;
        ShedPriority = shedPriority;
        ReactionTime = reactionTime;
        NoShed = noShed;
        MinOn = minOn;
        MinOff = minOff;
    }

    public DeviceName Id { get; init; }

    /// <summary>The configuration's device type; it never changes a device's kind, which its subtype fixes.</summary>
    public DeviceType Type { get; }

    public ImmutableArray<StageSpec> Stages { get; init; }

    public double ValuePerKwh { get; init; }

    /// <summary>Replaces <see cref="ValuePerKwh"/> as the shed key when set (DISP-02).</summary>
    public double? ShedPriority { get; init; }

    /// <summary>From a command to the load actually changing; the shorter one sheds first on a tie.</summary>
    public TimeSpan ReactionTime { get; init; }

    /// <summary>A device that physically cannot stop: never a shedding candidate (04 §2).</summary>
    public bool NoShed { get; init; }

    public TimeSpan MinOn { get; init; }

    public TimeSpan MinOff { get; init; }

    /// <summary>The phases on which a draw-current signal measures this device's own current.</summary>
    public ImmutableArray<Phase> CurrentDrawPhases { get; init; } = [];

    /// <summary>The downstream breaker group this device sits behind (CONN-03); null when none.</summary>
    public GroupName? CircuitGroup { get; init; }

    /// <summary>
    /// What dispatch may do beyond restoring what protection shed (<c>start:</c>); it changes no level,
    /// so it is not part of the device's identity. Always set by the configuration layer from
    /// devices.default_start or the device's own key: there is no fallback here.
    /// </summary>
    public required StartPolicy Start { get; init; }

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

    /// <summary>
    /// Relays switch off; a modulating device only through its stop actuator (its setpoint range
    /// excludes zero); a storage device is brought to zero charge, never switched off.
    /// </summary>
    public abstract bool CanSwitchOff { get; }

    /// <summary>Every phase any stage draws current on, in phase order.</summary>
    public ImmutableArray<Phase> Phases => [.. Stages.SelectMany(s => s.Phases).Distinct().Order()];

    public bool IsOn(Phase phase) => Stages.Any(s => s.Phases.Contains(phase));

    public bool HasCurrentDraw(Phase phase) => !CurrentDrawPhases.IsDefault && CurrentDrawPhases.Contains(phase);
}

/// <summary>A device that switches relays: one stage (<see cref="DeviceType.OnOff"/>) or several (<see cref="DeviceType.Stepped"/>).</summary>
public sealed record RelaySpec : DeviceSpec
{
    public RelaySpec(
        DeviceName id,
        DeviceType type,
        ImmutableArray<StageSpec> stages,
        double valuePerKwh,
        double? shedPriority,
        TimeSpan reactionTime,
        bool noShed,
        TimeSpan minOn,
        TimeSpan minOff)
        : base(id, type is DeviceType.OnOff or DeviceType.Stepped ? type : throw new ArgumentException($"A relay device is onoff or stepped, not {type}.", nameof(type)), stages, valuePerKwh, shedPriority, reactionTime, noShed, minOn, minOff)
    {
    }

    public override bool CanSwitchOff => true;
}

/// <summary>A device whose actuator is a current setpoint: <see cref="DeviceType.Modulating"/> or <see cref="DeviceType.Storage"/>.</summary>
public sealed record SetpointSpec : DeviceSpec
{
    public SetpointSpec(
        DeviceName id,
        DeviceType type,
        ImmutableArray<StageSpec> stages,
        ModulatingRange range,
        double valuePerKwh,
        double? shedPriority,
        TimeSpan reactionTime,
        bool noShed,
        TimeSpan minOn,
        TimeSpan minOff)
        : base(id, type is DeviceType.Modulating or DeviceType.Storage ? type : throw new ArgumentException($"A setpoint device is modulating or storage, not {type}.", nameof(type)), stages, valuePerKwh, shedPriority, reactionTime, noShed, minOn, minOff)
    {
        Range = range ?? throw new ArgumentNullException(nameof(range));
    }

    public ModulatingRange Range { get; init; }

    /// <summary>A modulating device with a declared stop actuator (a switch, or a select, that disables it): it can be switched off.</summary>
    public bool HasStopActuator { get; init; }

    /// <summary>The stop and resume options when the stop actuator is a select; null for a switch (or no stop actuator).</summary>
    public StopOptions? StopOptions { get; init; }

    public override bool CanSwitchOff => Type == DeviceType.Modulating && HasStopActuator;

    /// <summary>The lowest setpoint anything targets: the range minimum, zero charge for storage (never discharge).</summary>
    public Amperes SetpointFloor => Type == DeviceType.Storage ? Amperes.Zero : Range.Min;
}
