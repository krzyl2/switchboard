using Switchboard.Application.Options;
using Switchboard.Infrastructure.Runtime;

namespace Switchboard.Infrastructure.HomeAssistant;

/// <summary>Where and as whom to connect. Carries the token, so it never prints it.</summary>
public sealed class HomeAssistantEndpoint(Uri webSocketUri, string accessToken)
{
    public Uri WebSocketUri { get; } = webSocketUri;

    public string AccessToken { get; } = accessToken;

    /// <summary>Scheme, host, port and path only: no user info, query or token.</summary>
    public string DisplayUri => WebSocketUri.GetComponents(
        UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);

    public override string ToString() => DisplayUri;
}

/// <summary>Either an endpoint or the reason there is none; the add-on keeps serving its page either way.</summary>
public sealed record HomeAssistantEndpointResolution(HomeAssistantEndpoint? Endpoint, string? Problem)
{
    public static HomeAssistantEndpointResolution Resolve(RuntimeEnvironment environment, HomeAssistantOptions options)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(options);

        if (environment.IsAddon)
        {
            return new(new HomeAssistantEndpoint(new Uri(options.SupervisorWebSocketUrl), environment.SupervisorToken!), null);
        }

        if (string.IsNullOrWhiteSpace(environment.HomeAssistantUrl) || string.IsNullOrWhiteSpace(environment.HomeAssistantToken))
        {
            return new(null,
                $"Standalone mode needs {EnvironmentVariableNames.HomeAssistantUrl} and {EnvironmentVariableNames.HomeAssistantToken}; Home Assistant stays disconnected.");
        }

        return HomeAssistantUrls.TryGetWebSocketUri(environment.HomeAssistantUrl, out var uri)
            ? new(new HomeAssistantEndpoint(uri, environment.HomeAssistantToken), null)
            : new(null, $"{EnvironmentVariableNames.HomeAssistantUrl} is not an http(s):// or ws(s):// URL; Home Assistant stays disconnected.");
    }
}
