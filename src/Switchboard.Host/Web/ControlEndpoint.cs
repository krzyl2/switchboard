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
        endpoints.MapGet("/api/control", (
                [FromServices] IControlSwitchView control,
                [FromServices] IModeSource mode,
                [FromServices] RuntimeStateCoordinator state,
                [FromServices] ICommandPathStatus commands,
                [FromServices] IDispatchStatusSource dispatch) =>
            Results.Json(Build(control.Status, mode.Current, state, commands.Status, dispatch.Current)));

        // A JSON body is required: a cross-site form cannot send one without a CORS preflight,
        // which the add-on never answers, so control cannot be allowed from another page.
        endpoints.MapPost("/api/control", async (
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
            if (!request.HasJsonContentType() || await ReadFlagAsync(request, "allowed", cancellationToken).ConfigureAwait(false) is not { } allowed)
            {
                return Results.Problem("Send Content-Type: application/json with the body {\"allowed\": true} or {\"allowed\": false}.", statusCode: StatusCodes.Status400BadRequest);
            }

            var result = await EvaluatedAsync(holder, s => s.Mode.ControlAllowed == allowed, () => control.Set(allowed, ControlSwitchSource.Api), options.Value.ApiEvaluationTimeout, time, cancellationToken).ConfigureAwait(false);
            if (result == ControlSetResult.RefusedLocked)
            {
                return Results.Json(
                    new { code = StateUnconfirmed, message = "state.json could not be trusted at start: confirm the state first; control stays off until then." },
                    statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Json(Build(control.Status, mode.Current, state, commands.Status, dispatch.Current));
        });

        endpoints.MapPost("/api/control/confirm", async (
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
            if (!request.HasJsonContentType() || await ReadFlagAsync(request, "confirm", cancellationToken).ConfigureAwait(false) is not true)
            {
                return Results.Problem("Send Content-Type: application/json with the body {\"confirm\": true}.", statusCode: StatusCodes.Status400BadRequest);
            }

            var confirmed = await EvaluatedAsync(holder, s => s.Mode.StateConfirmed, control.Confirm, options.Value.ApiEvaluationTimeout, time, cancellationToken).ConfigureAwait(false);
            if (!confirmed)
            {
                return Results.Json(new { code = NotLocked, message = "Nothing waits for a confirmation." }, statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Json(Build(control.Status, mode.Current, state, commands.Status, dispatch.Current));
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

    /// <summary>Applies a change and waits until the guard evaluated it (the status shows it), so the answer reflects the mode in force.</summary>
    private static async Task<T> EvaluatedAsync<T>(GuardStatusHolder holder, Func<GuardStatus, bool> shows, Func<T> change, TimeSpan timeout, TimeProvider time, CancellationToken cancellationToken)
    {
        var evaluated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPublished(object? sender, GuardStatus published)
        {
            if (shows(published))
            {
                evaluated.TrySetResult();
            }
        }

        holder.Published += OnPublished;
        try
        {
            var result = change();
            if (shows(holder.Current))
            {
                evaluated.TrySetResult();
            }

            try
            {
                await evaluated.Task.WaitAsync(timeout, time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The switch is in force in memory either way; only the evaluation is late.
            }

            return result;
        }
        finally
        {
            holder.Published -= OnPublished;
        }
    }

    /// <summary>Exactly one key with a boolean value.</summary>
    private static async Task<bool?> ReadFlagAsync(HttpRequest request, string key, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.EnumerateObject().Count() == 1
                && root.TryGetProperty(key, out var value)
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? value.GetBoolean()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
