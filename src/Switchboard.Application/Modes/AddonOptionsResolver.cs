using System.Text.Json;

namespace Switchboard.Application.Modes;

public sealed record ResolvedAddonOptions(
    OperatingMode Mode,
    AddonLogLevel LogLevel,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Turns the add-on options document plus the standalone overrides into a mode and a log level.
/// Fail-safe by construction: only the exact string "active" yields <see cref="OperatingMode.Active"/>;
/// a typo, a wrong case, a missing or corrupt file all land in shadow mode, because a mistaken
/// "active" switches 5 kW loads in someone's house while a mistaken "shadow" only logs.
/// </summary>
public static class AddonOptionsResolver
{
    /// <summary>The only mode literal the code acts on. Everything else means shadow.</summary>
    public const string ActiveModeValue = "active";

    private const string ShadowModeValue = "shadow";
    private const string ModeKey = "mode";
    private const string LogLevelKey = "log_level";

    /// <param name="optionsJson">Contents of options.json, or null when the file is missing or unreadable.</param>
    /// <param name="modeOverride">SWITCHBOARD_MODE when set (even when empty); wins over the file.</param>
    /// <param name="logLevelOverride">SWITCHBOARD_LOG_LEVEL when set; wins over the file.</param>
    public static ResolvedAddonOptions Resolve(string? optionsJson, string? modeOverride, string? logLevelOverride)
    {
        var warnings = new List<string>();
        var (fileMode, fileLogLevel) = ReadFile(optionsJson, warnings);

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

        return new ResolvedAddonOptions(mode, logLevel ?? AddonLogLevel.Info, warnings);
    }

    private static (string? Mode, string? LogLevel) ReadFile(string? optionsJson, List<string> warnings)
    {
        if (optionsJson is null)
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(optionsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                warnings.Add("The options file is not a JSON object; running in shadow mode.");
                return (null, null);
            }

            return (ReadString(document.RootElement, ModeKey), ReadString(document.RootElement, LogLevelKey));
        }
        catch (JsonException)
        {
            warnings.Add("The options file is not valid JSON; running in shadow mode.");
            return (null, null);
        }
    }

    // A non-string value (number, bool, object) is reported as "" so it counts as set-but-unrecognised.
    private static string? ReadString(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : string.Empty
            : null;

    private static AddonLogLevel? ParseLogLevel(string? raw) => raw switch
    {
        "debug" => AddonLogLevel.Debug,
        "info" => AddonLogLevel.Info,
        "warning" => AddonLogLevel.Warning,
        _ => null,
    };
}
