using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Switchboard.Application.Configuration;
using Switchboard.Application.Ev;
using Switchboard.Application.Goals;
using Switchboard.Application.Planning;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Ev;
using Switchboard.Domain.Forecast;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Planning;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Reports;

/// <summary>One goal of the plan report: an active goal (with the plan's verdict on it while a fresh plan has judged it) or an inactive one, and the EV module's cause for it.</summary>
/// <param name="Goal">The goal the board evaluates; null for an inactive one.</param>
/// <param name="Inactive">The goal that could not be evaluated; null for an active one.</param>
/// <param name="Judged">The fresh plan's verdict on the goal; null without a fresh plan or when the plan does not list the goal.</param>
/// <param name="Cause">Why the EV module wants the goal (its own closed assessment text); null on a goal of any other source.</param>
public sealed record PlanReportGoal(Goal? Goal, InactiveGoal? Inactive, PlanGoal? Judged, string? Cause);

/// <summary>A device with a goal: its goals in board order (active first, then the inactive ones) and what the plan holds for it.</summary>
public sealed record PlanReportDevice(string Device, IReadOnlyList<PlanReportGoal> Goals, DevicePlan? Planned);

/// <summary>
/// What GET api/plan says, as the application decided it and before it is worded: whether a planner exists, the plan's status, the inputs the slots stand on
/// (the plan's own while one is held), every goal per device with the plan's verdict, the notes and the issues. The web layer only maps it to its response.
/// </summary>
/// <param name="Planner">The <c>planner:</c> section in force; null without one (a 0.10 file), when nothing but the goals is shown.</param>
/// <param name="Parameters">The <c>control.planner</c> parameters; null without a valid configuration.</param>
/// <param name="Currency">The label from <c>economics.currency</c>; null without an <c>economics:</c> section.</param>
/// <param name="Inputs">The plan's inputs, or the inputs as they stand now without a plan; null without a planner or its sources.</param>
/// <param name="PvForecast">The PV forecast signal's latest sample, for its state and age; null when none is mapped.</param>
/// <param name="Notes">The input, plan and service notes, once each in order.</param>
public sealed partial record PlanReport(
    DateTimeOffset At,
    PlannerDefinition? Planner,
    PlannerParameters? Parameters,
    string? Currency,
    PlanStatus Status,
    PlanInputs? Inputs,
    ForecastSample? PvForecast,
    PlanSnapshot? Plan,
    PlanState? State,
    int GoalsActive,
    ImmutableArray<InactiveGoal> GoalsInactive,
    IReadOnlyList<PlanReportDevice> Devices,
    IReadOnlyList<GoalRequest> Superseded,
    IReadOnlyList<PlanNote> Notes,
    IReadOnlyList<ConfigurationIssue> Issues)
{
    /// <param name="sources">The plan's sources (see <see cref="PlanSourceMapper"/>); null leaves the inputs and slots out, as without a planner.</param>
    /// <param name="forecast">The forecast signals of the latest store cycle, for the PV forecast's state and age.</param>
    /// <param name="state">What the planner service holds (the last adopted plan, its status, the last solve); null or empty without a planner.</param>
    /// <param name="ev">The EV module's published snapshot; its assessment code is the cause of the module's goals.</param>
    public static PlanReport Compose(ConfigurationResult? result, GoalBoard board, PlanInputSources? sources = null, ForecastInputs? forecast = null, PlanState? state = null, EvPublished? ev = null)
    {
        var cause = ev?.Snapshot is { } evSnapshot ? EvAssessmentCodes.Text(evSnapshot.Composition.Code) : null;
        var planner = result?.Configuration?.Planner;
        var parameters = result?.Configuration?.Control.Planner;
        var plan = planner is not null ? state?.Plan : null;
        var status = planner is null || state is null ? PlanStatus.Absent : state.StatusAt(board.At);

        // With a plan the slots and the inputs shown are the ones it was computed on, so that the entries and the slots agree; without one they are the
        // slots as they stand now (slice 3).
        var inputs = plan?.Inputs ?? (planner is not null && sources is not null && parameters is not null
            ? PlanInputs.Build(board.At, board.At + parameters.Horizon, null, parameters.Forecast(), sources)
            : null);
        var devices = new SortedDictionary<string, List<PlanReportGoal>>(StringComparer.Ordinal);
        foreach (var goal in board.Goals)
        {
            Of(devices, goal.Request.Device.Value).Add(new PlanReportGoal(goal, null, Judged(goal.Request, plan, status), goal.Request.Origin == GoalOrigin.Module ? CauseOf(ev?.Snapshot, goal.Request.Revision, cause) : null));
        }

        foreach (var inactive in board.Inactive)
        {
            Of(devices, inactive.Device.Value).Add(new PlanReportGoal(null, inactive, null, inactive.Origin == GoalOrigin.Module ? cause : null));
        }

        return new PlanReport(
            board.At,
            planner,
            parameters,
            result?.Configuration?.Economics?.Currency,
            status,
            inputs,
            inputs is null ? null : forecast?.ForRole(SignalRole.PvForecast) ?? forecast?.ForRole(SignalRole.PvForecastToday),
            plan,
            state,
            board.Goals.Length,
            board.Inactive,
            [.. devices.Select(d => new PlanReportDevice(d.Key, d.Value, plan?.Device(DeviceName.Parse(d.Key))))],
            [.. board.Superseded],
            NotesOf(inputs, plan, state, planner is null ? [] : board.Inactive),
            IssuesOf(result));
    }

    /// <summary>The input notes, the plan's own (a goal at risk, a shortfall) and the service's (a held solve), once each, in the order of the list.</summary>
    private static IReadOnlyList<PlanNote> NotesOf(PlanInputs? inputs, PlanSnapshot? plan, PlanState? state, ImmutableArray<InactiveGoal> inactive) =>
        [.. (plan?.Notes ?? inputs?.Notes ?? []).Concat(plan is null ? [] : state?.Notes ?? []).Concat(inactive.Any(i => i.Reason is GoalInactiveReason.InputStale or GoalInactiveReason.InputUnavailable) ? [PlanNote.GoalInputStale] : []).Distinct().Order()];

    /// <summary>The plan's verdict on a goal (at risk, infeasible, planned energy), only while the plan is fresh.</summary>
    private static PlanGoal? Judged(GoalRequest r, PlanSnapshot? plan, PlanStatus status) =>
        status == PlanStatus.Fresh ? plan?.Device(r.Device)?.Goals.FirstOrDefault(g => g.Ref.Revision == r.Revision && g.Deadline == r.Deadline) : null;

    private static List<PlanReportGoal> Of(SortedDictionary<string, List<PlanReportGoal>> devices, string device)
    {
        if (!devices.TryGetValue(device, out var list))
        {
            list = [];
            devices[device] = list;
        }

        return list;
    }

    /// <summary>
    /// Why the module wants this very goal: its own kind says (trip, weekly full charge, surplus cap, floor); a goal the snapshot does not list (an inactive one)
    /// or a PV-only mode falls back to the assessment's primary code.
    /// </summary>
    private static string? CauseOf(EvSnapshot? snapshot, long revision, string? fallback)
    {
        if (snapshot is null || snapshot.Composition.Code == EvAssessmentCode.PvOnly)
        {
            return fallback;
        }

        var offer = snapshot.Composition.Goals.FirstOrDefault(g => g.Request.Revision == revision);
        return offer?.Kind switch
        {
            EvGoalKind.Trip => EvAssessmentCodes.Text(EvAssessmentCode.TripDeadline),
            EvGoalKind.WeeklyFull => EvAssessmentCodes.Text(EvAssessmentCode.WeeklyFull),
            EvGoalKind.Cap => EvAssessmentCodes.Text(EvAssessmentCode.Surplus),
            EvGoalKind.Floor => EvAssessmentCodes.Text(GoalMath.IsMet(offer.Request.RemainingKwh) ? EvAssessmentCode.FloorHold : EvAssessmentCode.CheapZone),
            _ => fallback,
        };
    }

    private static IReadOnlyList<ConfigurationIssue> IssuesOf(ConfigurationResult? result) =>
        result is null
            ? []
            : [.. result.Errors.Concat(result.Warnings).Where(i => PlanPath().IsMatch(i.Path))];

    [GeneratedRegex("^(planner([.\\[]|$)|devices\\[\\d+\\]\\.goal([.\\[]|$))", RegexOptions.CultureInvariant)]
    private static partial Regex PlanPath();
}

/// <summary>
/// Reads the plan report from the live sources (the goal board, the planner service's plan, the load profile, the EV module's snapshot). The one place
/// GET api/plan gets its answer from; the web layer maps it and nothing more. It is handed the configuration and forecast of the latest signal cycle, so it
/// never holds the full signal snapshot.
/// </summary>
public sealed class PlanReader(SignalClock clock, GoalTracker goals, LoadProfileFeed loadProfile, PlanHolder plans, IEvSnapshotSource ev)
{
    /// <param name="configuration">The configuration in force (valid or not); null before the first load.</param>
    /// <param name="forecast">The forecast signals of the latest store cycle.</param>
    public PlanReport Read(ConfigurationResult? configuration, ForecastInputs? forecast)
    {
        var now = clock.Time.GetUtcNow();
        var sources = configuration?.Configuration is { Planner: not null } valid
            ? PlanSourceMapper.Map(valid, forecast, loadProfile.Current, clock.Time.LocalTimeZone, now)
            : null;
        return PlanReport.Compose(configuration, goals.Evaluate(now), sources, forecast, plans.Current, ev.Current);
    }
}
