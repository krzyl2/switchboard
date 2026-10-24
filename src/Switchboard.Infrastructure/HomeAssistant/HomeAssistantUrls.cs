using System.Diagnostics.CodeAnalysis;

namespace Switchboard.Infrastructure.HomeAssistant;

public static class HomeAssistantUrls
{
    private const string WebSocketPath = "api/websocket";
    private const string RestPath = "api/";

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

    /// <summary>
    /// The REST API base (ending in <c>/api/</c>) on the same origin as the WebSocket: http(s) as
    /// given, ws becomes http and wss https, and a trailing <c>/api/websocket</c> is dropped.
    /// </summary>
    public static bool TryGetRestBaseUri(string? url, [NotNullWhen(true)] out Uri? restBaseUri)
    {
        restBaseUri = null;
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var parsed) || string.IsNullOrEmpty(parsed.Host))
        {
            return false;
        }

        var scheme = parsed.Scheme switch
        {
            "http" or "ws" => Uri.UriSchemeHttp,
            "https" or "wss" => Uri.UriSchemeHttps,
            _ => null,
        };
        if (scheme is null)
        {
            return false;
        }

        var path = parsed.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/" + WebSocketPath, StringComparison.Ordinal))
        {
            path = path[..^(WebSocketPath.Length + 1)];
        }

        var builder = new UriBuilder(parsed)
        {
            Scheme = scheme,
            Port = parsed.IsDefaultPort ? -1 : parsed.Port,
            Path = path + "/" + RestPath,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        restBaseUri = builder.Uri;
        return true;
    }
}
