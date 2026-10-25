using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Switchboard.Application.Modes;

namespace Switchboard.Host.Web;

/// <summary>
/// GET and POST api/mode (MODE-02): the add-on option (the ceiling), the UI choice persisted in
/// state.json, and the effective mode, shown honestly. In this version the effective mode is always
/// shadow (no command path exists), so the UI can choose shadow only; active is refused.
/// </summary>
/// <param name="Note">none or active_not_enabled.</param>
public sealed record ModeResponse(string Ceiling, string UiChoice, string Effective, string Note);

internal static class ModeEndpoint
{
    public const string ActiveNotEnabled = "active_not_enabled";

    public static IEndpointRouteBuilder MapModeApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/mode", ([FromServices] ModeState mode) => Results.Json(Build(mode.Current)));

        // A JSON body is required: a cross-site form cannot send one without a CORS preflight.
        endpoints.MapPost("/api/mode", async (HttpRequest request, [FromServices] ModeState mode, CancellationToken cancellationToken) =>
        {
            if (!request.HasJsonContentType() || await ReadChoiceAsync(request, cancellationToken).ConfigureAwait(false) is not { } choice)
            {
                return Results.Problem("Send Content-Type: application/json with the body {\"uiChoice\": \"shadow\"}.", statusCode: StatusCodes.Status400BadRequest);
            }

            // While commanding is not enabled, active is never stored: a later version with a command
            // path must not start active on an upgrade without a new deliberate act.
            return mode.Choose(choice) is { } status
                ? Results.Json(Build(status))
                : Results.Json(
                    new { code = ActiveNotEnabled, message = "Commanding is not enabled in this version: Switchboard computes and logs decisions and switches nothing, so active mode cannot be chosen." },
                    statusCode: StatusCodes.Status409Conflict);
        });
        return endpoints;
    }

    internal static ModeResponse Build(ModeStatus status) => new(
        ApiText.Snake(status.Ceiling),
        ApiText.Snake(status.UiChoice),
        ApiText.Snake(status.Effective),
        ApiText.Snake(status.Note));

    private static async Task<OperatingMode?> ReadChoiceAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 || !root.TryGetProperty("uiChoice", out var value) || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return value.GetString() switch
            {
                "shadow" => OperatingMode.Shadow,
                "active" => OperatingMode.Active,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
