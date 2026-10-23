using System.Diagnostics.CodeAnalysis;

namespace Switchboard.Infrastructure.HomeAssistant;

public static class HomeAssistantUrls
{
    private const string WebSocketPath = "api/websocket";

    /// <summary>
    /// Users paste the address they open HA with (http://homeassistant:8123); the WebSocket API
    /// lives under /api/websocket on the same origin. A ws:// or wss:// URL is taken as-is.
    /// </summary>
    public static bool TryGetWebSocketUri(string? url, [NotNullWhen(true)] out Uri? webSocketUri)
    {
        webSocketUri = null;
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var parsed) || string.IsNullOrEmpty(parsed.Host))
        {
            return false;
        }

        if (parsed.Scheme == Uri.UriSchemeWs || parsed.Scheme == Uri.UriSchemeWss)
        {
            webSocketUri = parsed;
            return true;
        }

        var scheme = parsed.Scheme switch
        {
            "http" => Uri.UriSchemeWs,
            "https" => Uri.UriSchemeWss,
            _ => null,
        };
        if (scheme is null)
        {
            return false;
        }

        var builder = new UriBuilder(parsed)
        {
            Scheme = scheme,
            Port = parsed.IsDefaultPort ? -1 : parsed.Port,
            Path = parsed.AbsolutePath.TrimEnd('/') + "/" + WebSocketPath,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        webSocketUri = builder.Uri;
        return true;
    }
}
