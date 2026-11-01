using System.Collections.Immutable;
using System.Diagnostics;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Units;

namespace Switchboard.Domain.Dispatch;

/// <summary>
/// The levels a device can be put at (DISP-01) and how its reported state reads as one. Relays
/// count stages up from stage 1 (and shed from the highest, 04 §2); a modulating device steps from
/// its minimum to its maximum, with "disabled" below the minimum when a stop actuator exists; a
/// storage device steps from zero charge to its maximum and is never taken into discharge.
/// </summary>
public static class DeviceLevels
{
    /// <summary>Every level dispatch may target, in ascending order of the current it allows.</summary>
    public static ImmutableArray<DeviceLevel> All(DeviceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec switch
        {
            RelaySpec relay => RelayLevels(relay),
            SetpointSpec setpoint => SetpointLevels(setpoint),
            _ => throw new UnreachableException(),
        };
    }

    private static ImmutableArray<DeviceLevel> RelayLevels(RelaySpec spec)
    {
        var levels = ImmutableArray.CreateBuilder<DeviceLevel>();
        for (var on = 0; on <= spec.Stages.Length; on++)
        {
            levels.Add(new RelayLevel([.. spec.Stages.Select(s => s.Index <= on)]));
        }

        return levels.ToImmutable();
    }

    private static ImmutableArray<DeviceLevel> SetpointLevels(SetpointSpec spec)
    {
        var levels = ImmutableArray.CreateBuilder<DeviceLevel>();
        var range = spec.Range;
        if (spec.HasStopActuator)
        {
            levels.Add(DeviceLevel.At(range.Min, enabled: false));
        }

        var floor = spec.SetpointFloor.Value;
        var steps = (int)Math.Round((range.Max.Value - floor) / range.Step.Value);
        for (var i = 0; i <= steps; i++)
        {
            levels.Add(DeviceLevel.At(new Amperes(Math.Min(range.Max.Value, floor + (i * range.Step.Value)))));
        }

        return levels.ToImmutable();
    }

    /// <summary>
    /// The safe level (D8): every relay off; a setpoint at its floor, disabled when a stop actuator
    /// exists. A modulating device without one can only come down to its minimum.
    /// </summary>
    public static DeviceLevel Off(DeviceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec switch
        {
            RelaySpec relay => new RelayLevel([.. relay.Stages.Select(_ => false)]),
            SetpointSpec setpoint => DeviceLevel.At(setpoint.SetpointFloor, enabled: !setpoint.HasStopActuator),
            _ => throw new UnreachableException(),
        };
    }

    public static DeviceLevel Max(DeviceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec switch
        {
            RelaySpec relay => new RelayLevel([.. relay.Stages.Select(_ => true)]),
            SetpointSpec setpoint => DeviceLevel.At(setpoint.Range.Max),
            _ => throw new UnreachableException(),
        };
    }

