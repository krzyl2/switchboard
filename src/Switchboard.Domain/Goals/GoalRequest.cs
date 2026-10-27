using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;

namespace Switchboard.Domain.Goals;

/// <summary>
/// What one source asks for, at one moment (15-PLANNER, Goal sources): a device wants an amount of energy by a deadline. The
/// remaining need is already in kWh drawn (loss included), so a consumer plans with one number whatever the kind.
/// </summary>
/// <param name="RemainingKwh">Energy still to draw, see <see cref="GoalMath"/>; zero when met.</param>
/// <param name="TargetKwh">An energy goal's target.</param>
/// <param name="TargetSoc">A SoC goal's target percent.</param>
/// <param name="CurrentSoc">A SoC goal's state of charge now.</param>
/// <param name="DeliveredKwh">An energy goal's progress from the ledger.</param>
/// <param name="Deadline">When the goal is due; null means the horizon end.</param>
/// <param name="Margin">Finish this long before the deadline.</param>
/// <param name="Since">When the goal began, from the ledger; null for a SoC goal, whose progress is the SoC signal and which has no ledger.</param>
/// <param name="Revision">The goal's fingerprint, see <see cref="GoalSpec.Revision"/>.</param>
public sealed record GoalRequest(
    DeviceName Device,
    GoalKind Kind,
    double RemainingKwh,
    double? TargetKwh,
    double? TargetSoc,
    double? CurrentSoc,
    double? DeliveredKwh,
    DateTimeOffset? Deadline,
    TimeSpan Margin,
    bool Hard,
    SourceSet? Sources,
    bool Escalate,
    GoalOnMet OnMet,
    DateTimeOffset? Since,
    GoalOrigin Origin,
    long Revision)
{
    /// <summary>The instant the plan aims to be finished: the deadline less the margin; null without a deadline.</summary>
    public DateTimeOffset? UsableDeadline => Deadline - Margin;
}

/// <summary>A request with where it stands now.</summary>
public sealed record Goal(GoalRequest Request, GoalStatus Status)
{
    /// <summary>
    /// The status of a request at <paramref name="now"/>: met when nothing remains, otherwise past its deadline it is expired (soft)
    /// or missed (hard), otherwise on track. At risk and infeasible are the planner's to say (slice 4).
    /// </summary>
    public static Goal Of(GoalRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        var status = GoalMath.IsMet(request.RemainingKwh)
            ? GoalStatus.Met
            : request.Deadline is { } deadline && now >= deadline
                ? request.Hard ? GoalStatus.Missed : GoalStatus.Expired
                : GoalStatus.OnTrack;
        return new Goal(request, status);
    }
}
