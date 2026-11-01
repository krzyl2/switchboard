using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;

namespace Switchboard.Domain.Goals;

/// <summary>
/// What one source asks for, at one moment (15-PLANNER, Goal sources): a device wants an amount of energy by a deadline. The
/// remaining need is already in kWh drawn (loss included), so a consumer plans with one number whatever the kind.
/// </summary>
/// <param name="Target">What the goal aims at and how far it is, by kind (<see cref="EnergyTarget"/> or <see cref="SocTarget"/>); a SoC goal has no ledger, so no progress or start.</param>
/// <param name="RemainingKwh">Energy still to draw, see <see cref="GoalMath"/>; zero when met.</param>
/// <param name="Deadline">When the goal is due; null means the horizon end.</param>
/// <param name="Margin">Finish this long before the deadline.</param>
/// <param name="Revision">The goal's fingerprint, see <see cref="GoalSpec.Revision"/>.</param>
/// <param name="Spread">The plan finishes this goal as late as its window allows and spreads the energy evenly over the window (the lowest power that holds it) instead of running at the device's ceiling; only a module goal asks for it.</param>
/// <param name="QuietShortfall">Energy this goal leaves unplaced does not raise the plan's <c>goal_shortfall</c> note (a PV-only cap that a cloudy evening cannot fill).</param>
public sealed record GoalRequest(
    DeviceName Device,
    GoalTarget Target,
    double RemainingKwh,
    DateTimeOffset? Deadline,
    TimeSpan Margin,
    bool Hard,
    SourceSet? Sources,
    bool Escalate,
    GoalOnMet OnMet,
    GoalOrigin Origin,
    long Revision,
    bool Spread = false,
    bool QuietShortfall = false)
{
    public GoalKind Kind => Target.Kind;

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
