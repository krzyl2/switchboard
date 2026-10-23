using Microsoft.Extensions.Options;

namespace Switchboard.Application.Options;

/// <summary>Bound from <c>Switchboard:Mqtt</c>.</summary>
public sealed class MqttOptions
{
    public const string SectionName = "Switchboard:Mqtt";

    /// <summary>Supervisor service-discovery endpoint that hands out broker credentials (PKG-04).</summary>
    public required string SupervisorServiceUrl { get; init; }

    public required string DiscoveryPrefix { get; init; }

    /// <summary>Root of every non-discovery topic we publish; the topic guard refuses anything outside it.</summary>
    public required string BaseTopic { get; init; }

    public required string ClientId { get; init; }

    public required TimeSpan ReconnectMinDelay { get; init; }

    public required TimeSpan ReconnectMaxDelay { get; init; }

    /// <summary>The broker drops a session that stays silent for 1.5 times this and publishes the Last Will.</summary>
    public required TimeSpan KeepAlivePeriod { get; init; }

    /// <summary>
    /// Limit for the OFF / offline / disconnect sequence at shutdown. Must leave room inside the
    /// container's stop grace for the Home Assistant close, or a broker that stops acknowledging gets
    /// the process killed before that socket is closed.
    /// </summary>
    public required TimeSpan ShutdownTimeout { get; init; }
}

public sealed class MqttOptionsValidator(IOptions<HeartbeatOptions> heartbeat) : IValidateOptions<MqttOptions>
{
    public ValidateOptionsResult Validate(string? name, MqttOptions options)
    {
        const string section = MqttOptions.SectionName;
        var failures = new List<string>();

        if (!Uri.TryCreate(options.SupervisorServiceUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add($"{section}:SupervisorServiceUrl must be an absolute http:// or https:// URL.");
        }

        // Single topic levels only: the topic guard reasons about levels, and a prefix containing
        // '/' or a wildcard would widen what it lets through.
        RequireTopicLevel(failures, $"{section}:DiscoveryPrefix", options.DiscoveryPrefix);
        RequireTopicLevel(failures, $"{section}:BaseTopic", options.BaseTopic);

        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            failures.Add($"{section}:ClientId must not be empty.");
        }

        OptionsValidation.RequireDelayRange(failures, section, options.ReconnectMinDelay, options.ReconnectMaxDelay);

        // The keep-alive decides how long the broker holds a frozen session, and with it the retained
        // "online" a restarted Home Assistant reads; keep it below the expiry HA applies to the state.
        OptionsValidation.RequirePositive(failures, $"{section}:KeepAlivePeriod", options.KeepAlivePeriod);
        if (HeartbeatExpiry() is { } expireAfter && options.KeepAlivePeriod >= expireAfter)
        {
            failures.Add($"{section}:KeepAlivePeriod must be shorter than {HeartbeatOptions.SectionName}:ExpireAfter.");
        }

        OptionsValidation.RequirePositive(failures, $"{section}:ShutdownTimeout", options.ShutdownTimeout);
        return OptionsValidation.Result(failures);
    }

    // An invalid heartbeat section is reported by its own validator; reporting it again from here
    // would only duplicate the message.
    private TimeSpan? HeartbeatExpiry()
    {
        try
        {
            return heartbeat.Value.ExpireAfter;
        }
        catch (OptionsValidationException)
        {
            return null;
        }
    }

    private static void RequireTopicLevel(List<string> failures, string key, string? value)
    {
        if (!TopicLevel.IsPlain(value))
        {
            failures.Add($"{key} must be one topic level of ASCII letters, digits, '_' or '-'.");
        }
    }
}

/// <summary>Shared definition of a "plain" MQTT topic level, used by validation and the topic guard.</summary>
public static class TopicLevel
{
    public static bool IsPlain(string? level) =>
        !string.IsNullOrEmpty(level)
        && level.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}
