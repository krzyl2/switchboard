using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Switchboard.Application.Decisions;
using Switchboard.Application.Guard;
using Switchboard.Application.Options;
using Switchboard.Application.Signals;
using Switchboard.Application.State;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Signals;

namespace Switchboard.Host.Web;

/// <summary>
/// Shape of GET api/guard (BRK-04, SAFE-01, SAFE-03, SIG-05): the guard's view for the Limits page.
/// Headroom is from the measured current; <c>headroomW</c> is computed here, at the presentation
/// edge, from the configured voltage. Enum values are snake_case.
/// </summary>
public sealed record GuardResponse(
    DateTimeOffset At,
    GuardModeDto Mode,
    SafeStateDto SafeState,
    KillSwitchDto KillSwitch,
    BreakerDto? Breaker,
    IReadOnlyList<GuardPhaseDto> Phases,
    IReadOnlyList<GuardDeviceDto> Devices,
    DecisionLogDto DecisionLog,
    StateFileDto StateFile,
    IReadOnlyList<DecisionDto> Decisions);

public sealed record GuardModeDto(string Ceiling, string UiChoice, string Effective, string Note);

public sealed record SafeStateDto(bool Active, string Scope, string? Reason, IReadOnlyList<string> Phases, DateTimeOffset? Since);

public sealed record KillSwitchDto(bool Engaged, DateTimeOffset? ChangedAt, string? Source);

/// <param name="BudgetRecoveryTimeS">How long an empty overload budget takes to refill at or below the continuous current.</param>
public sealed record BreakerDto(double RatingA, string Characteristic, double ContinuousMultiple, double ContinuousA, double BudgetRecoveryTimeS, IReadOnlyList<TierDto> Tiers);

/// <param name="UpTo">The band's upper limit as a multiple of In (inclusive); null for the open band.</param>
/// <param name="UpToW">The same limit as power per phase at the nominal voltage (display only); null for the open band.</param>
/// <param name="AllowedS">The allowed duration in seconds; null for unlimited.</param>
public sealed record TierDto(double? UpTo, double? UpToA, double? UpToW, double? AllowedS, string Reaction);

public sealed record GuardPhaseDto(
    string Phase,
    bool DataValid,
    string? InvalidReason,
    double? CurrentA,
    double? EffectiveCurrentA,
    double? Multiple,
    int? Band,
    string? Reaction,
    double ContinuousA,
    double ReserveThresholdA,
    double? HeadroomA,
    double? HeadroomW,
    double? BreakerHeadroomA,
    double Budget,
    string BudgetTrend,
    double RecoveryRemainingS,
    bool IncreasesAllowed,
    bool ReductionRequested,
    string State,
    string? Direction,
    DateTimeOffset? ReportedAt);

/// <param name="UnsafeSignals">Logical names of the signals this device depends on that are not fresh (SIG-05).</param>
public sealed record GuardDeviceDto(
    string Id,
    string Type,
    IReadOnlyList<string> Phases,
    string GuardState,
    string? Reason,
    IReadOnlyList<GuardStageDto> Stages,
    int? ShedRank,
    double ValuePerKwh,
    double? ShedPriority,
    bool NoShed,
    bool CanSwitchOff,
    double MinOnRemainingS,
    double MinOffRemainingS,
    IReadOnlyList<string> UnsafeSignals);

/// <param name="Limit">What protection holds the stage to (off, at_minimum); null when nothing.</param>
/// <param name="LastKnown">The last fresh applied state seen, kept across restarts; for display only.</param>
public sealed record GuardStageDto(int Stage, string? Applied, double? SetpointA, bool Fresh, string? InvalidReason, string? Limit, LastKnownDto? LastKnown);

public sealed record LastKnownDto(string? Applied, double? SetpointA, DateTimeOffset At);

public sealed record DecisionLogDto(bool Writable, string? LastError, long Dropped);

public sealed record StateFileDto(string Status, string? Warning);

/// <summary>Body of POST api/guard/kill-switch.</summary>
public sealed record KillSwitchRequest(bool Engaged);

