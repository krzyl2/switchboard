using System.Text.Json;

namespace Switchboard.Application.Modes;

/// <param name="MqttEnablingCommands">
/// Whether MQTT may also move the enabling way (allow control, release the kill switch, an override or EV command that can raise load); off, MQTT only makes
/// things safer (kill switch on, control off, <c>force_off</c>, EV PV only).
/// </param>
public sealed record ResolvedAddonOptions(
    OperatingMode Mode,
    AddonLogLevel LogLevel,
    bool MqttEnablingCommands,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Turns the add-on options document plus the standalone overrides into a mode, a log level and the MQTT direction.
/// Fail-safe by construction: only the exact string "active" yields <see cref="OperatingMode.Active"/>;
/// a typo, a wrong case, a missing or corrupt file all land in shadow mode, because a mistaken
/// "active" switches 5 kW loads in someone's house while a mistaken "shadow" only logs. The same holds for
/// <c>mqtt_enabling_commands</c>: only a JSON <c>true</c> (or exactly <c>true</c> in the environment) lets MQTT allow control or release the kill switch.
/// </summary>
public static class AddonOptionsResolver
{
    /// <summary>The only mode literal the code acts on. Everything else means shadow.</summary>
    public const string ActiveModeValue = "active";

    /// <summary>The add-on option that lets MQTT move the enabling way; named in the log line of a refused command.</summary>
    public const string MqttEnablingCommandsKey = "mqtt_enabling_commands";

    private const string ShadowModeValue = "shadow";
    private const string TrueValue = "true";
    private const string FalseValue = "false";
    private const string ModeKey = "mode";
    private const string LogLevelKey = "log_level";

    /// <param name="optionsJson">Contents of options.json, or null when the file is missing or unreadable.</param>
    /// <param name="modeOverride">SWITCHBOARD_MODE when set (even when empty); wins over the file.</param>
    /// <param name="logLevelOverride">SWITCHBOARD_LOG_LEVEL when set; wins over the file.</param>
    /// <param name="mqttEnablingOverride">SWITCHBOARD_MQTT_ENABLING_COMMANDS when set (even when empty); wins over the file.</param>
    public static ResolvedAddonOptions Resolve(string? optionsJson, string? modeOverride, string? logLevelOverride, string? mqttEnablingOverride = null)
    {
        var warnings = new List<string>();
        var (fileMode, fileLogLevel, fileMqttEnabling) = ReadFile(optionsJson, warnings);

        var rawMode = modeOverride ?? fileMode;
        var mode = string.Equals(rawMode, ActiveModeValue, StringComparison.Ordinal)
            ? OperatingMode.Active
            : OperatingMode.Shadow;
        if (rawMode is not null && mode == OperatingMode.Shadow
            && !string.Equals(rawMode, ShadowModeValue, StringComparison.Ordinal))
        {
            warnings.Add("The configured mode is not recognised; running in shadow mode.");
        }

        var rawLogLevel = logLevelOverride ?? fileLogLevel;
        var logLevel = ParseLogLevel(rawLogLevel);
        if (rawLogLevel is not null && logLevel is null)
        {
            warnings.Add("The configured log_level is not recognised; using info.");
        }

        var rawMqttEnabling = mqttEnablingOverride ?? fileMqttEnabling;
        if (rawMqttEnabling is not null && rawMqttEnabling is not (TrueValue or FalseValue))
        {
            warnings.Add($"The configured {MqttEnablingCommandsKey} is not true or false; MQTT can only make things safer.");
        }

        return new ResolvedAddonOptions(mode, logLevel ?? AddonLogLevel.Info, string.Equals(rawMqttEnabling, TrueValue, StringComparison.Ordinal), warnings);
    }

    private static (string? Mode, string? LogLevel, string? MqttEnabling) ReadFile(string? optionsJson, List<string> warnings)
    {
        if (optionsJson is null)
        {
            return (null, null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(optionsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                warnings.Add("The options file is not a JSON object; running in shadow mode.");
                return (null, null, null);
            }

            var root = document.RootElement;
            return (ReadString(root, ModeKey), ReadString(root, LogLevelKey), ReadFlag(root, MqttEnablingCommandsKey));
        }
        catch (JsonException)
        {
            warnings.Add("The options file is not valid JSON; running in shadow mode.");
            return (null, null, null);
        }
    }

    // A non-string value (number, bool, object) is reported as "" so it counts as set-but-unrecognised.
    private static string? ReadString(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : string.Empty
            : null;

    // Only a JSON boolean counts; anything else (the string "true" too) is reported as "" so it counts as set-but-unrecognised, and off.
    private static string? ReadFlag(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value)
            ? value.ValueKind switch { JsonValueKind.True => TrueValue, JsonValueKind.False => FalseValue, _ => string.Empty }
            : null;

    private static AddonLogLevel? ParseLogLevel(string? raw) => raw switch
    {
        "debug" => AddonLogLevel.Debug,
        "info" => AddonLogLevel.Info,
        "warning" => AddonLogLevel.Warning,
        _ => null,
    };
}
