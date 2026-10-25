using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Effector;
using Switchboard.Application.Options;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;

namespace Switchboard.Host.Web;

/// <summary>
/// Shape of GET api/dispatch: what dispatch and the effector gate make of every declared device
/// (DISP-04 … DISP-10, SAFE-04, UI-10). <c>commandPath</c> is false in this version: decisions are
/// computed and logged, nothing is switched, so <c>intended</c> is what Switchboard would have the
/// device at and <c>reported</c> what Home Assistant says it is. Enum values are snake_case.
/// </summary>
public sealed record DispatchResponse(
    DateTimeOffset At,
    GuardModeDto Mode,
    bool CommandPath,
    IReadOnlyList<DispatchPhaseDto> Phases,
    IReadOnlyList<DispatchDeviceDto> Devices);

/// <param name="EnvelopeA">E: what dispatch may still add on the phase (null without valid data).</param>
/// <param name="InFlightA">The in-flight changes counted in the estimate.</param>
/// <param name="ForeignW">House load not explained by our devices (04 §4); null when unknown.</param>
/// <param name="Rank">The adopted phase priority for rotated elements (DISP-10; 1 is started first); null before one is adopted.</param>
public sealed record DispatchPhaseDto(
    string Phase,
    double? EnvelopeA,
    double InFlightA,
    bool DataValid,
    string? InvalidReason,
    double? ForeignW,
    double? ForeignBaselineW,
    int? Rank,
    IReadOnlyList<DispatchGroupDto> Groups);

public sealed record DispatchGroupDto(string Group, double? EnvelopeA, double InFlightA, bool IncreasesAllowed);

/// <param name="Intended">The resolved target; null while unset (it then follows the reported state).</param>
/// <param name="Matches">Intended and reported agree (an unset target always does).</param>
/// <param name="Pending">An increase dispatch wants but has not made yet, and why (awaiting_confirmation, settling_window_open, a guard reason).</param>
/// <param name="Settling">none, open or timed_out.</param>
/// <param name="History">Verifications of our changes and observations of other controllers' switching, newest first (UI-12).</param>
public sealed record DispatchDeviceDto(
    string Id,
    string Type,
    IReadOnlyList<string> Phases,
    string? RotationGroup,
    string? CircuitGroup,
    LevelDto? Intended,
    LevelDto? Reported,
    bool ReportedFresh,
    bool Matches,
    string? Reason,
    string? Origin,
    int? StartRank,
    string? Pending,
    LockoutDto Lockout,
    DeferredDto? Deferred,
    string Settling,
    UnresponsiveDto? Unresponsive,
    int Mismatches,
    OverrideDto Override,
    DrawDto? Draw,
    IReadOnlyList<HistoryDto> History);

/// <summary>Remaining minimum on and off times on the dispatch (target) timeline, in seconds.</summary>
public sealed record LockoutDto(double MinOnRemainingS, double MinOffRemainingS);

/// <param name="DueAt">When the lockout expires; null for a forced change re-checked every cycle (headroom, budget).</param>
public sealed record DeferredDto(LevelDto Target, string Reason, string RefusedBy, DateTimeOffset Since, DateTimeOffset? DueAt);

public sealed record UnresponsiveDto(DateTimeOffset Since, DateTimeOffset? RetryAt);

/// <param name="Mode">auto, force_on or force_off.</param>
/// <param name="Source">api, mqtt or restored; null without an override.</param>
public sealed record OverrideDto(string Mode, DateTimeOffset? ChangedAt, string? Source);

public sealed record DrawDto(double? CurrentA, double? PowerW, bool Fresh);

/// <param name="Kind">command (a change of ours) or observed (another controller switched the device).</param>
/// <param name="Unit">amperes or watts.</param>
/// <param name="CommandedAt">When the change was decided (shadow: not applied) or the switching was seen; null for records from before 0.5.0.</param>
/// <param name="From">The level the change started from.</param>
/// <param name="To">The level it led to.</param>
public sealed record HistoryDto(DateTimeOffset At, string Kind, double Expected, double Measured, string Unit, string Verdict, DateTimeOffset? CommandedAt, LevelDto? From, LevelDto? To);

/// <summary>Body of POST api/devices/{id}/override.</summary>
public sealed record OverrideRequest(OverrideMode Mode);

internal static class DispatchEndpoint
{
    public static IEndpointRouteBuilder MapDispatchApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/dispatch", ([FromServices] IDispatchStatusSource status) => TypedResults.Json(Build(status.Current)));

