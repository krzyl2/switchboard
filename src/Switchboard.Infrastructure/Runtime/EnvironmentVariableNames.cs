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

    /// <summary>Where the configuration document is read from; defaults to the add-on config folder.</summary>
    public const string ConfigPath = "SWITCHBOARD_CONFIG_PATH";

    /// <summary>Where runtime state (state.json) and the decision log are written; defaults to /data.</summary>
    public const string DataPath = "SWITCHBOARD_DATA_PATH";

    /// <summary>Standalone development only: replay recorded history (csv or csv.gz) instead of connecting to Home Assistant.</summary>
    public const string DevReplay = "SWITCHBOARD_DEV_REPLAY";

    /// <summary>Replay speed factor (default 1).</summary>
    public const string DevReplaySpeed = "SWITCHBOARD_DEV_REPLAY_SPEED";

    /// <summary>Standalone development with replay only: a folder of Home Assistant discovery answers (states, entity and device registry) for the setup wizard.</summary>
    public const string DevDiscovery = "SWITCHBOARD_DEV_DISCOVERY";

    /// <summary>Standalone development with replay only: a JSON file of simulated loads that answer the development command sink's calls.</summary>
    public const string DevPlant = "SWITCHBOARD_DEV_PLANT";
}
