using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Options;
using Switchboard.Application.State;

namespace Switchboard.Host.Web;

/// <summary>
/// Shape of GET and POST api/control: the global "control allowed" switch (MODE-02), who set it
/// when, the lock after a corrupt state.json, the mode it results in, how long the installation has
/// been observing (advice only, never enforced), and the command path: link, queue, last
/// acknowledgement and failure, devices whose acknowledged command did not show, and devices
/// Switchboard left reduced (restore baselines kept while control is off).
/// </summary>
public sealed record ControlResponse(
    bool ControlAllowed,
    DateTimeOffset? ChangedAt,
    string Source,
    bool Locked,
    string? LockReason,
    ModeResponse Mode,
    ObservingDto? Observation,
    CommandsDto Commands,
    IReadOnlyList<LeftReducedDto> LeftReduced);

/// <param name="Since">When observing began (first start, or the oldest decision-log day of an older installation).</param>
public sealed record ObservingDto(DateTimeOffset Since, int ConfigurationErrors, DateTimeOffset? LastConfigurationErrorAt);

public sealed record CommandsDto(bool LinkReady, int Pending, DateTimeOffset? LastAcknowledgedAt, CommandFailureDto? LastFailure, IReadOnlyList<UnconfirmedDto> Unconfirmed);

/// <param name="Outcome">failed, timed_out or not_sent.</param>
public sealed record CommandFailureDto(string Device, DateTimeOffset At, string Outcome, string? NotSent, string? Detail);

public sealed record UnconfirmedDto(string Device, DateTimeOffset Since);

/// <param name="Level">The level restore_only brings the device back to.</param>
public sealed record LeftReducedDto(string Device, LevelDto Level, DateTimeOffset Since);

internal static class ControlEndpoint
{
    public const string StateUnconfirmed = "state_unconfirmed";
    public const string NotLocked = "not_locked";

    public static IEndpointRouteBuilder MapControlApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/control", (
                [FromServices] IControlSwitchView control,
                [FromServices] IModeSource mode,
                [FromServices] RuntimeStateCoordinator state,
                [FromServices] ICommandPathStatus commands,
                [FromServices] IDispatchStatusSource dispatch) =>
            TypedResults.Json(Build(control.Status, mode.Current, state, commands.Status, dispatch.Current)));

        // A JSON body is required: a cross-site form cannot send one without a CORS preflight,
        // which the add-on never answers, so control cannot be allowed from another page.
        endpoints.MapPost("/control", async Task<IResult> (
            HttpRequest request,
            [FromServices] IControlSwitch control,
            [FromServices] IModeSource mode,
            [FromServices] GuardStatusHolder holder,
            [FromServices] RuntimeStateCoordinator state,
            [FromServices] ICommandPathStatus commands,
            [FromServices] IDispatchStatusSource dispatch,
            [FromServices] IOptions<GuardOptions> options,
            [FromServices] TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            var read = await request.ReadBodyAsync(cancellationToken).ConfigureAwait(false);
            if (read.TooLarge)
            {
                return ApiResults.TooLarge(read.Limit);
            }

            if (ReadFlag(read, "allowed") is not { } allowed)
            {
                return ApiResults.Problem(StatusCodes.Status400BadRequest, "Send Content-Type: application/json with the body {\"allowed\": true} or {\"allowed\": false}.");
            }

            // The switch is in force in memory either way; only the evaluation can be late.
            var applied = await StatusChange.ApplyAsync(StatusChange.Feed(holder), s => s.Mode.ControlAllowed == allowed, () => control.Set(allowed, ControlSwitchSource.Api), _ => true, options.Value.ApiEvaluationTimeout, time, cancellationToken).ConfigureAwait(false);
            if (applied.Result == ControlSetResult.RefusedLocked)
            {
                return ApiResults.Refusal(StatusCodes.Status409Conflict, StateUnconfirmed, "state.json could not be trusted at start: confirm the state first; control stays off until then.");
            }

            return TypedResults.Json(Build(control.Status, mode.Current, state, commands.Status, dispatch.Current));
        });

        endpoints.MapPost("/control/confirm", async Task<IResult> (
            HttpRequest request,
            [FromServices] IControlSwitch control,
            [FromServices] IModeSource mode,
            [FromServices] GuardStatusHolder holder,
            [FromServices] RuntimeStateCoordinator state,
            [FromServices] ICommandPathStatus commands,
            [FromServices] IDispatchStatusSource dispatch,
            [FromServices] IOptions<GuardOptions> options,
            [FromServices] TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            var read = await request.ReadBodyAsync(cancellationToken).ConfigureAwait(false);
            if (read.TooLarge)
            {
                return ApiResults.TooLarge(read.Limit);
            }

            if (ReadFlag(read, "confirm") is not true)
            {
                return ApiResults.Problem(StatusCodes.Status400BadRequest, "Send Content-Type: application/json with the body {\"confirm\": true}.");
            }

            var applied = await StatusChange.ApplyAsync(StatusChange.Feed(holder), s => s.Mode.StateConfirmed, control.Confirm, _ => true, options.Value.ApiEvaluationTimeout, time, cancellationToken).ConfigureAwait(false);
            if (!applied.Result)
            {
                return ApiResults.Refusal(StatusCodes.Status409Conflict, NotLocked, "Nothing waits for a confirmation.");
            }

            return TypedResults.Json(Build(control.Status, mode.Current, state, commands.Status, dispatch.Current));
        });
        return endpoints;
    }

    internal static ControlResponse Build(ControlSwitchStatus control, ModeStatus mode, RuntimeStateCoordinator state, CommandPathStatus commands, DispatchStatus dispatch) => new(
        control.Allowed,
        control.ChangedAt,
        ApiText.Snake(control.Source),
        control.Locked,
        control.LockReason is { } reason ? ApiText.Snake(reason) : null,
        ModeEndpoint.Build(mode),
        state.Current.Observation is { } observation ? new ObservingDto(observation.FirstStartedAt, observation.ConfigurationErrors, observation.LastConfigurationErrorAt) : null,
        new CommandsDto(
            commands.LinkReady,
            commands.Pending,
            commands.LastAcknowledgedAt,
            commands.LastFailure is { } failure
                ? new CommandFailureDto(failure.Device.Value, failure.At, ApiText.Snake(failure.Outcome), failure.NotSent is { } notSent ? ApiText.Snake(notSent) : null, failure.Detail)
                : null,
            [.. dispatch.Devices.Where(d => d.UnconfirmedSince is not null).Select(d => new UnconfirmedDto(d.Id.Value, d.UnconfirmedSince!.Value))]),
        [.. dispatch.Devices.Where(d => d.Baseline is { Commanded: true }).Select(d => new LeftReducedDto(d.Id.Value, ApiText.Level(d.Baseline!.Level)!, d.Baseline.Since))]);

    /// <summary>Exactly one key with a boolean value.</summary>
    private static bool? ReadFlag(BodyRead read, string key)
    {
        var root = read.AsObject();
        return root is { } body
            && body.EnumerateObject().Count() == 1
            && body.TryGetProperty(key, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : null;
    }
}
