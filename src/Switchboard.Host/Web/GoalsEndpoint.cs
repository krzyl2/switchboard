using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Switchboard.Application.Goals;
using Switchboard.Application.Planning;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Goals;

namespace Switchboard.Host.Web;

/// <summary>Body of POST api/goals: exactly these five keys.</summary>
/// <param name="Device">A declared device's id.</param>
/// <param name="Kind">energy (target is kWh) or soc (target is percent; the device's goal: must be of kind soc).</param>
/// <param name="Target">Above zero; at most 1 000 000 kWh, or 100 percent.</param>
/// <param name="Deadline">An ISO 8601 instant with an explicit offset (Z or ±hh:mm), in the future and within planner.horizon.</param>
/// <param name="Hard">A hard deadline is met without a value test; a soft goal lapses at the deadline.</param>
public sealed record ManualGoalRequest(string Device, GoalKind Kind, double Target, DateTimeOffset Deadline, bool Hard);

/// <summary>What POST api/goals stored (camelCase like the other endpoints).</summary>
/// <param name="Created">False when the same goal was already stored (nothing changed).</param>
public sealed record ManualGoalResponse(string Device, string Kind, double Target, DateTimeOffset Deadline, bool Hard, DateTimeOffset Since, double DeliveredKwh, bool Created);

/// <summary>
/// POST api/goals and DELETE api/goals/{device} (PLAN-19, 15-PLANNER "How want is entered"): the "want now" action of the device card. It stores
/// or cancels a manual goal in the add-on's own state (state.json) and nudges the planner to look again; it writes no entity and commands nothing.
/// Same protection as the other POST endpoints: ingress only (the guard middleware runs first), no new port, and a JSON body is required, because a
/// cross-site form cannot send one without a CORS preflight, which the add-on never answers (a DELETE cannot be sent cross-site without one either). The body
/// is read strictly (five keys, a bounded size) and a device the valid configuration does not declare is refused (404), as is any request while there is no
/// valid configuration (409).
/// </summary>
internal static partial class GoalsEndpoint
{
    /// <summary>Far above the five short keys; a larger body is not a goal request.</summary>
    internal const int MaxBodyBytes = 1024;

    public const string BadRequest = "bad_request";
    public const string UnknownDevice = "unknown_device";
    public const string NoConfiguration = "no_configuration";
    public const string NoManualGoal = "no_manual_goal";
    public const string SocNotConfigured = "soc_not_configured";
    public const string TargetOutOfRange = "target_out_of_range";
    public const string DeadlinePassed = "deadline_passed";
    public const string DeadlineBeyondHorizon = "deadline_beyond_horizon";

    // An instant with an explicit offset: a bare local time would be read in the add-on's zone, which the add-on does not have.
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex Instant();

