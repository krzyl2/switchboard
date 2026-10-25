using Microsoft.AspNetCore.Mvc;
using Switchboard.Application.Modes;

namespace Switchboard.Host.Web;

/// <summary>
/// GET api/mode (MODE-02), shown honestly: the add-on option (the ceiling), the control switch, the
/// configuration's validity, whether the state is confirmed, the effective mode, and every
/// condition that keeps it in shadow (option_shadow, control_off, configuration_invalid,
/// state_unconfirmed). The mode is changed through POST api/control, never here.
/// </summary>
/// <param name="Note">none (0.6.x notes are kept only for reading old decision lines).</param>
public sealed record ModeResponse(string Ceiling, bool ControlAllowed, bool ConfigurationValid, bool StateConfirmed, string Effective, IReadOnlyList<string> Missing, string Note);

internal static class ModeEndpoint
{
    public static IEndpointRouteBuilder MapModeApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/mode", ([FromServices] IModeSource mode) => Results.Json(Build(mode.Current)));
        return endpoints;
    }

    internal static ModeResponse Build(ModeStatus status) => new(
        ApiText.Snake(status.Ceiling),
        status.ControlAllowed,
        status.ConfigurationValid,
        status.StateConfirmed,
        ApiText.Snake(status.Effective),
        [.. status.Missing.Select(ApiText.Snake)],
        ApiText.Snake(status.Note));
}
