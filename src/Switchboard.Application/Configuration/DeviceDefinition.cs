using System.Collections.Immutable;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Configuration;

/// <summary>
/// One declared device as the configuration layer holds it: the only place where a device meets
/// entity IDs. <see cref="StageDefinition.Actuator"/> is the one entity that may ever be commanded
/// for that stage (SAFE-05); nothing in Stage 2 commands it. Protection receives the
/// <see cref="DeviceSpec"/> built from this, without entity IDs or watts.
/// </summary>
/// <param name="Index">The position under <c>devices:</c>, for issue paths (<c>devices[0]</c>).</param>
/// <param name="Range">A modulating device's setpoint range, converted to amperes.</param>
/// <param name="RangeUnit">The unit the range and the actuator are written in (A or mA).</param>
/// <param name="StateMaxAge">max_age of the stage state signals (SIG-05).</param>
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
        MinOff);
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
