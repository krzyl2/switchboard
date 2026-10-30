using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
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

namespace Switchboard.Host.Web;

/// <summary>
/// GET api/plan (PLAN-01 to PLAN-05, PLAN-10 to PLAN-12; read-only, ingress only, no new port; nothing here changes a decision). Whether a
/// <c>planner:</c> section exists, its dispatch mode as configured, every device goal with its status or the reason it is inactive, the slots with
/// their inputs and, since slice 4, the plan the planner service holds: its id and age, per device an entry for every slot, the cost and two references,
/// the notes and the last solve. Nothing reads the plan yet; <see cref="Status"/> says whether it is fresh, stale, suspended, absent or an error.
/// </summary>
/// <param name="Enabled">False without a <c>planner:</c> section (a 0.10 file); the goals are still shown, they simply feed nothing.</param>
/// <param name="DispatchMode">off, narrow or full as configured; null when not enabled. Nothing reads it before the plan reaches dispatch.</param>
/// <param name="Status">The plan's state: fresh, stale (older than <c>planner.plan_max_age</c> or from the future), suspended, absent (none held) or error (the last solve failed and none is held).</param>
/// <param name="Inputs">The goal inputs' state.</param>
/// <param name="Devices">Per device with a goal: its goals ordered by deadline, or the reason the goal is inactive.</param>
/// <param name="Superseded">Requests the precedence merge set aside (manual over module over entity over config): shown, not applied.</param>
/// <param name="Notes">What the inputs and the plan say about themselves: a missing or partial forecast, a goal at risk or infeasible, a solve that was held back.</param>
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
    IReadOnlyList<ConfigurationIssue> Issues)
{
    /// <summary>The slots from now to the horizon with their inputs; empty without a <c>planner:</c> section. With a plan these are the slots it was computed on.</summary>
    public IReadOnlyList<PlanSlotDto> Slots { get; init; } = [];

    /// <summary>The plan's fingerprint; null while there is no plan.</summary>
    public string? Id { get; init; }

    /// <summary>When the plan held now was computed.</summary>
    public DateTimeOffset? ComputedAt { get; init; }

    /// <summary>When the plan stops being usable: <c>computedAt</c> plus <c>planner.plan_max_age</c>.</summary>
    public DateTimeOffset? ValidUntil { get; init; }

    public DateTimeOffset? HorizonEnd { get; init; }

    public double? SlotMinutes { get; init; }

    /// <summary>The configuration the plan was made for (a counter of the loaded configuration, not a content hash).</summary>
    public long? ConfigRevision { get; init; }

    /// <summary>The guard is in safe state, the kill switch is engaged or Home Assistant is disconnected: the plan is ignored and none is made.</summary>
    public bool Suspended { get; init; }

    /// <summary>What the plan cost and two references; null while there is no plan.</summary>
    public PlanCostDto? Cost { get; init; }

    /// <summary>The last attempt to compute a plan; null before the first.</summary>
    public SolveDto? LastSolve { get; init; }
}

/// <param name="Planned">The plan's own cost in the configuration's currency.</param>
/// <param name="ReferenceV2">The same goals delivered as soon as possible on what the devices' own sources allow (v2 on its own).</param>
/// <param name="ReferenceAsap">The same goals delivered as soon as possible on any energy.</param>
/// <param name="Currency">The label from <c>economics.currency</c>, verbatim; null without an <c>economics:</c> section.</param>
public sealed record PlanCostDto(double Planned, double ReferenceV2, double ReferenceAsap, string? Currency);

/// <param name="Result">computed, timeout, invalid or failed.</param>
/// <param name="Reason">What made the planner run: first, cadence, goal, progress, config, device_state, safe_state_exit, clock_step, clock_step_back.</param>
/// <param name="Violations">The verifier's findings when the plan was refused (codes), else empty.</param>
public sealed record SolveDto(DateTimeOffset At, double DurationMs, string Result, string Reason, IReadOnlyList<string> Violations);

/// <param name="Want">run, defer, done, urgent or none.</param>
/// <param name="Class">pv or zone:&lt;id&gt; for run and urgent; null otherwise.</param>
/// <param name="EnergyKwh">The slot's budget for the device.</param>
/// <param name="MaxPowerW">The ceiling for the slot; zero unless run or urgent.</param>
public sealed record PlanEntryDto(DateTimeOffset Start, DateTimeOffset End, string Want, string? Class, double EnergyKwh, double MaxPowerW);

/// <param name="PvForecast">The PV forecast the slots stand on; null without a <c>planner:</c> section.</param>
/// <param name="LoadForecast">The house load forecast; null without a <c>planner:</c> section.</param>
/// <param name="Prices">The import price basis; null without a <c>planner:</c> section.</param>
/// <param name="GoalsActive">Goals that could be evaluated now (met and expired ones included).</param>
/// <param name="GoalsInactive">Goals that could not, with the input and why; never guessed.</param>
public sealed record PlanInputsDto(
    PvForecastDto? PvForecast,
    LoadForecastDto? LoadForecast,
    PlanPricesDto? Prices,
    int GoalsActive,
    IReadOnlyList<GoalInactiveDto> GoalsInactive);

