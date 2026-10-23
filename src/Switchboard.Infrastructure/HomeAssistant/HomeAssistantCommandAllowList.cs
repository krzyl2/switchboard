using System.Collections.Frozen;

namespace Switchboard.Infrastructure.HomeAssistant;

/// <summary>
/// Every WebSocket command the client may send, besides the auth handshake. This is the hard
/// boundary behind "never command an entity the user has not declared": a type that is not
/// listed here cannot leave the process. Stage 0 only keeps the connection alive. Types that
/// change state in Home Assistant (call_service, fire_event, execute_script, ...) are added only
/// together with the declared-actuator gate that is allowed to use them.
/// </summary>
public static class HomeAssistantCommandAllowList
{
    public const string Ping = "ping";

    public static FrozenSet<string> Allowed { get; } = new[] { Ping }.ToFrozenSet(StringComparer.Ordinal);

    public static void EnsureAllowed(string type)
    {
        if (type is null || !Allowed.Contains(type))
        {
            throw new HomeAssistantCommandRefusedException(type);
        }
    }
}

public sealed class HomeAssistantCommandRefusedException(string? type)
    : InvalidOperationException($"Home Assistant command type '{type}' is not on the outbound allow-list.")
{
    public string? CommandType { get; } = type;
}
