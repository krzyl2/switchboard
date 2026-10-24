using System.Collections.Immutable;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Domain.Guard;

/// <summary>What protection already holds a stage to.</summary>
public enum StageLimit
{
    AtMinimum,
    Off,
}

public enum ShedAction
{
    SwitchOff,
    ReduceToMinimum,
}

/// <summary>
/// A device considered for shedding. <see cref="Applied"/> holds one entry per stage (index =
/// stage number − 1); null is an unknown or stale state, which counts as on at full rating.
/// <see cref="Limited"/> maps stage numbers to what protection already holds them to.
/// </summary>
public sealed record ShedCandidate(
    DeviceSpec Spec,
    ImmutableArray<AppliedState?> Applied,
    bool UnderMinOn,
    ImmutableDictionary<int, StageLimit> Limited);

/// <summary>One shedding step on one phase; <see cref="Relief"/> is the current it removes from that phase.</summary>
public sealed record ShedStep(DeviceName Device, int Stage, ShedAction Action, Amperes Relief, bool UnderMinOn);

/// <summary>
/// The shedding order of 04 §2 and DISP-02, on one phase. Only devices with a stage on the
/// overloaded phase are candidates (energy cannot move between phases); <c>no_shed</c> devices
/// never are. Sorted by (under min_on lockout, shed priority or value per kWh ascending, reaction
/// time ascending, name): a device under a min_on lockout is taken last but never skipped, because
/// lockouts never outrank protection. A modulating device is first reduced to its minimum and then
/// switched off; stages of a stepped device go highest first. A stage known to be off (or already
/// held where the step would take it) gives no relief and is no step.
/// </summary>
public static class ShedOrder
{
    public static ImmutableArray<ShedStep> For(Phase phase, IReadOnlyList<ShedCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var steps = ImmutableArray.CreateBuilder<ShedStep>();
        var ordered = candidates
            .Where(c => !c.Spec.NoShed && c.Spec.IsOn(phase))
            .OrderBy(c => c.UnderMinOn)
            .ThenBy(c => c.Spec.ShedKey)
            .ThenBy(c => c.Spec.ReactionTime)
            .ThenBy(c => c.Spec.Id);
        foreach (var candidate in ordered)
        {
            steps.AddRange(StepsOf(candidate, phase));
        }

        return steps.ToImmutable();
    }

    /// <summary>The order of devices alone, for display: the same sort without the lockout term.</summary>
    public static ImmutableArray<DeviceName> Rank(IEnumerable<DeviceSpec> devices) =>
        [.. devices.Where(d => !d.NoShed).OrderBy(d => d.ShedKey).ThenBy(d => d.ReactionTime).ThenBy(d => d.Id).Select(d => d.Id)];

    private static IEnumerable<ShedStep> StepsOf(ShedCandidate candidate, Phase phase)
    {
        var spec = candidate.Spec;
        if (spec.Type == DeviceType.Modulating)
        {
            var stage = spec.Stages[0];
            var range = spec.Range!;
            var limit = candidate.Limited.TryGetValue(stage.Index, out var held) ? held : (StageLimit?)null;
            var setpoint = AppliedOf(candidate, stage.Index) is SetpointApplied s ? s.Setpoint : range.Max;
            if (limit == StageLimit.Off || setpoint.Value <= 0)
            {
                yield break;
            }

            if (limit is null && setpoint > range.Min)
            {
                yield return new ShedStep(spec.Id, stage.Index, ShedAction.ReduceToMinimum, setpoint - range.Min, candidate.UnderMinOn);
            }

            // After the reduction (taken now or earlier) only the minimum is left to switch off.
            var remaining = setpoint > range.Min ? range.Min : setpoint;
            yield return new ShedStep(spec.Id, stage.Index, ShedAction.SwitchOff, remaining, candidate.UnderMinOn);
            yield break;
        }

        foreach (var stage in spec.Stages.Where(s => s.Phases.Contains(phase)).OrderByDescending(s => s.Index))
        {
            if (candidate.Limited.TryGetValue(stage.Index, out var held) && held == StageLimit.Off)
            {
                continue;
            }

            if (AppliedOf(candidate, stage.Index) is RelayApplied { On: false })
            {
                continue;
            }

            yield return new ShedStep(spec.Id, stage.Index, ShedAction.SwitchOff, stage.RatedCurrent, candidate.UnderMinOn);
        }
    }

    private static AppliedState? AppliedOf(ShedCandidate candidate, int stage) =>
        stage - 1 < candidate.Applied.Length ? candidate.Applied[stage - 1] : null;
}
