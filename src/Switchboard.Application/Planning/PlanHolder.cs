using System.Collections.Immutable;
using Switchboard.Domain.Planning;

namespace Switchboard.Application.Planning;

/// <summary>Where the plan stands (15-PLANNER, <c>api/plan</c> status).</summary>
public enum PlanStatus
{
    /// <summary>No plan: not computed yet (the start delay, no inputs), or the last attempt failed and nothing is held.</summary>
    Absent,

    /// <summary>A plan is held and younger than <c>planner.plan_max_age</c>.</summary>
    Fresh,

    /// <summary>A plan is held but older than <c>planner.plan_max_age</c>: whatever consumes it must treat it as absent.</summary>
    Stale,

    /// <summary>The guard is in safe state, the kill switch is engaged or Home Assistant is disconnected: the plan is ignored and none is made.</summary>
    Suspended,

    /// <summary>The last solve failed and there is no usable plan.</summary>
    Error,
}

/// <summary>How the last solve ended.</summary>
public enum SolveResult
{
    Computed,
    Timeout,
    Invalid,
    Failed,
}

/// <summary>The last attempt to compute a plan, for <c>api/plan</c> (<c>lastSolve</c>).</summary>
/// <param name="At">When the attempt started.</param>
/// <param name="Duration">How long the solve ran (the injected clock's reading, so zero on a frozen clock).</param>
/// <param name="Reason">What made the planner run: cadence, a trigger, the first plan.</param>
/// <param name="Violations">The verifier's findings when the result was refused.</param>
public sealed record SolveReport(DateTimeOffset At, TimeSpan Duration, SolveResult Result, string Reason, ImmutableArray<PlanViolation> Violations);

/// <summary>What the holder publishes: the last adopted plan (or none), whether the planner is suspended, and the last solve.</summary>
/// <param name="Plan">The last adopted plan; kept when a later solve fails, until it is older than <c>plan_max_age</c> (then consumers treat it as absent).</param>
/// <param name="Notes">Notes the service adds to the plan's own: a held solve (<see cref="PlanNote.SolverTimeout"/>, <see cref="PlanNote.PlanInvalid"/>).</param>
/// <param name="PlanMaxAge">The parameter the holder's status is judged by (<c>planner.plan_max_age</c>); zero before the first read of a configuration.</param>
public sealed record PlanState(PlanSnapshot? Plan, bool Suspended, SolveReport? LastSolve, ImmutableArray<PlanNote> Notes, TimeSpan PlanMaxAge)
{
    public static PlanState Empty { get; } = new(null, false, null, [], TimeSpan.Zero);

    /// <summary>The status at <paramref name="now"/>: a plan from the future (the clock stepped back) is as unusable as an old one.</summary>
    public PlanStatus StatusAt(DateTimeOffset now)
    {
        if (Suspended)
        {
            return PlanStatus.Suspended;
        }

        if (Plan is null)
        {
            return LastSolve is { Result: not SolveResult.Computed } ? PlanStatus.Error : PlanStatus.Absent;
        }

        return Plan.ComputedAt > now || now - Plan.ComputedAt > PlanMaxAge ? PlanStatus.Stale : PlanStatus.Fresh;
    }

    /// <summary>The plan only while it is fresh (not suspended, not older than the maximum age, not from the future).</summary>
    public PlanSnapshot? UsablePlan(DateTimeOffset now) => StatusAt(now) == PlanStatus.Fresh ? Plan : null;
}

/// <summary>
/// Holds the last <see cref="PlanState"/> (15-PLANNER, "What the plan is"): written by the planner service, read by anyone without locking, as
/// <c>SignalSnapshotHolder</c> holds the inputs. The planner publishes here and nothing else; whoever reads a plan judges its age and its
/// configuration on every use.
/// </summary>
public sealed class PlanHolder
{
    private PlanState _current = PlanState.Empty;

    public PlanState Current => Volatile.Read(ref _current);

    public event EventHandler<PlanState>? Published;

    public void Publish(PlanState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Volatile.Write(ref _current, state);
        Published?.Invoke(this, state);
    }
}
