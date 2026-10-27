using Switchboard.Application.Goals;
using Switchboard.Application.Planning;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Planning;

namespace Switchboard.Host.Composition;

/// <summary>
/// What the plan view compares a plan with (15-PLANNER, "When the plan is missing, stale or wrong"): the configuration in force (its planner mode and the
/// revision the planner stamps its plans with), the device's spec fingerprint and the goals as they stand now. It lives at the host's edge for the same reason
/// <see cref="DiagnosticsPlannerSource"/> does: it is one of the few things that may read the full signal snapshot and the goal model, so dispatch sees neither.
/// The goal board is evaluated once per instant however many devices ask.
/// </summary>
internal sealed class DiagnosticsPlanCurrency(ISignalDiagnosticsSource diagnostics, DiagnosticsPlannerSource planner, GoalTracker goals) : IPlanCurrency
{
    private (DateTimeOffset At, GoalBoard Board)? _board;

    public PlannerDispatchMode Mode => diagnostics.Current.Configuration?.Configuration?.Planner?.Dispatch ?? PlannerDispatchMode.Off;

    public long? ConfigRevision => diagnostics.Current.Configuration?.Configuration is { Planner: not null } configuration ? planner.RevisionOf(configuration) : null;

    public long? SpecIdentity(DeviceName device) =>
        diagnostics.Current.Snapshot.Protection.Setup?.Devices.FirstOrDefault(d => d.Id == device) is { } spec ? DiagnosticsPlannerSource.Identity(spec) : null;

    public PlanGoalState? Goal(DeviceName device, string id, long revision, DateTimeOffset now)
    {
        if (_board is not { } cached || cached.At != now)
        {
            cached = (now, goals.Evaluate(now));
            _board = cached;
        }

        return cached.Board.Goals.FirstOrDefault(g => g.Request.Device == device && g.Request.Revision == revision && Planner.GoalId(g.Request) == id) is { } goal
            ? new PlanGoalState(goal.Request.Sources, goal.Request.OnMet == GoalOnMet.Stop)
            : null;
    }
}