    public static IEndpointRouteBuilder MapGoalsApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/goals", async Task<IResult> (
            HttpRequest request,
            [FromServices] IManualGoalControl goals,
            [FromServices] IPlanTrigger planner,
            CancellationToken cancellationToken) =>
        {
            var read = await request.ReadBodyAsync(cancellationToken).ConfigureAwait(false);
            if (read.TooLarge)
            {
                return ApiResults.TooLarge(read.Limit);
            }

            if (Parse(read) is not { } body)
            {
                return Refused(StatusCodes.Status400BadRequest, BadRequest, Shape);
            }

            if (!DeviceName.TryParse(body.Device, out var device))
            {
                return Refused(StatusCodes.Status404NotFound, UnknownDevice, $"No declared device is called '{body.Device}'; only devices of the configuration can have a goal.");
            }

            var result = goals.Begin(device.Value, body.Kind, body.Target, body.Deadline, body.Hard);
            switch (result)
            {
                case ManualGoalResult.UnknownDevice:
                    return Refused(StatusCodes.Status404NotFound, UnknownDevice, $"No declared device is called '{body.Device}'; only devices of the configuration can have a goal.");
                case ManualGoalResult.NoConfiguration:
                    return Refused(StatusCodes.Status409Conflict, NoConfiguration, "There is no valid configuration to check the device against; fix switchboard.yaml first.");
                case ManualGoalResult.SocNotConfigured:
                    return Refused(StatusCodes.Status422UnprocessableEntity, SocNotConfigured, "A state-of-charge goal needs the device's goal: of kind soc (it says where the SoC is read and the capacity); ask for energy instead.");
                case ManualGoalResult.TargetOutOfRange:
                    return Refused(StatusCodes.Status422UnprocessableEntity, TargetOutOfRange, body.Kind == GoalKind.Soc ? "The target must be above 0 and at most 100 percent." : $"The target must be above 0 and at most {ManualGoal.MaxTargetKwh.ToString("0", CultureInfo.InvariantCulture)} kWh.");
                case ManualGoalResult.DeadlinePassed:
                    return Refused(StatusCodes.Status422UnprocessableEntity, DeadlinePassed, "The deadline must be in the future.");
                case ManualGoalResult.DeadlineBeyondHorizon:
                    return Refused(StatusCodes.Status422UnprocessableEntity, DeadlineBeyondHorizon, "The deadline is beyond the planning horizon (control.planner.horizon); the plan does not see so far.");
            }

            var stored = goals.Current.Goals[device.Value];
            var created = result == ManualGoalResult.Begun;

            // The goal changed: ask the planner to look again (it rate-limits and decides; with no planner: section it is idle).
            if (created)
            {
                planner.Wake();
            }

            return TypedResults.Json(new ManualGoalResponse(device.Value.Value, ApiText.Snake(stored.Kind), stored.Target, stored.Deadline, stored.Hard, stored.Since, stored.DeliveredKwh, created));
        }).WithBodyLimit(MaxBodyBytes);

        endpoints.MapDelete("/goals/{device}", IResult (
            string device,
            [FromServices] IManualGoalControl goals,
            [FromServices] IPlanTrigger planner) =>
        {
            if (!DeviceName.TryParse(device, out var name))
            {
                return Refused(StatusCodes.Status404NotFound, UnknownDevice, $"No declared device is called '{device}'.");
            }

            switch (goals.End(name.Value))
            {
                case ManualGoalResult.NotFound:
                    return Refused(StatusCodes.Status404NotFound, NoManualGoal, $"{device} has no manual goal.");
                default:
                    planner.Wake();
                    return TypedResults.Json(new { device = name.Value.Value, ended = true });
            }
        });
        return endpoints;
    }

    private static ProblemHttpResult Refused(int status, string code, string message) => ApiResults.Refusal(status, code, message);

    private const string Shape = "Send exactly {\"device\": string, \"kind\": \"energy\" | \"soc\", \"target\": number, \"deadline\": ISO 8601 instant with an offset, \"hard\": boolean}.";

    /// <summary>Exactly device, kind, target, deadline and hard, each of its type; null when the body is anything else.</summary>
    private static ManualGoalRequest? Parse(BodyRead read)
    {
        var root = read.AsObject();
        if (root is not { } body || body.EnumerateObject().Count() != 5)
        {
            return null;
        }

        if (!body.TryGetProperty("device", out var device) || device.ValueKind != JsonValueKind.String
            || !body.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String
            || !body.TryGetProperty("target", out var target) || target.ValueKind != JsonValueKind.Number || !target.TryGetDouble(out var targetValue) || !double.IsFinite(targetValue)
            || !body.TryGetProperty("deadline", out var deadline) || deadline.ValueKind != JsonValueKind.String
            || !body.TryGetProperty("hard", out var hard) || hard.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }

        var kindText = kind.GetString();
        var kinds = Enum.GetValues<GoalKind>().Where(k => ApiText.Snake(k) == kindText).ToArray();
        var deadlineText = deadline.GetString()!;
        if (kinds.Length != 1
            || !Instant().IsMatch(deadlineText)
            || !DateTimeOffset.TryParse(deadlineText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
        {
            return null;
        }

        return new ManualGoalRequest(device.GetString()!, kinds[0], targetValue, instant, hard.GetBoolean());
    }
}
