using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Switchboard.Application.Configuration;
using Switchboard.Application.Goals;
using Switchboard.Application.Signals;
using Switchboard.Domain.Goals;

namespace Switchboard.Host.Web;

/// <summary>
/// GET api/plan (PLAN-01 to PLAN-05; read-only, ingress only, no new port; nothing here changes a decision). Slice 1 of the planner
/// shows the goals only: whether a <c>planner:</c> section exists, its dispatch mode as configured, and every device goal with its
/// status or the reason it is inactive. There is no plan yet, so <see cref="Status"/> is <c>absent</c> and no slots, entries or cost exist.
/// </summary>
/// <param name="Enabled">False without a <c>planner:</c> section (a 0.10 file); the goals are still shown, they simply feed nothing.</param>
/// <param name="DispatchMode">off, narrow or full as configured; null when not enabled. Nothing reads it before the plan reaches dispatch.</param>
/// <param name="Status">The plan's state: always <c>absent</c> in this slice.</param>
/// <param name="Inputs">The goal inputs' state.</param>
/// <param name="Devices">Per device with a goal: its goals ordered by deadline, or the reason the goal is inactive.</param>
/// <param name="Superseded">Requests the precedence merge set aside (manual over module over entity over config): shown, not applied.</param>
/// <param name="Notes">Empty in this slice.</param>
/// <param name="Issues">Configuration issues under <c>planner</c> and under a device's <c>goal</c>, errors first.</param>
public sealed record PlanResponse(
    DateTimeOffset At,
    bool Enabled,
    string? DispatchMode,
    string Status,
    PlanInputsDto Inputs,
    IReadOnlyList<PlanDeviceDto> Devices,
    IReadOnlyList<GoalDto> Superseded,
    IReadOnlyList<string> Notes,
    IReadOnlyList<ConfigurationIssue> Issues);

/// <param name="GoalsActive">Goals that could be evaluated now (met and expired ones included).</param>
/// <param name="GoalsInactive">Goals that could not, with the input and why; never guessed.</param>
public sealed record PlanInputsDto(int GoalsActive, IReadOnlyList<GoalInactiveDto> GoalsInactive);

/// <param name="Reason">want_off, input_missing, input_stale, input_unavailable, input_invalid or input_out_of_range.</param>
/// <param name="Input">soc, target, deadline or want.</param>
public sealed record GoalInactiveDto(string Device, string Reason, string Input);

/// <param name="Goal">The goal the planner works on first (the earliest deadline); null for a device that has none.</param>
/// <param name="Goals">Every goal of the device, ordered by deadline; an inactive goal has status inactive.</param>
public sealed record PlanDeviceDto(string Device, GoalDto? Goal, IReadOnlyList<GoalDto> Goals);

/// <param name="Kind">energy or soc.</param>
/// <param name="Source">config, entity, module or manual.</param>
/// <param name="TargetKwh">An energy goal's target.</param>
/// <param name="TargetSoc">A SoC goal's target percent.</param>
/// <param name="CurrentSoc">A SoC goal's state of charge now.</param>
/// <param name="Deadline">When the goal is due; null means the horizon end.</param>
/// <param name="MarginS">The plan finishes this many seconds before the deadline.</param>
/// <param name="RemainingKwh">Energy still to draw, loss fraction included; null while the goal is inactive.</param>
/// <param name="DeliveredKwh">An energy goal's progress from the ledger.</param>
/// <param name="Status">on_track, at_risk, infeasible, met, expired, missed or inactive (at risk and infeasible need the plan, so not before slice 4).</param>
/// <param name="InactiveReason">Why the goal is inactive.</param>
/// <param name="InactiveInput">Which input failed.</param>
/// <param name="OnMet">opportunistic or stop.</param>
/// <param name="Sources">The classes the goal may be scheduled on; null means the device's.</param>
/// <param name="Since">When an energy goal began, from the ledger.</param>
public sealed record GoalDto(
    string Kind,
    bool Hard,
    string Source,
    double? TargetKwh,
    double? TargetSoc,
    double? CurrentSoc,
    DateTimeOffset? Deadline,
    double MarginS,
    double? RemainingKwh,
    double? DeliveredKwh,
    string Status,
    string? InactiveReason,
    string? InactiveInput,
    string? OnMet,
    bool Escalate,
    string? Sources,
    DateTimeOffset? Since);

