using System.Collections.Immutable;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Planning;

namespace Switchboard.Application.Planning;

/// <summary>
/// Why to plan now (15-PLANNER, "Re-planning"): the one place that reads the world against the plan in force. A goal added, changed, met or removed, a device
/// that stopped answering or answers again, goal progress that left the plan, a configuration reload, a clock step and a resume are triggers; a changed
/// forecast or price curve is not (the next cycle boundary has it).
/// </summary>
/// <param name="Reason">Why to plan; null: nothing calls for it.</param>
/// <param name="Immediate">The rate limit does not apply.</param>
/// <param name="Everyone">The new plan replaces the current slot's entries of every device at once.</param>
/// <param name="Devices">The devices whose entries it replaces at once.</param>
internal sealed record ReplanDecision(string? Reason, bool Immediate, bool Everyone, HashSet<DeviceName> Devices);

/// <summary>What a goal's change is read from: its revision, deadline, hardness, what happens when it is met, and whether it is met. Nothing else of a goal is a trigger.</summary>
internal sealed record GoalMark(long Revision, DateTimeOffset? Deadline, bool Hard, GoalOnMet OnMet, bool Met);

/// <summary>A goal of the device that is not active, and why: it leaves the plan, which is a change.</summary>
internal sealed record InactiveMark(GoalInputKind Input, GoalInactiveReason Reason);

/// <summary>The goals of one device as the triggers last saw them, in a fixed order; equal when they are the same goals in the same order.</summary>
internal sealed record DeviceGoalMarks(ImmutableArray<GoalMark> Goals, ImmutableArray<InactiveMark> Inactive)
{
    public bool Equals(DeviceGoalMarks? other) => other is not null && Goals.SequenceEqual(other.Goals) && Inactive.SequenceEqual(other.Inactive);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var goal in Goals)
        {
            hash.Add(goal);
        }

        foreach (var inactive in Inactive)
        {
            hash.Add(inactive);
        }

        return hash.ToHashCode();
    }
}

/// <summary>Remembers the goals and device fingerprint of the last plan and decides, for a new capture, whether and why to plan again.</summary>
internal sealed class ReplanTriggers
{
    private Dictionary<DeviceName, DeviceGoalMarks> _marks = [];
    private string? _deviceFingerprint;

    /// <param name="plan">The plan in force; none: the first plan, at once for everyone.</param>
    public ReplanDecision Evaluate(PlanSnapshot? plan, PlannerCapture capture, DateTimeOffset now, DateTimeOffset? previous, bool resumed, PlannerParameters parameters)
    {
        var devices = new HashSet<DeviceName>();
        if (plan is null)
        {
            return new ReplanDecision("first", false, true, devices);
        }

        string? reason = null;
        var immediate = false;
        var everyone = false;
        if (plan.ComputedAt > now || (previous is { } before && now < before))
        {
            reason = "clock_step_back";
            immediate = everyone = true;
        }
        else if (previous is { } earlier && now - earlier > parameters.PlanMaxAge)
        {
            reason = "clock_step";
            immediate = everyone = true;
        }

        if (plan.ConfigRevision != capture.Input.ConfigRevision)
        {
            reason ??= "config";
            immediate = everyone = true;
        }

        if (resumed)
        {
            reason ??= "safe_state_exit";
            immediate = everyone = true;
        }

        var marks = MarksOf(capture);
        foreach (var device in marks.Keys.Union(_marks.Keys))
        {
            if (!Equals(marks.GetValueOrDefault(device), _marks.GetValueOrDefault(device)))
            {
                devices.Add(device);
                reason ??= "goal";
            }
        }

        if (_deviceFingerprint is not null && _deviceFingerprint != capture.DeviceFingerprint)
        {
            reason ??= "device_state";
            everyone = true;
        }

        foreach (var device in Deviating(plan, capture, now, parameters))
        {
            devices.Add(device);
            reason ??= "progress";
        }

        return new ReplanDecision(reason, immediate, everyone, devices);
    }

    /// <summary>Takes the capture a plan was just adopted for as what later captures are compared with.</summary>
    public void Remember(PlannerCapture capture)
    {
        _marks = MarksOf(capture);
        _deviceFingerprint = capture.DeviceFingerprint;
    }

    public void Forget()
    {
        _marks = [];
        _deviceFingerprint = null;
    }

    private static Dictionary<DeviceName, DeviceGoalMarks> MarksOf(PlannerCapture capture)
    {
        var result = new Dictionary<DeviceName, DeviceGoalMarks>();
        foreach (var group in capture.Input.Goals.GroupBy(g => g.Device))
        {
            var goals = group.OrderBy(g => g.Deadline is null).ThenBy(g => g.Deadline).ThenBy(g => g.Revision)
                .Select(g => new GoalMark(g.Revision, g.Deadline, g.Hard, g.OnMet, GoalMath.IsMet(g.RemainingKwh)));
            result[group.Key] = new DeviceGoalMarks([.. goals], []);
        }

        foreach (var inactive in capture.Inactive)
        {
            var known = result.GetValueOrDefault(inactive.Device) ?? new DeviceGoalMarks([], []);
            result[inactive.Device] = known with { Inactive = known.Inactive.Add(new InactiveMark(inactive.Input, inactive.Reason)) };
        }

        return result;
    }

    /// <summary>
    /// Devices whose goal progress left the plan: the energy the goals still need against what the plan said would still be needed by now. A device that drew
    /// half the planned surplus under a cloud, or one charged by hand, is replanned without waiting for the cycle boundary.
    /// </summary>
    private static IEnumerable<DeviceName> Deviating(PlanSnapshot plan, PlannerCapture capture, DateTimeOffset now, PlannerParameters parameters)
    {
        foreach (var device in plan.Devices)
        {
            var planned = device.Goals.Where(g => g.Status is GoalStatus.OnTrack or GoalStatus.AtRisk or GoalStatus.Infeasible or GoalStatus.Missed).ToList();
            var start = planned.Sum(g => g.RemainingKwh);
            if (start <= Planner.Tolerance)
            {
                continue;
            }

            var done = 0.0;
            foreach (var entry in device.Entries.Where(e => e.Start < now && e.Want is PlanWant.Run or PlanWant.Urgent))
            {
                var share = entry.End <= now ? 1 : (now - entry.Start) / (entry.End - entry.Start);
                done += entry.EnergyKwh * share;
            }

            var expected = Math.Max(0, start - done);
            var actual = capture.Input.Goals
                .Where(g => g.Device == device.Device && planned.Any(p => p.Ref.Revision == g.Revision && p.Deadline == g.Deadline))
                .Sum(g => g.RemainingKwh);
            if (Math.Abs(actual - expected) > parameters.ProgressToleranceFraction * start)
            {
                yield return device.Device;
            }
        }
    }
}
