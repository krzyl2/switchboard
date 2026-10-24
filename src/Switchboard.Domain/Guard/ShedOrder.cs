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
    ImmutableDictionary<int, StageLimit> Limited)
{
    /// <summary>The device's own fresh draw on the phase; null without a draw signal there or when it is not fresh.</summary>
    public Amperes? Draw { get; init; }
}

/// <summary>One shedding step on one phase; <see cref="Relief"/> is the current it removes from that phase.</summary>
/// <param name="Relief">Nominal (rating or setpoint) relief, capped by the device's fresh draw when it declares one.</param>
public sealed record ShedStep(DeviceName Device, int Stage, ShedAction Action, Amperes Relief, bool UnderMinOn)
{
    /// <summary>A fresh draw shows the step relieves nothing (the draw is at or below the level it leads to).</summary>
    public bool Idle { get; init; }
}

/// <summary>
/// The shedding order of 04 §2 and DISP-02, on one phase. Only devices with a stage on the
/// overloaded phase are candidates (energy cannot move between phases); <c>no_shed</c> devices
/// never are. Sorted by (under min_on lockout, shed priority or value per kWh ascending, reaction
/// time ascending, name): a device under a min_on lockout is taken last but never skipped, because
/// lockouts never outrank protection. A modulating device is first reduced to its minimum and then,
/// only if it has a stop actuator, switched off; a storage device is reduced to zero charge. Stages
/// of a stepped device go highest first. A stage known to be off (or already held where the step
/// would take it) gives no relief and is no step. A step whose device's fresh draw is already at or
/// below the level the step leads to (an idle charger at a high setpoint) relieves nothing, the
/// rule of <see cref="Dispatch.LoadModel"/>: it is kept, so shedding everything still holds it, but
/// it comes after every step that relieves something, so one shed step is never spent on it.
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

        return [.. steps.Where(s => !s.Idle), .. steps.Where(s => s.Idle)];
    }

    /// <summary>The order of devices alone, for display: the same sort without the lockout term.</summary>
    public static ImmutableArray<DeviceName> Rank(IEnumerable<DeviceSpec> devices) =>
        [.. devices.Where(d => !d.NoShed).OrderBy(d => d.ShedKey).ThenBy(d => d.ReactionTime).ThenBy(d => d.Id).Select(d => d.Id)];

    private static IEnumerable<ShedStep> StepsOf(ShedCandidate candidate, Phase phase)
    {
        var spec = candidate.Spec;
        if (spec.IsSetpoint)
        {
            var stage = spec.Stages[0];
            var floor = spec.SetpointFloor;
            var limit = candidate.Limited.TryGetValue(stage.Index, out var held) ? held : (StageLimit?)null;
            var setpoint = AppliedOf(candidate, stage.Index) is SetpointApplied s ? s.Setpoint : spec.Range!.Max;

            // Nothing to take: already held off, held at the floor of a device that cannot switch
            // off, or drawing nothing (a storage device that is not charging is never a candidate:
            // stopping a discharge would raise the current).
            if (limit == StageLimit.Off || (limit == StageLimit.AtMinimum && !spec.CanSwitchOff) || setpoint.Value <= 0)
            {
                yield break;
            }

            if (limit is null && setpoint > floor)
            {
                yield return Step(candidate, stage.Index, ShedAction.ReduceToMinimum, setpoint - floor, floor);
            }

            // Only a device with a stop path is switched off after the reduction; one that cannot
            // switch off is never a step, so the next real candidate is taken instead.
            if (spec.CanSwitchOff)
            {
                var remaining = setpoint > floor ? floor : setpoint;
                yield return Step(candidate, stage.Index, ShedAction.SwitchOff, remaining, Amperes.Zero);
            }

            yield break;
        }

        var stages = spec.Stages
            .Where(s => s.Phases.Contains(phase)
                && !(candidate.Limited.TryGetValue(s.Index, out var held) && held == StageLimit.Off)
                && AppliedOf(candidate, s.Index) is not RelayApplied { On: false })
            .OrderByDescending(s => s.Index)
            .ToList();
        var after = new Amperes(stages.Sum(s => s.RatedCurrent.Value));
        foreach (var stage in stages)
        {
            after -= stage.RatedCurrent;
            yield return Step(candidate, stage.Index, ShedAction.SwitchOff, stage.RatedCurrent, after);
        }
    }

    /// <param name="after">The ceiling on the phase once the step is taken.</param>
    private static ShedStep Step(ShedCandidate candidate, int stage, ShedAction action, Amperes relief, Amperes after) =>
        candidate.Draw is { } draw
            ? new ShedStep(candidate.Spec.Id, stage, action, Amperes.Max(Amperes.Zero, Amperes.Min(relief, draw - after)), candidate.UnderMinOn) { Idle = draw <= after }
            : new ShedStep(candidate.Spec.Id, stage, action, relief, candidate.UnderMinOn);

    private static AppliedState? AppliedOf(ShedCandidate candidate, int stage) =>
        stage - 1 < candidate.Applied.Length ? candidate.Applied[stage - 1] : null;
}
