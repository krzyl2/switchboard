namespace Switchboard.Infrastructure.Runtime;

/// <summary>
/// Everything the process takes from its environment, read once. Holds secrets, so it is a
/// class with a redacting <see cref="ToString"/> rather than a record that would print them.
/// </summary>
public sealed class RuntimeEnvironment
{
    /// <summary>Where the Supervisor writes the add-on options.</summary>
    public const string DefaultOptionsPath = "/data/options.json";

    /// <summary>The configuration document in the add-on config folder (map: addon_config).</summary>
    public const string DefaultConfigPath = "/config/switchboard.yaml";

    /// <summary>The add-on's private data folder (map: data): runtime state and the decision log.</summary>
    public const string DefaultDataPath = "/data";

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

    public string ConfigPath { get; init; } = DefaultConfigPath;

    public string DataPath { get; init; } = DefaultDataPath;

    /// <summary>Recorded history to replay instead of Home Assistant (standalone development only); null when unset.</summary>
    public string? DevReplayPath { get; init; }

    /// <summary>The raw replay speed; validated when the signal source is chosen.</summary>
    public string? DevReplaySpeed { get; init; }

    /// <summary>Discovery fixtures for the setup wizard in development replay; null when unset.</summary>
    public string? DevDiscoveryPath { get; init; }

    /// <summary>Dev compose only: turns off the ingress source-address check.</summary>
    public bool DevTrustAllRequests { get; init; }

    public bool IsAddon => !string.IsNullOrEmpty(SupervisorToken);

    public static RuntimeEnvironment Read(Func<string, string?> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        var optionsPath = lookup(EnvironmentVariableNames.OptionsPath);
        var configPath = lookup(EnvironmentVariableNames.ConfigPath);
        var dataPath = lookup(EnvironmentVariableNames.DataPath);
        var replay = lookup(EnvironmentVariableNames.DevReplay);
        var discovery = lookup(EnvironmentVariableNames.DevDiscovery);
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
            ConfigPath = string.IsNullOrWhiteSpace(configPath) ? DefaultConfigPath : configPath,
            DataPath = string.IsNullOrWhiteSpace(dataPath) ? DefaultDataPath : dataPath,
            DevReplayPath = string.IsNullOrWhiteSpace(replay) ? null : replay,
            DevReplaySpeed = lookup(EnvironmentVariableNames.DevReplaySpeed),
            DevDiscoveryPath = string.IsNullOrWhiteSpace(discovery) ? null : discovery,
            DevTrustAllRequests = bool.TryParse(lookup(EnvironmentVariableNames.DevTrustAllRequests), out var trust) && trust,
        };
    }

    public override string ToString() =>
        $"RuntimeEnvironment {{ Addon = {IsAddon}, OptionsPath = {OptionsPath}, ConfigPath = {ConfigPath}, DataPath = {DataPath}, DevReplay = {DevReplayPath is not null}, DevDiscovery = {DevDiscoveryPath is not null}, DevTrustAllRequests = {DevTrustAllRequests} }}";
}
