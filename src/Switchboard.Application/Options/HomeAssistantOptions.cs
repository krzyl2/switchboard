using Microsoft.Extensions.Options;

namespace Switchboard.Application.Options;

/// <summary>Bound from <c>Switchboard:HomeAssistant</c>.</summary>
public sealed class HomeAssistantOptions
{
    public const string SectionName = "Switchboard:HomeAssistant";

    /// <summary>Supervisor proxy endpoint used in add-on mode.</summary>
    public required string SupervisorWebSocketUrl { get; init; }

    /// <summary>Base of the Home Assistant REST API through the Supervisor proxy (add-on mode), ending in '/'.</summary>
    public required string SupervisorRestUrl { get; init; }

    public required TimeSpan PingInterval { get; init; }

    /// <summary>A ping not answered within this time means the link is dead, even if TCP looks alive.</summary>
    public required TimeSpan PongTimeout { get; init; }

    public required TimeSpan ReconnectMinDelay { get; init; }

    /// <summary>Also the wait after a rejected token, so a bad token never produces a tight loop.</summary>
    public required TimeSpan ReconnectMaxDelay { get; init; }

    /// <summary>A freshness probe (one entity state read) not answered within this time is abandoned.</summary>
    public required TimeSpan StateReadTimeout { get; init; }
}

public sealed class HomeAssistantOptionsValidator : IValidateOptions<HomeAssistantOptions>
{
    public ValidateOptionsResult Validate(string? name, HomeAssistantOptions options)
    {
        const string section = HomeAssistantOptions.SectionName;
        var failures = new List<string>();

        if (!Uri.TryCreate(options.SupervisorWebSocketUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeWs && uri.Scheme != Uri.UriSchemeWss))
        {
            failures.Add($"{section}:SupervisorWebSocketUrl must be an absolute ws:// or wss:// URL.");
        }

        if (!Uri.TryCreate(options.SupervisorRestUrl, UriKind.Absolute, out var rest)
            || (rest.Scheme != Uri.UriSchemeHttp && rest.Scheme != Uri.UriSchemeHttps)
            || !rest.AbsolutePath.EndsWith('/'))
        {
            failures.Add($"{section}:SupervisorRestUrl must be an absolute http:// or https:// URL ending in '/'.");
        }

        OptionsValidation.RequirePositive(failures, $"{section}:PingInterval", options.PingInterval);
        OptionsValidation.RequirePositive(failures, $"{section}:PongTimeout", options.PongTimeout);
        if (options.PongTimeout >= options.PingInterval)
        {
            failures.Add($"{section}:PongTimeout must be shorter than PingInterval, or pings overlap.");
        }

        OptionsValidation.RequireDelayRange(failures, section, options.ReconnectMinDelay, options.ReconnectMaxDelay);

        // A probe slower than a keepalive period means the link itself is in trouble.
        OptionsValidation.RequirePositive(failures, $"{section}:StateReadTimeout", options.StateReadTimeout);
        if (options.StateReadTimeout >= options.PingInterval)
        {
            failures.Add($"{section}:StateReadTimeout must be shorter than PingInterval.");
        }

        return OptionsValidation.Result(failures);
    }
}
