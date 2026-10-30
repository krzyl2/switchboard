using System.Collections.Immutable;
using Switchboard.Application.Goals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Ev;
using Switchboard.Domain.Goals;

namespace Switchboard.Application.Ev;

/// <summary>
/// The EV module as a goal source (EV-06, docs/EV.md B1, B4, B8): at most four goals for the one EV device, stated from the snapshot the module computed beside
/// the control loop. This runs inside the guard's step (through the plan view), so it does the least that can be done: no I/O, no computation of the offer, no
/// exception. It only reads the published snapshot and filters it by the clock:
/// <list type="bullet">
/// <item>a goal whose deadline has passed is dropped, a trip the moment its start is reached (B4: a trip in progress must never become a missed hard goal that
/// runs in every zone from now);</item>
/// <item>a snapshot older than <c>control.ev.snapshot_max_age</c>, or a computation that failed, is a failed offer: the goals are missing, the tracker does not
/// conclude anything from their absence and the car is v2 (never the old goals);</item>
/// <item>a manual goal on the device switches the module's goals off (B8), so the two never double count.</item>
/// </list>
/// It names no effector, command port, override or guard type: the module proposes, the plan and the guard decide.
/// </summary>
public sealed class EvGoalSource(IEvSnapshotSource snapshots, Func<DeviceName, bool> manualGoalActive) : IGoalSource
{
    public GoalOffer Goals(GoalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Configuration.Ev is not { } ev)
        {
            return GoalOffer.Empty;
        }

        var published = snapshots.Current;
        if (published.Failed)
        {
            return GoalOffer.FailedOffer;
        }

        if (published.Snapshot is not { } snapshot)
        {
            return GoalOffer.Empty;
        }

        if (snapshot.Device != ev.Device)
        {
            // The configuration moved on to another device; the next computation follows.
            return GoalOffer.Empty;
        }

        if (context.Now > snapshot.ValidUntil)
        {
            return GoalOffer.FailedOffer;
        }

        if (manualGoalActive(ev.Device))
        {
            return GoalOffer.Empty;
        }

        var composition = snapshot.Composition;
        var inactive = Inactive(snapshot);
        if (inactive is not null)
        {
            return new GoalOffer([], [inactive]);
        }

        var requests = composition.Goals
            .Where(g => g.Request.Deadline is not { } deadline || deadline > context.Now)
            .Select(g => g.Request with { Spread = g.Spread, QuietShortfall = g.QuietShortfall })
            .ToImmutableArray();
        return new GoalOffer(requests, []);
    }

    /// <summary>No SoC, or a plugged state that cannot be read: the goal exists but cannot be stated, and the plan says why. An unplugged car is nothing to reserve for.</summary>
    private static InactiveGoal? Inactive(EvSnapshot snapshot)
    {
        var hard = snapshot.NextTrip is not null;
        return snapshot.Composition.Code switch
        {
            EvAssessmentCode.SocStale => new InactiveGoal(snapshot.Device, GoalInactiveReason.InputStale, GoalInputKind.Soc, GoalOrigin.Module, GoalKind.Soc, hard),
            EvAssessmentCode.NotPlugged when snapshot.PluggedUnavailable => new InactiveGoal(snapshot.Device, GoalInactiveReason.InputUnavailable, GoalInputKind.Want, GoalOrigin.Module, GoalKind.Soc, hard),
            _ => null,
        };
    }
}
