using System.Collections.Immutable;
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
        var levels = ImmutableArray.CreateBuilder<DeviceLevel>();
        if (!spec.IsSetpoint)
        {
            for (var on = 0; on <= spec.Stages.Length; on++)
            {
                levels.Add(new DeviceLevel([.. spec.Stages.Select(s => s.Index <= on)], null, true));
            }

            return levels.ToImmutable();
        }

        var range = spec.Range!;
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
        return spec.IsSetpoint
            ? DeviceLevel.At(spec.SetpointFloor, enabled: !spec.HasStopActuator)
            : new DeviceLevel([.. spec.Stages.Select(_ => false)], null, true);
    }

    public static DeviceLevel Max(DeviceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec.IsSetpoint ? DeviceLevel.At(spec.Range!.Max) : new DeviceLevel([.. spec.Stages.Select(_ => true)], null, true);
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
        if (!spec.IsSetpoint)
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

            return new DeviceLevel(relays.MoveToImmutable(), null, true);
        }

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

        return DeviceLevel.At(setpoint.Setpoint, enabled);
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

        if (!spec.IsSetpoint)
        {
            var relays = level.Relays.IsDefault ? [.. spec.Stages.Select(_ => false)] : level.Relays;
            return level with { Relays = [.. relays.Select((on, i) => on && !(limits.TryGetValue(i + 1, out var held) && held == StageLimit.Off))] };
        }

        if (!limits.TryGetValue(spec.Stages[0].Index, out var limit))
        {
            return level;
        }

        var floor = spec.SetpointFloor;
        var setpoint = level.Setpoint is { } s ? Amperes.Min(s, floor) : floor;
        return limit == StageLimit.Off && spec.HasStopActuator
            ? DeviceLevel.At(floor, enabled: false)
            : DeviceLevel.At(setpoint, level.Enabled);
    }

    /// <summary>The current a level allows on a phase (A): rated current of every stage on there, or the enabled setpoint (zero charge at most for storage).</summary>
    public static Amperes Ceiling(DeviceSpec spec, DeviceLevel level, Signals.Phase phase)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(level);
        if (spec.IsSetpoint)
        {
            return level.Enabled && level.Setpoint is { } setpoint && spec.IsOn(phase) ? Amperes.Max(setpoint, Amperes.Zero) : Amperes.Zero;
        }

        var total = Amperes.Zero;
        foreach (var stage in spec.Stages)
        {
            if (stage.Phases.Contains(phase) && !level.Relays.IsDefault && stage.Index - 1 < level.Relays.Length && level.Relays[stage.Index - 1])
            {
                total += stage.RatedCurrent;
            }
        }

        return total;
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
        if (spec.IsSetpoint || spec.Stages.Length == 1)
        {
            return to;
        }

        // A level of another declaration (another stage count) says nothing about this one's relays.
        var relays = (from.Relays.IsDefault || from.Relays.Length != spec.Stages.Length ? [.. spec.Stages.Select(_ => false)] : from.Relays).ToArray();
        var wanted = to.Relays.ToArray();
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

        return new DeviceLevel([.. relays], null, true);
    }

    /// <summary>The number of relay stages (or the stop actuator) that differ between two levels.</summary>
    public static int StageChanges(DeviceSpec spec, DeviceLevel from, DeviceLevel to)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (spec.IsSetpoint)
        {
            return from.Enabled != to.Enabled ? 1 : 0;
        }

        return from.Relays.Zip(to.Relays).Count(p => p.First != p.Second);
    }
}
