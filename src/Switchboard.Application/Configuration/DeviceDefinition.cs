using System.Collections.Immutable;
using Switchboard.Application.Signals;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Configuration;

/// <summary>
/// One declared device as the configuration layer holds it: the only place where a device meets
/// entity IDs. <see cref="StageDefinition.Actuator"/> (and <see cref="StopActuator"/>) are the only
/// entities that may ever be commanded for it (SAFE-05); nothing in this version commands them.
/// Protection receives the <see cref="DeviceSpec"/> built from this, without entity IDs or watts.
/// </summary>
/// <param name="Index">The position under <c>devices:</c>, for issue paths (<c>devices[0]</c>).</param>
/// <param name="Range">A modulating or storage device's setpoint range, converted to amperes.</param>
/// <param name="RangeUnit">The unit the range and the actuator are written in (A or mA).</param>
/// <param name="StateMaxAge">max_age of the stage state signals (SIG-05); unused when the state reports on change.</param>
public sealed record DeviceDefinition(
    int Index,
    DeviceName Id,
    DeviceType Type,
    ImmutableArray<StageDefinition> Stages,
    ModulatingRange? Range,
    string? RangeUnit,
    double ValuePerKwh,
    double? ShedPriority,
    TimeSpan ReactionTime,
    bool NoShed,
    TimeSpan MinOn,
    TimeSpan MinOff,
    TimeSpan StateMaxAge)
{
    public string Path => $"devices[{Index}]";

    /// <summary>How the applied-state signals are judged fresh: by age (default) or, for a relay that reports only on change, by availability.</summary>
    public StateFreshness StateReports { get; init; } = StateFreshness.Age;

    /// <summary>A switch that disables a modulating device (its only way to reach zero); validated like an actuator.</summary>
    public EntityId? StopActuator { get; init; }

    /// <summary>The state signal reading <see cref="StopActuator"/> (<c>device_&lt;id&gt;_stop</c>).</summary>
    public SignalName? StopSignal { get; init; }

    /// <summary>The device's own measured draw, if declared.</summary>
    public DeviceDrawDefinition? Draw { get; init; }

    /// <summary>Identical elements on different phases, rotated by phase ranking (DISP-10).</summary>
    public GroupName? RotationGroup { get; init; }

    /// <summary>The circuit group the device sits behind (CONN-03).</summary>
    public GroupName? CircuitGroup { get; init; }

    public DeviceSpec ToSpec() => new(
        Id,
        Type,
        [.. Stages.Select(s => new StageSpec(s.Index, s.Phases, s.RatedCurrent))],
        Range,
        ValuePerKwh,
        ShedPriority,
        ReactionTime,
        NoShed,
        MinOn,
        MinOff)
    {
        HasStopActuator = StopActuator is not null,
        CurrentDrawPhases = Draw is null ? [] : [.. Draw.Current.Keys.Order()],
        CircuitGroup = CircuitGroup,
    };
}

/// <summary>
/// One stage of a device. <see cref="StateSignal"/> is the logical signal (role device_state) that
/// reads <see cref="StateEntity"/>, the stage's applied state, through the signal layer.
/// </summary>
/// <param name="Index">The stage number, starting at 1.</param>
/// <param name="RatedPower">Rated power of an onoff or stepped stage; consumed by dispatch, never by protection.</param>
public sealed record StageDefinition(
    int Index,
    EntityId Actuator,
    EntityId StateEntity,
    ImmutableArray<Phase> Phases,
    Amperes RatedCurrent,
    Watts? RatedPower,
    SignalName StateSignal);

/// <summary>
/// A device's own draw (<c>draw:</c>): a direct device sensor per phase, read through the signal
/// layer with its own max_age. The current feeds protection's relief rule and verification; the
/// power feeds verification, foreign load and the SIG-09 device terms.
/// </summary>
/// <param name="Current">Draw-current signal per phase (role device_current).</param>
/// <param name="Power">Draw-power signal per phase (role device_power).</param>
public sealed record DeviceDrawDefinition(
    ImmutableDictionary<Phase, SignalName> Current,
    ImmutableDictionary<Phase, SignalName> Power,
    TimeSpan MaxAge);

/// <summary>
/// One declared circuit group (CONN-03): a downstream breaker with its own rating, characteristic,
/// reserve and, optionally, a sub-meter current per phase.
/// </summary>
/// <param name="Reserve">Reserve per phase in W (converted to A by the signal layer).</param>
/// <param name="Current">Sub-meter current signal per phase (role group_current); empty for an estimated group.</param>
/// <param name="Members">The devices that name this group.</param>
public sealed record CircuitGroupDefinition(
    int Index,
    GroupName Id,
    Amperes BreakerRating,
    BreakerCharacteristic Characteristic,
    ImmutableArray<Phase> Phases,
    ImmutableDictionary<Phase, Watts> Reserve,
    ImmutableDictionary<Phase, SignalName> Current,
    TimeSpan? CurrentMaxAge,
    ImmutableArray<DeviceName> Members)
{
    public string Path => $"circuit_groups[{Index}]";
}
