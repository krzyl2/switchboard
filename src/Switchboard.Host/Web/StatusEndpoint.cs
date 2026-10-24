using Microsoft.AspNetCore.Mvc;
using Switchboard.Application;
using Switchboard.Application.Connectivity;
using Switchboard.Application.Health;
using Switchboard.Application.Modes;

namespace Switchboard.Host.Web;

/// <summary>
/// Shape of GET api/status. Holds no secret by construction: none of its inputs carry one.
/// <c>mode</c> is the effective mode: the add-on option is only its ceiling.
/// </summary>
public sealed record StatusResponse(string Version, string Mode, string HomeAssistant, string Mqtt, bool Healthy);

internal static class StatusEndpoint
{
    public static IEndpointRouteBuilder MapStatusApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/status", (
                [FromServices] AppVersion version,
                [FromServices] IModeSource mode,
                [FromServices] IHomeAssistantConnectionState homeAssistant,
                [FromServices] IMqttConnectionState mqtt,
                [FromServices] IHealthStatus health) =>
            TypedResults.Json(new StatusResponse(
                version.Value,
                ModeName(mode.Current.Effective),
                StateName(homeAssistant.Current),
                StateName(mqtt.Current),
                health.IsHealthy)));
        return endpoints;
    }

    private static string ModeName(OperatingMode mode) => mode switch
    {
        OperatingMode.Active => "active",
        _ => "shadow",
    };

    private static string StateName(ConnectionState state) => state switch
    {
        ConnectionState.Connected => "connected",
        ConnectionState.Connecting => "connecting",
        _ => "disconnected",
    };
}