internal static partial class PlanEndpoint
{
    public static IEndpointRouteBuilder MapPlanApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/plan", (
            [FromServices] ISignalDiagnosticsSource diagnostics,
            [FromServices] SignalClock clock,
            [FromServices] GoalTracker goals) =>
        {
            var now = clock.Time.GetUtcNow();
            return Results.Json(Build(diagnostics.Current.Configuration, goals.Evaluate(now)));
        });
        return endpoints;
    }

    internal static PlanResponse Build(ConfigurationResult? result, GoalBoard board)
    {
        var planner = result?.Configuration?.Planner;
        var devices = new SortedDictionary<string, List<GoalDto>>(StringComparer.Ordinal);
        foreach (var goal in board.Goals)
        {
            Of(devices, goal.Request.Device.Value).Add(Applied(goal));
        }

        foreach (var inactive in board.Inactive)
        {
            Of(devices, inactive.Device.Value).Add(Inactive(inactive));
        }

        return new PlanResponse(
            board.At,
            planner is not null,
            planner is { } p ? ApiText.Snake(p.Dispatch) : null,
            "absent",
            new PlanInputsDto(board.Goals.Length, [.. board.Inactive.Select(i => new GoalInactiveDto(i.Device.Value, ApiText.Snake(i.Reason), ApiText.Snake(i.Input)))]),
            [.. devices.Select(d => new PlanDeviceDto(d.Key, d.Value.FirstOrDefault(), d.Value))],
            [.. board.Superseded.Select(r => Applied(new Goal(r, GoalStatus.OnTrack)) with { Status = "superseded" })],
            [],
            Issues(result));
    }

    private static List<GoalDto> Of(SortedDictionary<string, List<GoalDto>> devices, string device)
    {
        if (!devices.TryGetValue(device, out var list))
        {
            list = [];
            devices[device] = list;
        }

        return list;
    }

    private static GoalDto Applied(Goal goal)
    {
        var r = goal.Request;
        return new GoalDto(
            ApiText.Snake(r.Kind),
            r.Hard,
            ApiText.Snake(r.Origin),
            r.TargetKwh,
            r.TargetSoc,
            r.CurrentSoc,
            r.Deadline,
            r.Margin.TotalSeconds,
            r.RemainingKwh,
            r.DeliveredKwh,
            ApiText.Snake(goal.Status),
            null,
            null,
            ApiText.Snake(r.OnMet),
            r.Escalate,
            r.Sources?.ToText(),
            r.Since);
    }

    private static GoalDto Inactive(InactiveGoal goal) =>
        new(
            ApiText.Snake(goal.Kind),
            goal.Hard,
            ApiText.Snake(goal.Origin),
            null,
            null,
            null,
            null,
            0,
            null,
            null,
            ApiText.Snake(GoalStatus.Inactive),
            ApiText.Snake(goal.Reason),
            ApiText.Snake(goal.Input),
            null,
            false,
            null,
            null);

    private static IReadOnlyList<ConfigurationIssue> Issues(ConfigurationResult? result) =>
        result is null
            ? []
            : [.. result.Errors.Concat(result.Warnings).Where(i => PlanPath().IsMatch(i.Path))];

    [GeneratedRegex("^(planner([.\\[]|$)|devices\\[\\d+\\]\\.goal([.\\[]|$))", RegexOptions.CultureInvariant)]
    private static partial Regex PlanPath();
}