internal static class GuardEndpoint
{
    /// <summary>Decisions included in GET api/guard (the Limits page's recent list).</summary>
    public const int RecentDecisions = 50;

    public static IEndpointRouteBuilder MapGuardApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/guard", (
                [FromServices] IGuardStatusSource status,
                [FromServices] ISignalDiagnosticsSource diagnostics,
                [FromServices] IDecisionRecorder decisions,
                [FromServices] RuntimeStateCoordinator state) =>
            TypedResults.Json(Build(status.Current, diagnostics.Current, decisions, state)));

        // A JSON body is required: a cross-site form cannot send one without a CORS preflight,
        // which the add-on never answers, so the kill switch cannot be flipped from another page.
        endpoints.MapPost("/api/guard/kill-switch", async (
            HttpRequest request,
            [FromServices] IKillSwitchControl killSwitch,
            [FromServices] GuardStatusHolder holder,
            [FromServices] ISignalDiagnosticsSource diagnostics,
            [FromServices] IDecisionRecorder decisions,
            [FromServices] RuntimeStateCoordinator state,
            [FromServices] IOptions<GuardOptions> options,
            [FromServices] TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            if (!request.HasJsonContentType() || await ReadBodyAsync(request, cancellationToken).ConfigureAwait(false) is not { } body)
            {
                return Results.Problem("Send Content-Type: application/json with the body {\"engaged\": true} or {\"engaged\": false}.", statusCode: StatusCodes.Status400BadRequest);
            }

            // Subscribed before the change, so the evaluation of it cannot be missed.
            var evaluated = new TaskCompletionSource<GuardStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnPublished(object? sender, GuardStatus published)
            {
                if (published.KillSwitch.Engaged == body.Engaged)
                {
                    evaluated.TrySetResult(published);
                }
            }

            holder.Published += OnPublished;
            try
            {
                killSwitch.Set(body.Engaged, KillSwitchSource.Api);
                if (holder.Current.KillSwitch.Engaged == body.Engaged)
                {
                    evaluated.TrySetResult(holder.Current);
                }

                var result = await evaluated.Task.WaitAsync(options.Value.ApiEvaluationTimeout, time, cancellationToken).ConfigureAwait(false);
                return Results.Json(Build(result, diagnostics.Current, decisions, state));
            }
            catch (TimeoutException)
            {
                // The switch is in force in memory either way; only the evaluation is late.
                return Results.Problem(
                    $"The kill switch is {(body.Engaged ? "engaged" : "released")}, but the guard did not evaluate it within {options.Value.ApiEvaluationTimeout}.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            finally
            {
                holder.Published -= OnPublished;
            }
        });
        return endpoints;
    }

    internal static GuardResponse Build(GuardStatus status, SignalDiagnostics diagnostics, IDecisionRecorder decisions, RuntimeStateCoordinator state)
    {
        var voltage = diagnostics.Snapshot.Connection?.Voltage;
        var lastKnown = state.LastKnown;
        var report = state.Report;
        var log = decisions.Status;
        return new GuardResponse(
            status.At,
            new GuardModeDto(ApiText.Snake(status.Mode.Ceiling), ApiText.Snake(status.Mode.UiChoice), ApiText.Snake(status.Mode.Effective), ApiText.Snake(status.Mode.Note)),
            new SafeStateDto(
                status.SafeState.Active,
                ApiText.Snake(status.SafeState.Scope),
                status.SafeState.Reason is { } reason ? Domain.Decisions.ReasonCodes.Name(reason) : null,
                [.. status.SafeState.Phases.Select(p => p.ToString())],
                status.SafeState.Since),
            new KillSwitchDto(status.KillSwitch.Engaged, status.KillSwitch.ChangedAt, ApiText.SnakeOrNull(status.KillSwitch.Source)),
            status.Breaker is { } breaker
                ? new BreakerDto(
                    breaker.Rating.Value,
                    breaker.Characteristic == BreakerCharacteristic.Unknown ? "unknown" : breaker.Characteristic.ToString(),
                    breaker.ContinuousMultiple,
                    breaker.Continuous.Value,
                    diagnostics.Snapshot.Protection.Setup?.Budget.RecoveryTime.TotalSeconds ?? 0,
                    [.. breaker.Tiers.Select(t => new TierDto(
                        t.UpTo,
                        t.UpTo * breaker.Rating.Value,
                        t.UpTo is { } upTo && voltage is { } nominal ? SignalUnits.PowerAt(breaker.Rating * upTo, nominal).Value : null,
                        t.Allowed?.TotalSeconds,
                        ApiText.Snake(t.Reaction)))])
                : null,
            [.. status.Phases.Select(p => new GuardPhaseDto(
                p.Phase.ToString(),
                p.DataValid,
                ApiText.SnakeOrNull(p.InvalidReason),
                p.Current?.Value,
                p.EffectiveCurrent?.Value,
                p.Multiple,
                p.Band,
                ApiText.SnakeOrNull(p.Reaction),
                p.Continuous.Value,
                p.ReserveThreshold.Value,
                p.Headroom?.Value,
                p.Headroom is { } headroom && voltage is { } volts ? SignalUnits.PowerAt(headroom, volts).Value : null,
                p.BreakerHeadroom?.Value,
                p.Budget,
                ApiText.Snake(p.BudgetTrend),
                p.RecoveryRemaining.TotalSeconds,
                p.IncreasesAllowed,
                p.ReductionRequested,
                ApiText.Snake(p.State),
                ApiText.SnakeOrNull(p.Direction),
                p.ReportedAt))],
            [.. status.Devices.Select(d => new GuardDeviceDto(
                d.Id.Value,
                ApiText.Snake(d.Type),
                [.. d.Phases.Select(p => p.ToString())],
                ApiText.Snake(d.GuardState),
                d.Reason is { } deviceReason ? Domain.Decisions.ReasonCodes.Name(deviceReason) : null,
                [.. d.Stages.Select(s =>
                {
                    var known = lastKnown.TryGetValue(d.Id, out var stages) && s.Stage - 1 < stages.Length ? stages[s.Stage - 1] : null;
                    return new GuardStageDto(
                        s.Stage,
                        ApiText.Applied(s.Applied),
                        ApiText.Setpoint(s.Applied),
                        s.Fresh,
                        ApiText.SnakeOrNull(s.InvalidReason),
                        ApiText.SnakeOrNull(s.Limit),
                        known is null ? null : new LastKnownDto(ApiText.Applied(known.Applied), ApiText.Setpoint(known.Applied), known.At));
                })],
                d.ShedRank,
                d.ValuePerKwh,
                d.ShedPriority,
                d.NoShed,
                d.CanSwitchOff,
                d.MinOnRemaining.TotalSeconds,
                d.MinOffRemaining.TotalSeconds,
                UnsafeSignals(diagnostics, d)))],
            new DecisionLogDto(log.Writable, log.LastError, log.Dropped),
            new StateFileDto(ApiText.Snake(report.Status), report.Warning),
            [.. decisions.Query(new DecisionQuery(null, null, null, RecentDecisions, null)).Items.Select(DecisionsEndpoint.Dto)]);
    }

    /// <summary>The signals a device depends on (its phases' currents, its own state) that are not fresh now.</summary>
    // A device's own draw is left out: stale, it is no longer trusted for relief or increases, but it
    // never makes the device unsafe or holds it off (the Signals page labels it "not trusted").
    private static List<string> UnsafeSignals(SignalDiagnostics diagnostics, DeviceGuardStatus device) =>
        [.. diagnostics.Snapshot.Signals
            .Where(s => s.Name is not null && s.State != SignalState.Fresh && s.Role is not (SignalRole.DeviceCurrent or SignalRole.DevicePower)
                && !s.Dependents.IsDefault && s.Dependents.Contains(device.Id))
            .Select(s => s.Name!.Value.Value)
            .Order(StringComparer.Ordinal)];

    private static async Task<KillSwitchRequest?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.EnumerateObject().Count() == 1
                && root.TryGetProperty("engaged", out var engaged)
                && engaged.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? new KillSwitchRequest(engaged.GetBoolean())
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
