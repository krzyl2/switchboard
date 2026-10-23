namespace Switchboard.Infrastructure.Runtime;

/// <summary>
/// Everything the process takes from its environment, read once. Holds secrets, so it is a
/// class with a redacting <see cref="ToString"/> rather than a record that would print them.
/// </summary>
public sealed class RuntimeEnvironment
{
    /// <summary>Where the Supervisor writes the add-on options.</summary>
    public const string DefaultOptionsPath = "/data/options.json";

    public string? SupervisorToken { get; init; }

    public string? HomeAssistantUrl { get; init; }

    public string? HomeAssistantToken { get; init; }

    public string? MqttHost { get; init; }

    public string? MqttPort { get; init; }

    public string? MqttUsername { get; init; }

    public string? MqttPassword { get; init; }

    /// <summary>Null when the variable is absent; an empty string still counts as set.</summary>
    public string? ModeOverride { get; init; }

    public string? LogLevelOverride { get; init; }

    public string OptionsPath { get; init; } = DefaultOptionsPath;

    /// <summary>Dev compose only: turns off the ingress source-address check.</summary>
    public bool DevTrustAllRequests { get; init; }

    public bool IsAddon => !string.IsNullOrEmpty(SupervisorToken);

    public static RuntimeEnvironment Read(Func<string, string?> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        var optionsPath = lookup(EnvironmentVariableNames.OptionsPath);
        return new RuntimeEnvironment
        {
            SupervisorToken = lookup(EnvironmentVariableNames.SupervisorToken),
            HomeAssistantUrl = lookup(EnvironmentVariableNames.HomeAssistantUrl),
            HomeAssistantToken = lookup(EnvironmentVariableNames.HomeAssistantToken),
            MqttHost = lookup(EnvironmentVariableNames.MqttHost),
            MqttPort = lookup(EnvironmentVariableNames.MqttPort),
            MqttUsername = lookup(EnvironmentVariableNames.MqttUsername),
            MqttPassword = lookup(EnvironmentVariableNames.MqttPassword),
            ModeOverride = lookup(EnvironmentVariableNames.Mode),
            LogLevelOverride = lookup(EnvironmentVariableNames.LogLevel),
            OptionsPath = string.IsNullOrWhiteSpace(optionsPath) ? DefaultOptionsPath : optionsPath,
            DevTrustAllRequests = bool.TryParse(lookup(EnvironmentVariableNames.DevTrustAllRequests), out var trust) && trust,
        };
    }

    public override string ToString() =>
        $"RuntimeEnvironment {{ Addon = {IsAddon}, OptionsPath = {OptionsPath}, DevTrustAllRequests = {DevTrustAllRequests} }}";
}