/// <param name="State">The signal's state as the signal layer reports it (fresh, stale, unavailable, rejected), or absent when no PV forecast signal is mapped.</param>
/// <param name="AgeS">Seconds since the forecast was reported (last_reported); null when none arrived.</param>
/// <param name="CoversUntil">Where the forecast ends; null when there is none. Coverage is not freshness.</param>
/// <param name="Basis">series, totals or none (no usable forecast: zero PV).</param>
/// <param name="Derate">The factor the PV energy is multiplied by.</param>
public sealed record PvForecastDto(string State, double? AgeS, DateTimeOffset? CoversUntil, string Basis, double Derate);

/// <param name="Basis">profile (every bin filled), partial (the mean of the filled ones stands in) or fallback (no bin filled yet).</param>
/// <param name="DaysFilled">Whole days of 24 filled bins.</param>
public sealed record LoadForecastDto(string Basis, int DaysFilled);

/// <param name="Basis">tariff (no curve), curve (it prices every slot) or partial (the tariff prices the rest).</param>
/// <param name="CoversUntil">Where the import curve ends; null without one.</param>
public sealed record PlanPricesDto(string Basis, DateTimeOffset? CoversUntil);

/// <summary>One planning slot: local boundaries (with the offset in force, so a DST day shows 23 or 25 hours), prices, derated PV and house load.</summary>
/// <param name="Zone">The import tariff's zone when one zone holds the whole slot, else null.</param>
/// <param name="ImportPrice">Currency per kWh, time-weighted over the slot; null where no price exists.</param>
/// <param name="ExportPrice">Currency per kWh; null where none is configured.</param>
/// <param name="SurplusKwh">PV energy left after the house load.</param>
public sealed record PlanSlotDto(
    DateTimeOffset Start,
    DateTimeOffset End,
    string? Zone,
    double? ImportPrice,
    double? ExportPrice,
    double PvKwh,
    double BaseKwh,
    double SurplusKwh);

/// <param name="Reason">want_off, input_missing, input_stale, input_unavailable, input_invalid or input_out_of_range.</param>
/// <param name="Input">soc, target, deadline or want.</param>
public sealed record GoalInactiveDto(string Device, string Reason, string Input);

/// <param name="Goal">The goal the planner works on first (the earliest deadline); null for a device that has none.</param>
/// <param name="Goals">Every goal of the device, ordered by deadline; an inactive goal has status inactive.</param>
public sealed record PlanDeviceDto(string Device, GoalDto? Goal, IReadOnlyList<GoalDto> Goals)
{
    /// <summary>One entry per slot of the plan (run, defer, done, urgent or none) while a plan is held (see <c>status</c>: a stale plan is shown as such); empty before the first.</summary>
    public IReadOnlyList<PlanEntryDto> Entries { get; init; } = [];

    /// <summary>Energy of the device's goals the plan could not place.</summary>
    public double? ShortfallKwh { get; init; }

    /// <summary>When the planned energy of the first goal is delivered; null with a shortfall or nothing planned.</summary>
    public DateTimeOffset? PredictedCompletion { get; init; }
}

/// <param name="Kind">energy or soc.</param>
/// <param name="Source">config, entity, module or manual.</param>
/// <param name="TargetKwh">An energy goal's target.</param>
/// <param name="TargetSoc">A SoC goal's target percent.</param>
/// <param name="CurrentSoc">A SoC goal's state of charge now.</param>
/// <param name="Deadline">When the goal is due; null means the horizon end.</param>
/// <param name="MarginS">The plan finishes this many seconds before the deadline.</param>
/// <param name="RemainingKwh">Energy still to draw, loss fraction included; null while the goal is inactive.</param>
/// <param name="DeliveredKwh">An energy goal's progress from the ledger.</param>
/// <param name="Status">on_track, at_risk, infeasible, met, expired, missed or inactive (at risk and infeasible are the plan's verdict, so only while a fresh plan has judged the goal).</param>
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
    DateTimeOffset? Since)
{
    /// <summary>Energy the plan places for the goal; null without a plan.</summary>
    public double? PlannedKwh { get; init; }

    /// <summary>Need the plan could not place; null without a plan.</summary>
    public double? ShortfallKwh { get; init; }

    /// <summary>The last grid-only start that still makes a hard deadline; null for a goal without one.</summary>
    public DateTimeOffset? LatestGridStart { get; init; }

    /// <summary>A hard goal that is urgent (its latest safe start has come, it is past its deadline or it added zones).</summary>
    public bool? Urgent { get; init; }

    /// <summary>The device a request belongs to; set only on a request the merge set aside (listed apart from the devices), null on a device's own goals.</summary>
    public string? Device { get; init; }

    /// <summary>Why the EV module wants this goal (its own closed assessment list, <c>ev_trip_deadline</c> ...); null on a goal of any other source.</summary>
    public string? Cause { get; init; }
}