        // SAFE-04 from the UI. A JSON body is required, as for the kill switch: a cross-site form
        // cannot send one without a CORS preflight, which the add-on never answers. Only a device of
        // the current valid configuration can be addressed (SAFE-05); protection still outranks it.
        endpoints.MapPost("/api/devices/{id}/override", async (
            string id,
            HttpRequest request,
            [FromServices] IDeviceOverrideControl overrides,
            [FromServices] DispatchStatusHolder holder,
            [FromServices] IOptions<GuardOptions> options,
            [FromServices] TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            if (!request.HasJsonContentType() || await ReadBodyAsync(request, cancellationToken).ConfigureAwait(false) is not { } body)
            {
                return Results.Problem("Send Content-Type: application/json with the body {\"mode\": \"auto\"}, {\"mode\": \"force_on\"} or {\"mode\": \"force_off\"}.", statusCode: StatusCodes.Status400BadRequest);
            }

            if (!DeviceName.TryParse(id, out var device))
            {
                return Results.Problem($"No declared device is called '{id}'.", statusCode: StatusCodes.Status404NotFound);
            }

            // Subscribed before the change, so the evaluation of it cannot be missed.
            var evaluated = new TaskCompletionSource<DispatchStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool Shows(DispatchStatus status) => status.Devices.Any(d => d.Id == device && d.Override.Mode == body.Mode);
            void OnPublished(object? sender, DispatchStatus published)
            {
                if (Shows(published))
                {
                    evaluated.TrySetResult(published);
                }
            }

            holder.Published += OnPublished;
            try
            {
                switch (overrides.Set(device.Value, body.Mode, OverrideSource.Api))
                {
                    case OverrideResult.UnknownDevice:
                        return Results.Problem($"No declared device is called '{id}'; only devices of the configuration can be overridden.", statusCode: StatusCodes.Status404NotFound);
                    case OverrideResult.NoConfiguration:
                        return Results.Problem("There is no valid configuration to check the device against; fix switchboard.yaml first.", statusCode: StatusCodes.Status409Conflict);
                }

                if (Shows(holder.Current))
                {
                    evaluated.TrySetResult(holder.Current);
                }

                var result = await evaluated.Task.WaitAsync(options.Value.ApiEvaluationTimeout, time, cancellationToken).ConfigureAwait(false);
                return Results.Json(Build(result));
            }
            catch (TimeoutException)
            {
                // The override is in force in memory either way; only the evaluation is late.
                return Results.Problem(
                    $"The override of {device} is {ApiText.Snake(body.Mode)}, but it was not evaluated within {options.Value.ApiEvaluationTimeout}.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            finally
            {
                holder.Published -= OnPublished;
            }
        });
        return endpoints;
    }

    internal static DispatchResponse Build(DispatchStatus status) => new(
        status.At,
        new GuardModeDto(ApiText.Snake(status.Mode.Ceiling), ApiText.Snake(status.Mode.UiChoice), ApiText.Snake(status.Mode.Effective), ApiText.Snake(status.Mode.Note)),
        status.CommandPath,
        [.. status.Phases.Select(p => new DispatchPhaseDto(
            p.Phase.ToString(),
            p.Envelope?.Value,
            p.InFlight.Value,
            p.DataValid,
            ApiText.SnakeOrNull(p.InvalidReason),
            p.Foreign?.Value,
            p.ForeignBaseline?.Value,
            p.Rank,
            [.. p.Groups.Select(g => new DispatchGroupDto(g.Group.Value, g.Envelope?.Value, g.InFlight.Value, g.IncreasesAllowed))]))],
        [.. status.Devices.Select(d => new DispatchDeviceDto(
            d.Id.Value,
            ApiText.Snake(d.Type),
            [.. d.Phases.Select(p => p.ToString())],
            d.RotationGroup?.Value,
            d.CircuitGroup?.Value,
            ApiText.Level(d.Intended),
            ApiText.Level(d.Reported),
            d.ReportedFresh,
            d.Matches,
            Name(d.Reason),
            ApiText.SnakeOrNull(d.Origin),
            d.StartRank,
            Name(d.Pending),
            new LockoutDto(d.MinOnRemaining.TotalSeconds, d.MinOffRemaining.TotalSeconds),
            d.Deferred is { } deferred
                ? new DeferredDto(ApiText.Level(deferred.Target)!, ReasonCodes.Name(deferred.Reason), ReasonCodes.Name(deferred.RefusedBy), deferred.Since, deferred.DueAt)
                : null,
            ApiText.Snake(d.Settling),
            d.Unresponsive is { } unresponsive ? new UnresponsiveDto(unresponsive.Since, unresponsive.RetryAt) : null,
            d.Mismatches,
            new OverrideDto(ApiText.Snake(d.Override.Mode), d.Override.ChangedAt, ApiText.SnakeOrNull(d.Override.Source)),
            d.Draw is { } draw ? new DrawDto(draw.Current?.Value, draw.Power?.Value, draw.Fresh) : null,
            [.. d.History.Reverse().Select(h => new HistoryDto(h.At, ApiText.Snake(h.Kind), h.Expected, h.Measured, ApiText.Snake(h.Unit), ApiText.Snake(h.Verdict), h.CommandedAt, ApiText.Level(h.From), ApiText.Level(h.To)))]))]);

    private static string? Name(ReasonCode? reason) => reason is { } code ? ReasonCodes.Name(code) : null;

    /// <summary>Exactly one key, mode, with one of the three snake_case values.</summary>
    private static async Task<OverrideRequest?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() != 1
                || !root.TryGetProperty("mode", out var mode)
                || mode.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var text = mode.GetString();
            return Enum.GetValues<OverrideMode>().Where(m => ApiText.Snake(m) == text).Select(m => new OverrideRequest(m)).SingleOrDefault();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