    /// <summary>
    /// The applied state as a level, from the fresh state samples (stage numbers from 1, and
    /// <see cref="DeviceStateSample.StopStage"/> for a stop actuator); null when any of them is
    /// unknown, because an unknown stage may be on.
    /// </summary>
    public static DeviceLevel? Reported(DeviceSpec spec, Func<int, DeviceStateSample?> sample)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(sample);
        return spec switch
        {
            RelaySpec relay => ReportedRelays(relay, sample),
            SetpointSpec setpoint => ReportedSetpoint(setpoint, sample),
            _ => throw new UnreachableException(),
        };
    }

    private static RelayLevel? ReportedRelays(RelaySpec spec, Func<int, DeviceStateSample?> sample)
    {
        var relays = ImmutableArray.CreateBuilder<bool>(spec.Stages.Length);
        foreach (var stage in spec.Stages)
        {
            if (sample(stage.Index) is not { Fresh: true, Applied: RelayApplied relay })
            {
                return null;
            }

            relays.Add(relay.On);
        }

        return new RelayLevel(relays.MoveToImmutable());
    }

    private static SetpointLevel? ReportedSetpoint(SetpointSpec spec, Func<int, DeviceStateSample?> sample)
    {
        if (sample(spec.Stages[0].Index) is not { Fresh: true, Applied: SetpointApplied setpoint })
        {
            return null;
        }

        var enabled = true;
        if (spec.HasStopActuator)
        {
            if (sample(DeviceStateSample.StopStage) is not { Fresh: true, Applied: RelayApplied stop })
            {
                return null;
            }

            enabled = stop.On;
        }

        return new SetpointLevel(setpoint.Setpoint, enabled);
    }

    /// <summary>What protection's holds leave of a level: an Off stage is off (a setpoint at its floor, disabled if it can be), AtMinimum caps a setpoint at its floor.</summary>
    public static DeviceLevel Cap(DeviceSpec spec, DeviceLevel level, IReadOnlyDictionary<int, StageLimit> limits)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.Count == 0)
        {
            return level;
        }

        return spec switch
        {
            RelaySpec relay => CapRelays(relay, level, limits),
            SetpointSpec setpoint => CapSetpoint(setpoint, level, limits),
            _ => throw new UnreachableException(),
        };
    }

    private static DeviceLevel CapRelays(RelaySpec spec, DeviceLevel level, IReadOnlyDictionary<int, StageLimit> limits)
    {
        // A level of another kind says nothing about this device's stages: all of them off.
        var relays = level is RelayLevel { Relays.IsDefault: false } held ? held.Relays : [.. spec.Stages.Select(_ => false)];
        return new RelayLevel([.. relays.Select((on, i) => on && !(limits.TryGetValue(i + 1, out var limit) && limit == StageLimit.Off))]);
    }

    private static DeviceLevel CapSetpoint(SetpointSpec spec, DeviceLevel level, IReadOnlyDictionary<int, StageLimit> limits)
    {
        if (!limits.TryGetValue(spec.Stages[0].Index, out var limit))
        {
            return level;
        }

        var floor = spec.SetpointFloor;
        var setpoint = level is SetpointLevel s ? Amperes.Min(s.Setpoint, floor) : floor;
        return limit == StageLimit.Off && spec.HasStopActuator
            ? DeviceLevel.At(floor, enabled: false)
            : DeviceLevel.At(setpoint, level.Enabled);
    }

    /// <summary>The current a level allows on a phase (A): rated current of every stage on there, or the enabled setpoint (zero charge at most for storage).</summary>
    public static Amperes Ceiling(DeviceSpec spec, DeviceLevel level, Signals.Phase phase)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(level);
        return spec switch
        {
            SetpointSpec setpoint => level is SetpointLevel { Enabled: true } on && setpoint.IsOn(phase) ? Amperes.Max(on.Setpoint, Amperes.Zero) : Amperes.Zero,
            RelaySpec relay => RelayCeiling(relay, level, phase),
            _ => throw new UnreachableException(),
        };
    }

    private static Amperes RelayCeiling(RelaySpec spec, DeviceLevel level, Signals.Phase phase)
    {
        var total = Amperes.Zero;
        foreach (var stage in spec.Stages)
        {
            if (stage.Phases.Contains(phase) && StageOn(spec, level, stage.Index))
            {
                total += stage.RatedCurrent;
            }
        }

        return total;
    }

    /// <summary>
    /// A stage is on at a level: a relay stage when its flag is set (a level without that flag has
    /// it off); a setpoint device's one stage when it is enabled above zero.
    /// </summary>
    public static bool StageOn(DeviceSpec spec, DeviceLevel level, int stage)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(level);
        return spec switch
        {
            SetpointSpec => level is SetpointLevel { Enabled: true, Setpoint.Value: > 0 },
            RelaySpec => level is RelayLevel { Relays.IsDefault: false } relays && stage - 1 < relays.Relays.Length && relays.Relays[stage - 1],
            _ => throw new UnreachableException(),
        };
    }

    /// <summary>The sum of a level's ceilings over the device's phases: how increases and decreases are told apart.</summary>
    public static double Total(DeviceSpec spec, DeviceLevel level)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec.Phases.Sum(p => Ceiling(spec, level, p).Value);
    }

    /// <summary>True when the level draws nothing anywhere (every relay off, disabled, or a zero setpoint).</summary>
    public static bool IsOff(DeviceSpec spec, DeviceLevel level) => Total(spec, level) <= 0;

    /// <summary>
    /// The level reached from <paramref name="from"/> toward <paramref name="to"/> changing at most
    /// <paramref name="maxStages"/> relay stages (07-PITFALLS: shutdown avalanche): stages switch on
    /// from the lowest index and off from the highest. A setpoint moves in one command.
    /// </summary>
    public static DeviceLevel Toward(DeviceSpec spec, DeviceLevel from, DeviceLevel to, int maxStages)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        return spec switch
        {
            SetpointSpec => to,
            RelaySpec { Stages.Length: 1 } => to,
            RelaySpec relay => TowardRelays(relay, from, to, maxStages),
            _ => throw new UnreachableException(),
        };
    }

    private static RelayLevel TowardRelays(RelaySpec spec, DeviceLevel from, DeviceLevel to, int maxStages)
    {
        // A level of another declaration (another stage count) says nothing about this one's relays.
        var relays = (from is RelayLevel { Relays.IsDefault: false } current && current.Relays.Length == spec.Stages.Length ? current.Relays : [.. spec.Stages.Select(_ => false)]).ToArray();
        var wanted = (to as RelayLevel ?? throw new ArgumentException("A relay device is moved toward a relay level.", nameof(to))).Relays.ToArray();
        var changes = 0;
        for (var i = 0; i < relays.Length && changes < maxStages; i++)
        {
            if (wanted[i] && !relays[i])
            {
                relays[i] = true;
                changes++;
            }
        }

        for (var i = relays.Length - 1; i >= 0 && changes < maxStages; i--)
        {
            if (!wanted[i] && relays[i])
            {
                relays[i] = false;
                changes++;
            }
        }

        return new RelayLevel([.. relays]);
    }

    /// <summary>The number of relay stages (or the stop actuator) that differ between two levels.</summary>
    public static int StageChanges(DeviceSpec spec, DeviceLevel from, DeviceLevel to)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        return spec switch
        {
            SetpointSpec => from.Enabled != to.Enabled ? 1 : 0,
            RelaySpec => Flags(from).Zip(Flags(to)).Count(p => p.First != p.Second),
            _ => throw new UnreachableException(),
        };
    }

    private static ImmutableArray<bool> Flags(DeviceLevel level) => level is RelayLevel { Relays.IsDefault: false } relays ? relays.Relays : [];
}
