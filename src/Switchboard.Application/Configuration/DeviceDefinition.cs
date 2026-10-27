using System.Collections.Immutable;
using Switchboard.Application.Signals;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Configuration;

/// <summary>
/// One declared device as the configuration layer holds it: the only place where a device meets
/// entity IDs. <see cref="StageDefinition.Actuator"/> (and <see cref="StopActuator"/>) are the only
/// entities that may ever be commanded for it (SAFE-05), through the command port's check of the
/// configuration loaded at send time.
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

    /// <summary>A switch or a select that disables a modulating device (its only way to reach zero); validated like an actuator.</summary>
    public EntityId? StopActuator { get; init; }

    /// <summary>The declared stop and resume options when <see cref="StopActuator"/> is a select (or input_select); null for a switch.</summary>
    public StopOptions? StopOptions { get; init; }

    /// <summary>The state signal reading <see cref="StopActuator"/> (<c>device_&lt;id&gt;_stop</c>).</summary>
    public SignalName? StopSignal { get; init; }

    /// <summary>The device's own measured draw, if declared.</summary>
    public DeviceDrawDefinition? Draw { get; init; }

    /// <summary>Identical elements on different phases, rotated by phase ranking (DISP-10).</summary>
    public GroupName? RotationGroup { get; init; }

    /// <summary>The circuit group the device sits behind (CONN-03).</summary>
    public GroupName? CircuitGroup { get; init; }

    /// <summary>An optional display name (<c>name:</c>), shown in the UI next to the id; never used for a decision.</summary>
    public string? Name { get; init; }

    /// <summary>What dispatch may do beyond restoring what was shed (<c>start:</c>, default from devices.default_start); always set by the parser.</summary>
    public required StartPolicy Start { get; init; }

    /// <summary>Which price the value follows (<c>value_follows</c>); static when it names a price the file has no spec for (warned at load).</summary>
    public ValueFollows ValueFollows { get; init; } = ValueFollows.Static;

    /// <summary><c>value_factor</c>, finite and above 0.</summary>
    public double ValueFactor { get; init; } = ValueSpec.NeutralFactor;

    /// <summary>The value model dispatch evaluates (slice 3); never part of the <see cref="DeviceSpec"/> protection sees.</summary>
    public ValueSpec Value => new(ValuePerKwh, ValueFollows, ValueFactor);

    /// <summary>The sources after narrowing to the zones <c>economics.tariff</c> has; null when the device declares none (0.9 behaviour).</summary>
    public SourceSet? Sources { get; init; }

    /// <summary>The sources as written; null when none declared.</summary>
    public SourceSet? SourcesDeclared { get; init; }

    /// <summary>The device's <c>goal:</c> (PLAN-01); null for a device without one, which stays an opportunist. Never part of the <see cref="DeviceSpec"/> protection sees.</summary>
    public GoalSpec? Goal { get; init; }

    /// <summary>
    /// An opaque fingerprint of every entity that commands or reports this device (FNV-1a over the
    /// stage actuators, state entities and stop actuator, and a select stop actuator's stop option):
    /// protection and the ledger compare it without ever seeing an entity ID, so a device re-pointed
    /// at another entity, or at another stop option (it changes what the reported state means),
    /// starts afresh. The resume option is left out: it is only what a resume sends, resolved from
    /// the configuration loaded at send time, so a new one keeps the restore baseline of a device
    /// Switchboard holds stopped. A switch stop adds nothing, so its fingerprint is the one 0.7.0
    /// computed.
    /// </summary>
    public long ActuatorRevision
    {
        get
        {
            const ulong offset = 14695981039346656037;
            const ulong prime = 1099511628211;
            var hash = offset;
            void Add(string text)
            {
                foreach (var b in System.Text.Encoding.UTF8.GetBytes(text))
                {
                    hash = (hash ^ b) * prime;
                }

                // A separator byte that no entity ID contains, so "ab"+"c" and "a"+"bc" differ.
                hash = (hash ^ 0x0A) * prime;
            }

            foreach (var stage in Stages)
            {
                Add(stage.Actuator.Value);
                Add(stage.StateEntity.Value);
            }

            Add(StopActuator?.Value ?? string.Empty);
            if (StopOptions is { } options)
            {
                Add(options.StopOption.Value);
            }

            return unchecked((long)hash);
        }
    }

    /// <summary>
    /// The phases on which a modulating device's current is derived from its power draw: it declares a
    /// <c>draw.power</c> but no <c>draw.current</c> there, and the phase has a mapped voltage. A charger that
    /// reports only changes leaves its current sensor frozen at a load while its power keeps refreshing
    /// (go-e: nrg_4 against nrg_7), so the power is the better evidence of what it draws. Without a
    /// voltage nothing is derived and the device behaves as before. Storage is left out on purpose: its
    /// stale draw must keep falling back to the reported discharge.
    /// </summary>
    public ImmutableArray<Phase> DerivedCurrentPhases(IReadOnlyCollection<Phase> voltagePhases)
    {
        ArgumentNullException.ThrowIfNull(voltagePhases);
        return Type == DeviceType.Modulating && Draw is not null
            ? [.. Draw.Power.Keys.Where(p => !Draw.Current.ContainsKey(p) && voltagePhases.Contains(p)).Order()]
            : [];
    }

    public DeviceSpec ToSpec() => ToSpec([]);

    /// <summary>The spec with the phases whose draw current is derived from the draw power (see <see cref="DerivedCurrentPhases"/>).</summary>
    public DeviceSpec ToSpec(IReadOnlyCollection<Phase> voltagePhases) => new(
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
        StopOptions = StopOptions,
        CurrentDrawPhases = Draw is null ? [] : [.. Draw.Current.Keys.Concat(DerivedCurrentPhases(voltagePhases)).Distinct().Order()],
        CircuitGroup = CircuitGroup,
        Start = Start,
        ActuatorRevision = ActuatorRevision,
        StateReportsOnChange = StateReports == StateFreshness.OnChange,
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
