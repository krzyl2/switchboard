using Microsoft.AspNetCore.Mvc;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Infrastructure.Replay;

namespace Switchboard.Host.Web;

/// <summary>One call the development sink received.</summary>
/// <param name="ServiceData">{ value } of a set_value (a number) or { option } of a select_option (text); null for turn_on and turn_off.</param>
public sealed record SinkEntryDto(DateTimeOffset At, string Domain, string Service, string EntityId, IReadOnlyDictionary<string, object>? ServiceData);

/// <summary>
/// GET api/dev/sink: what the development command sink received, oldest first. Mapped only in
/// standalone development replay (the only place the sink exists); never in an add-on.
/// </summary>
internal static class DevEndpoint
{
    public static IEndpointRouteBuilder MapDevApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/dev/sink", ([FromServices] ICallServiceChannel channel) =>
            channel is DevCommandSink sink
                ? Results.Json(sink.Entries.Select(e => new SinkEntryDto(e.At, e.Domain, e.Service, e.EntityId, e.ServiceData)).ToList())
                : Results.NotFound());
        return endpoints;
    }
}
