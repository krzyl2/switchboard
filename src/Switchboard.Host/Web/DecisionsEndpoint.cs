using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Switchboard.Application.Decisions;
using Switchboard.Application.Options;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;

namespace Switchboard.Host.Web;

/// <summary>Shape of GET api/decisions: recent decisions, newest first (MODE-03). <c>next</c> is the cursor for the older page.</summary>
public sealed record DecisionsResponse(IReadOnlyList<DecisionDto> Items, string? Next, bool LogWritable);

/// <summary>One decision as the API shows it; codes and enum values are snake_case, the reason as 04 §6 writes it.</summary>
public sealed record DecisionDto(
    string Id,
    DateTimeOffset At,
    string Scope,
    string? Device,
    int? Stage,
    string? Phase,
    string Action,
    string Reason,
    string? Overridden,
    string Mode,
    bool Commanded,
    string? RefusedBy,
    DecisionInputsDto Inputs);

public sealed record DecisionInputsDto(PhaseInputsDto? Phase, DeviceInputsDto? Device, ModeInputsDto? Mode, string? KillSwitch, string? SafeStateCause);

public sealed record PhaseInputsDto(
    string Phase,
    bool DataValid,
    string? InvalidReason,
    double? CurrentA,
    double? EffectiveCurrentA,
    double? Multiple,
    int? Band,
    string? Reaction,
    double Budget,
    double ContinuousA,
    double ReserveThresholdA,
    double? HeadroomA,
    string? Direction,
    DateTimeOffset? ReportedAt);

public sealed record DeviceInputsDto(
    string? Applied,
    double? SetpointA,
    bool StateFresh,
    string? StateInvalidReason,
    int? ShedRank,
    double ValuePerKwh,
    double? ShedPriority,
    double MinOnRemainingS,
    double MinOffRemainingS,
    bool CanSwitchOff);

public sealed record ModeInputsDto(string Ceiling, string UiChoice, string Note);

internal static class DecisionsEndpoint
{
    /// <summary>The page size when the request names none; the API's default, not a control parameter.</summary>
    public const int DefaultLimit = 50;

    public static IEndpointRouteBuilder MapDecisionsApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/decisions", (
            HttpRequest request,
            [FromServices] IDecisionRecorder recorder,
            [FromServices] IOptions<PersistenceOptions> options) =>
        {
            if (Parse(request.Query, options.Value.DecisionMaxPageSize) is not { } query)
            {
                return Results.Problem(
                    $"Filters: device (a device name), phase (L1, L2, L3), reason (a reason code of list version {ReasonCodes.ListVersion}), limit (1 to {options.Value.DecisionMaxPageSize}), before (a cursor from 'next').",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                var page = recorder.Query(query);
                return Results.Json(new DecisionsResponse([.. page.Items.Select(Dto)], page.Next, page.LogWritable));
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });
        return endpoints;
    }

    /// <summary>The query, or null when any parameter is present but invalid (or repeated).</summary>
    internal static DecisionQuery? Parse(IQueryCollection query, int maxLimit)
    {
        if (query.Any(p => p.Value.Count > 1))
        {
            return null;
        }

        DeviceName? device = null;
        if (query.TryGetValue("device", out var deviceText) && !DeviceName.TryParse(deviceText, out device))
        {
            return null;
        }

        Phase? phase = null;
        if (query.TryGetValue("phase", out var phaseText))
        {
            phase = Enum.GetValues<Phase>().Where(p => p.ToString() == phaseText).Select(p => (Phase?)p).SingleOrDefault();
            if (phase is null)
            {
                return null;
            }
        }

        ReasonCode? reason = null;
        if (query.TryGetValue("reason", out var reasonText))
        {
            reason = ReasonCodes.All.Where(c => ReasonCodes.Name(c) == reasonText).Select(c => (ReasonCode?)c).SingleOrDefault();
            if (reason is null)
            {
                return null;
            }
        }

        var limit = DefaultLimit;
        if (query.TryGetValue("limit", out var limitText)
            && !(int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) && limit >= 1 && limit <= maxLimit))
        {
            return null;
        }

        string? before = null;
        if (query.TryGetValue("before", out var beforeText))
        {
            before = beforeText.ToString();
            if (before.Length == 0)
            {
                return null;
            }
        }

        return new DecisionQuery(device, phase, reason, Math.Min(limit, maxLimit), before);
    }

    internal static DecisionDto Dto(Decision decision) => new(
        decision.Id,
        decision.At,
        ApiText.Snake(decision.Scope),
        decision.Device?.Value,
        decision.Stage,
        decision.Phase?.ToString(),
        ApiText.Snake(decision.Action),
        ReasonCodes.Name(decision.Reason),
        decision.Overridden is { } overridden ? ReasonCodes.Name(overridden) : null,
        ApiText.Snake(decision.Mode),
        decision.Commanded,
        decision.RefusedBy is { } refused ? ReasonCodes.Name(refused) : null,
        new DecisionInputsDto(
            decision.Inputs.Phase is { } p
                ? new PhaseInputsDto(
                    p.Phase.ToString(),
                    p.DataValid,
                    ApiText.SnakeOrNull(p.InvalidReason),
                    p.Current?.Value,
                    p.EffectiveCurrent?.Value,
                    p.Multiple,
                    p.Band,
                    ApiText.SnakeOrNull(p.Reaction),
                    p.Budget,
                    p.Continuous.Value,
                    p.ReserveThreshold.Value,
                    p.Headroom?.Value,
                    ApiText.SnakeOrNull(p.Direction),
                    p.ReportedAt)
                : null,
            decision.Inputs.Device is { } d
                ? new DeviceInputsDto(
                    ApiText.Applied(d.Applied),
                    ApiText.Setpoint(d.Applied),
                    d.StateFresh,
                    ApiText.SnakeOrNull(d.StateInvalidReason),
                    d.ShedRank,
                    d.ValuePerKwh,
                    d.ShedPriority,
                    d.MinOnRemaining.TotalSeconds,
                    d.MinOffRemaining.TotalSeconds,
                    d.CanSwitchOff)
                : null,
            decision.Inputs.Mode is { } m ? new ModeInputsDto(ApiText.Snake(m.Ceiling), ApiText.Snake(m.UiChoice), ApiText.Snake(m.Note)) : null,
            ApiText.SnakeOrNull(decision.Inputs.KillSwitch),
            ApiText.SnakeOrNull(decision.Inputs.SafeStateCause)));
}

/// <summary>How enum values and applied states are spelled in the API: snake_case, like api/signals.</summary>
internal static class ApiText
{
    public static string Snake<TEnum>(TEnum value)
        where TEnum : struct, Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    public static string? SnakeOrNull<TEnum>(TEnum? value)
        where TEnum : struct, Enum => value is { } known ? Snake(known) : null;

    public static string? Applied(Domain.Devices.AppliedState? applied) =>
        applied is RelayApplied relay ? (relay.On ? "on" : "off") : null;

    public static double? Setpoint(Domain.Devices.AppliedState? applied) =>
        applied is SetpointApplied setpoint ? setpoint.Setpoint.Value : null;
}
