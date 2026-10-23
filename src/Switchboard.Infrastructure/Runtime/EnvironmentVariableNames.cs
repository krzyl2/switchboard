namespace Switchboard.Infrastructure.Runtime;

/// <summary>The exact environment variable names the add-on and the standalone container read.</summary>
public static class EnvironmentVariableNames
{
    /// <summary>Injected by the Supervisor; its presence is what makes this an add-on run.</summary>
    public const string SupervisorToken = "SUPERVISOR_TOKEN";

    public const string HomeAssistantUrl = "SWITCHBOARD_HA_URL";
    public const string HomeAssistantToken = "SWITCHBOARD_HA_TOKEN";
    public const string MqttHost = "SWITCHBOARD_MQTT_HOST";
    public const string MqttPort = "SWITCHBOARD_MQTT_PORT";
    public const string MqttUsername = "SWITCHBOARD_MQTT_USERNAME";
    public const string MqttPassword = "SWITCHBOARD_MQTT_PASSWORD";
    public const string Mode = "SWITCHBOARD_MODE";
    public const string LogLevel = "SWITCHBOARD_LOG_LEVEL";
    public const string OptionsPath = "SWITCHBOARD_OPTIONS_PATH";
    public const string DevTrustAllRequests = "SWITCHBOARD_DEV_TRUST_ALL_REQUESTS";
}
