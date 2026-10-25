using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Options;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;

namespace Switchboard.Host.Web;

/// <summary>Shape of GET api/decisions: recent decisions, newest first (MODE-03). <c>next</c> is the cursor for the older page.</summary>
public sealed record DecisionsResponse(IReadOnlyList<DecisionDto> Items, string? Next, bool LogWritable);

/// <summary>One decision as the API shows it; codes and enum values are snake_case, the reason as 04 §6 writes it.</summary>
/// <param name="Origin">guard, dispatch or override for a device change; null for events and for 0.3.x decisions.</param>
/// <param name="Group">The circuit group the decision is about or was caused by.</param>
/// <param name="Target">The device's target level after the decision (what Switchboard intends; in shadow mode nothing is applied).</param>
/// <param name="Kind">protection, dispatch, override, verification, foreign_load or system: what the decision log filters by.</param>
public sealed record DecisionDto(
    string Id,
    DateTimeOffset At,
    string Scope,
    string? Device,
    int? Stage,
    string? Phase,
    string Action,
    string Kind,
    string Reason,
    string? Overridden,
    string Mode,
    bool Commanded,
    string? RefusedBy,
    string? Origin,
    string? Group,
    LevelDto? Target,
    DecisionInputsDto Inputs);

public sealed record DecisionInputsDto(PhaseInputsDto? Phase, DeviceInputsDto? Device, ModeInputsDto? Mode, string? KillSwitch, string? SafeStateCause, DispatchInputsDto? Dispatch);

/// <summary>A device level: relay flags per stage (null for a setpoint device), or a setpoint in A; enabled is false only for a modulating device switched off by its stop actuator.</summary>
public sealed record LevelDto(IReadOnlyList<bool>? Relays, double? SetpointA, bool Enabled);

/// <summary>What a dispatch decision was made from: headroom per constraint (main phase, or a circuit group's phase), the hysteresis band, settling and verification.</summary>
/// <param name="Headroom">E from the guard per constraint, and what was left when the device's turn came.</param>
/// <param name="Contribution">The device's share of the measured current per phase.</param>
/// <param name="InFlight">The in-flight change counted for the device per constraint.</param>
public sealed record DispatchInputsDto(
    IReadOnlyList<HeadroomDto> Headroom,
    double StartHysteresisA,
    double StopHysteresisA,
    int? StartRank,
    IReadOnlyList<PhaseCurrentDto> Contribution,
    IReadOnlyList<ConstraintCurrentDto> InFlight,
    string? Settling,
    VerificationDto? Verification,
    double MinOnRemainingS,
    double MinOffRemainingS,
    DateTimeOffset? DeferredSince,
    string Override,
    bool Reasserted);

/// <param name="Group">The circuit group of the constraint; null for the main phase.</param>
public sealed record HeadroomDto(string? Group, string Phase, double? EnvelopeA, double? RemainingA);

public sealed record PhaseCurrentDto(string Phase, double CurrentA);

public sealed record ConstraintCurrentDto(string? Group, string Phase, double CurrentA);

/// <param name="Unit">amperes or watts.</param>
public sealed record VerificationDto(double Expected, double Measured, string Unit, string Verdict);

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

/// <summary>The mode at a mode decision; the control switch's parts are null on lines written before 0.7.0.</summary>
public sealed record ModeInputsDto(string Ceiling, string UiChoice, string Note, bool? ControlAllowed, bool? ConfigurationValid, bool? StateConfirmed, IReadOnlyList<string> Missing, string? Source);

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
                    $"Filters: device (a device name), phase (L1, L2, L3), reason (a reason code of list version {ReasonCodes.ListVersion}), kind ({string.Join(", ", Enum.GetValues<DecisionKind>().Select(ApiText.Snake))}), limit (1 to {options.Value.DecisionMaxPageSize}), before (a cursor from 'next').",
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

        DecisionKind? kind = null;
        if (query.TryGetValue("kind", out var kindText))
        {
            kind = Enum.GetValues<DecisionKind>().Where(k => ApiText.Snake(k) == kindText).Select(k => (DecisionKind?)k).SingleOrDefault();
            if (kind is null)
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

        return new DecisionQuery(device, phase, reason, Math.Min(limit, maxLimit), before) { Kind = kind };
    }

    internal static DecisionDto Dto(Decision decision) => new(
        decision.Id,
        decision.At,
        ApiText.Snake(decision.Scope),
        decision.Device?.Value,
        decision.Stage,
        decision.Phase?.ToString(),
        ApiText.Snake(decision.Action),
        ApiText.Snake(DecisionKinds.Of(decision)),
        ReasonCodes.Name(decision.Reason),
        decision.Overridden is { } overridden ? ReasonCodes.Name(overridden) : null,
        ApiText.Snake(decision.Mode),
        decision.Commanded,
        decision.RefusedBy is { } refused ? ReasonCodes.Name(refused) : null,
        ApiText.SnakeOrNull(decision.Origin),
        decision.Group?.Value,
        ApiText.Level(decision.Target),
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
            decision.Inputs.Mode is { } m
                ? new ModeInputsDto(ApiText.Snake(m.Ceiling), ApiText.Snake(m.UiChoice), ApiText.Snake(m.Note), m.ControlAllowed, m.ConfigurationValid, m.StateConfirmed, [.. (m.Missing.IsDefault ? [] : m.Missing).Select(ApiText.Snake)], ApiText.SnakeOrNull(m.Source))
                : null,
            ApiText.SnakeOrNull(decision.Inputs.KillSwitch),
            ApiText.SnakeOrNull(decision.Inputs.SafeStateCause),
            decision.Inputs.Dispatch is { } dispatch ? Dispatch(dispatch) : null));

    private static DispatchInputsDto Dispatch(DispatchDecisionInputs dispatch) => new(
        [.. (dispatch.Headroom.IsDefault ? [] : dispatch.Headroom).Select(h => new HeadroomDto(h.Key.Group?.Value, h.Key.Phase.ToString(), h.Envelope?.Value, h.Remaining?.Value))],
        dispatch.StartHysteresis.Value,
        dispatch.StopHysteresis.Value,
        dispatch.StartRank,
        [.. dispatch.Contribution.OrderBy(c => c.Key).Select(c => new PhaseCurrentDto(c.Key.ToString(), c.Value.Value))],
        [.. dispatch.InFlight.OrderBy(c => c.Key.Group?.Value, StringComparer.Ordinal).ThenBy(c => c.Key.Phase).Select(c => new ConstraintCurrentDto(c.Key.Group?.Value, c.Key.Phase.ToString(), c.Value.Value))],
        ApiText.SnakeOrNull(dispatch.Settling),
        dispatch.Verification is { } v ? new VerificationDto(v.Expected, v.Measured, ApiText.Snake(v.Unit), ApiText.Snake(v.Verdict)) : null,
        dispatch.MinOnRemaining.TotalSeconds,
        dispatch.MinOffRemaining.TotalSeconds,
        dispatch.DeferredSince,
        ApiText.Snake(dispatch.Override),
        dispatch.Reasserted);
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

    public static LevelDto? Level(DeviceLevel? level) =>
        level is null ? null : new LevelDto(level.Setpoint is null ? [.. level.Relays.IsDefault ? [] : level.Relays] : null, level.Setpoint?.Value, level.Enabled);
}