internal static partial class PlanEndpoint
{
    public static IEndpointRouteBuilder MapPlanApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/plan", (
            [FromServices] ISignalDiagnosticsSource diagnostics,
            [FromServices] SignalClock clock,
            [FromServices] GoalTracker goals,
            [FromServices] LoadProfileFeed loadProfile,
            [FromServices] PlanHolder plans,
            [FromServices] IEvSnapshotSource ev) =>
        {
            var now = clock.Time.GetUtcNow();
            var current = diagnostics.Current;
            var sources = current.Configuration?.Configuration is { Planner: not null } configuration
                ? PlanSourceMapper.Map(configuration, current.Snapshot.Forecast, loadProfile.Current, clock.Time.LocalTimeZone, now)
                : null;
            return Results.Json(Build(current.Configuration, goals.Evaluate(now), sources, current.Snapshot.Forecast, plans.Current, ev.Current));
        });
        return endpoints;
    }

    /// <param name="sources">The plan's sources (see <see cref="PlanSourceMapper"/>); null leaves the inputs and slots out, as without a planner.</param>
    /// <param name="forecast">The forecast signals of the latest store cycle, for the PV forecast's state and age.</param>
    /// <param name="state">What the planner service holds (the last adopted plan, its status, the last solve); null or empty without a planner.</param>
    /// <param name="ev">The EV module's published snapshot; its assessment code is the <c>cause</c> of the module's goals.</param>
    internal static PlanResponse Build(ConfigurationResult? result, GoalBoard board, PlanInputSources? sources = null, ForecastInputs? forecast = null, PlanState? state = null, EvPublished? ev = null)
    {
        var cause = ev?.Snapshot is { } evSnapshot ? EvAssessmentCodes.Text(evSnapshot.Composition.Code) : null;
        var planner = result?.Configuration?.Planner;
        var parameters = result?.Configuration?.Control.Planner;
        var plan = planner is not null ? state?.Plan : null;
        var status = planner is null || state is null ? PlanStatus.Absent : state.StatusAt(board.At);

        // With a plan the slots and the inputs shown are the ones it was computed on, so that the entries and the slots agree; without one they are the
        // slots as they stand now (slice 3).
        var planInputs = plan?.Inputs ?? (planner is not null && sources is not null && parameters is not null
            ? PlanInputs.Build(board.At, board.At + parameters.Horizon, null, parameters.Forecast(), sources)
            : null);
        var devices = new SortedDictionary<string, List<GoalDto>>(StringComparer.Ordinal);
        foreach (var goal in board.Goals)
        {
            Of(devices, goal.Request.Device.Value).Add(Applied(goal, plan, status, CauseOf(ev?.Snapshot, goal.Request.Revision, cause)));
        }

        foreach (var inactive in board.Inactive)
        {
            Of(devices, inactive.Device.Value).Add(Inactive(inactive, cause));
        }

        return new PlanResponse(
            board.At,
            planner is not null,
            planner is { } p ? ApiText.Snake(p.Dispatch) : null,
            ApiText.Snake(status),
            new PlanInputsDto(
                planInputs is null ? null : PvForecast(planInputs, parameters!, forecast?.ForRole(SignalRole.PvForecast) ?? forecast?.ForRole(SignalRole.PvForecastToday), board.At),
                planInputs is null ? null : new LoadForecastDto(ApiText.Snake(planInputs.LoadBasis), planInputs.LoadDaysFilled),
                planInputs is null ? null : new PlanPricesDto(ApiText.Snake(planInputs.ImportPriceBasis), planInputs.ImportCurveCoversUntil),
                board.Goals.Length,
                [.. board.Inactive.Select(i => new GoalInactiveDto(i.Device.Value, ApiText.Snake(i.Reason), ApiText.Snake(i.Input)))]),
            [.. devices.Select(d => Device(d.Key, d.Value, plan))],
            [.. board.Superseded.Select(r => Applied(new Goal(r, GoalStatus.OnTrack), null, PlanStatus.Absent) with { Status = "superseded", Device = r.Device.Value })],
            Notes(planInputs, plan, state, planner is null ? [] : board.Inactive),
            Issues(result))
        {
            Slots = planInputs is null ? [] : [.. planInputs.Slots.Select(Slot)],
            Id = plan?.Id,
            ComputedAt = plan?.ComputedAt,
            ValidUntil = plan is not null && state is not null ? plan.ComputedAt + state.PlanMaxAge : null,
            HorizonEnd = plan?.HorizonEnd,
            SlotMinutes = parameters is null || planner is null ? null : parameters.Slot.TotalMinutes,
            ConfigRevision = plan?.ConfigRevision,
            Suspended = planner is not null && state is { Suspended: true },
            Cost = plan is null ? null : new PlanCostDto(plan.Cost.Planned, plan.Cost.ReferenceV2, plan.Cost.ReferenceAsap, result?.Configuration?.Economics?.Currency),
            LastSolve = planner is null ? null : Solve(state?.LastSolve),
        };
    }

    private static SolveDto? Solve(SolveReport? report) =>
        report is null
            ? null
            : new SolveDto(report.At, report.Duration.TotalMilliseconds, ApiText.Snake(report.Result), report.Reason, [.. report.Violations.Select(v => ApiText.Snake(v.Code)).Distinct()]);

    /// <summary>The input notes, the plan's own (a goal at risk, a shortfall) and the service's (a held solve), once each in the order of the list.</summary>
    private static IReadOnlyList<string> Notes(PlanInputs? inputs, PlanSnapshot? plan, PlanState? state, ImmutableArray<InactiveGoal> inactive) =>
        [.. (plan?.Notes ?? inputs?.Notes ?? []).Concat(plan is null ? [] : state?.Notes ?? []).Concat(inactive.Any(i => i.Reason is GoalInactiveReason.InputStale or GoalInactiveReason.InputUnavailable) ? [PlanNote.GoalInputStale] : []).Distinct().Order().Select(n => ApiText.Snake(n))];

    private static PlanDeviceDto Device(string name, List<GoalDto> goals, PlanSnapshot? plan)
    {
        var planned = plan?.Device(DeviceName.Parse(name));
        var first = planned?.Goals.FirstOrDefault();
        return new PlanDeviceDto(name, goals.FirstOrDefault(), goals)
        {
            Entries = planned is null ? [] : [.. planned.Entries.Select(e => new PlanEntryDto(e.Start, e.End, ApiText.Snake(e.Want), e.Class, e.EnergyKwh, e.MaxPowerW))],
            ShortfallKwh = planned is null ? null : planned.Goals.Sum(g => g.ShortfallKwh),
            PredictedCompletion = first?.PredictedCompletion,
        };
    }

    private static PvForecastDto PvForecast(PlanInputs inputs, PlannerParameters parameters, ForecastSample? sample, DateTimeOffset now) =>
        new(
            sample is null ? "absent" : ApiText.Snake(sample.State),
            sample?.ReportedAt is { } reported ? (now - reported).TotalSeconds : null,
            inputs.PvCoversUntil,
            ApiText.Snake(inputs.PvBasis),
            parameters.PvDerate);

    private static PlanSlotDto Slot(PlanInputSlot s) =>
        new(s.Slot.Start, s.Slot.End, s.Zone, s.Import?.PerKwh, s.Export?.PerKwh, s.PvKwh, s.BaseKwh, s.SurplusKwh);

    private static List<GoalDto> Of(SortedDictionary<string, List<GoalDto>> devices, string device)
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

    /// <summary>A goal as shown; the status is the plan's (at risk, infeasible) while a fresh plan has judged the goal, else the goal model's.</summary>
    private static GoalDto Applied(Goal goal, PlanSnapshot? plan, PlanStatus status, string? cause = null)
    {
        var r = goal.Request;
        var judged = status == PlanStatus.Fresh ? plan?.Device(r.Device)?.Goals.FirstOrDefault(g => g.Ref.Revision == r.Revision && g.Deadline == r.Deadline) : null;
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
            ApiText.Snake(judged?.Status ?? goal.Status),
            null,
            null,
            ApiText.Snake(r.OnMet),
            r.Escalate,
            r.Sources?.ToText(),
            r.Since)
        {
            PlannedKwh = judged?.PlannedKwh,
            ShortfallKwh = judged?.ShortfallKwh,
            LatestGridStart = judged?.LatestGridStart,
            Urgent = judged?.Urgent,
            Cause = r.Origin == GoalOrigin.Module ? cause : null,
        };
    }

    private static GoalDto Inactive(InactiveGoal goal, string? cause = null) =>
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
            null)
        {
            Cause = goal.Origin == GoalOrigin.Module ? cause : null,
        };

    private static IReadOnlyList<ConfigurationIssue> Issues(ConfigurationResult? result) =>
        result is null
            ? []
            : [.. result.Errors.Concat(result.Warnings).Where(i => PlanPath().IsMatch(i.Path))];

    [GeneratedRegex("^(planner([.\\[]|$)|devices\\[\\d+\\]\\.goal([.\\[]|$))", RegexOptions.CultureInvariant)]
    private static partial Regex PlanPath();
}
