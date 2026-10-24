using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Options;
using Switchboard.Application.State;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Infrastructure.Persistence;

/// <summary>
/// <c>/data/state.json</c>, schema version 1. Read as untrusted input (a user may edit it): exact
/// keys, exact types, values in range, at most StateMaxFileBytes. Anything else renames the file to
/// <c>state.json.corrupt</c> and starts fresh with a warning; it never stops the process. Written
/// atomically: a temporary file is written and flushed, then moved over the old one, so a crash
/// mid-write leaves the previous state whole.
/// </summary>
public sealed class RuntimeStateFile : IRuntimeStateStore
{
    public const string FileName = "state.json";
    public const string CorruptSuffix = ".corrupt";
    public const string TemporarySuffix = ".tmp";

    // Bounds on untrusted content, far above anything a valid configuration produces (64 devices).
    private const int MaxDevices = 256;
    private const int MaxStages = 64;
    private const int MaxDiscovered = 2048;

    private readonly string _directory;
    private readonly int _maxBytes;
    private readonly ILogger<RuntimeStateFile> _logger;

    public RuntimeStateFile(string dataDirectory, PersistenceOptions options, ILogger<RuntimeStateFile> logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        ArgumentNullException.ThrowIfNull(options);
        _directory = dataDirectory;
        _maxBytes = options.StateMaxFileBytes;
        _logger = logger;
    }

    public string FilePath => Path.Combine(_directory, FileName);

    public RuntimeStateLoad Load()
    {
        byte[] bytes;
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists)
            {
                return new RuntimeStateLoad(RuntimeState.Fresh, StateFileStatus.Fresh, null);
            }

            if (info.Length > _maxBytes)
            {
                return Reset($"it is larger than {_maxBytes} bytes");
            }

            bytes = File.ReadAllBytes(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var warning = $"state.json could not be read ({ex.Message}); starting with full budgets, kill switch off and shadow mode.";
            _logger.LogWarning("{Warning}", warning);
            return new RuntimeStateLoad(RuntimeState.Fresh, StateFileStatus.ResetCorrupt, warning);
        }

        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            return new RuntimeStateLoad(Parse(document.RootElement), StateFileStatus.Loaded, null);
        }
        catch (JsonException ex)
        {
            return Reset($"it is not valid JSON ({ex.Message})");
        }
        catch (InvalidStateException ex)
        {
            return Reset(ex.Message);
        }
    }

    public void Save(RuntimeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var payload = Serialize(state);
        if (payload.Length > _maxBytes)
        {
            throw new IOException($"the state is {payload.Length} bytes, above the {_maxBytes} byte limit");
        }

        Directory.CreateDirectory(_directory);
        var temporary = FilePath + TemporarySuffix;
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, FilePath, overwrite: true);
    }

    internal static byte[] Serialize(RuntimeState state)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", RuntimeState.SchemaVersion);
            WriteTime(writer, "saved_at", state.SavedAt);
            writer.WriteBoolean("clean_shutdown", state.CleanShutdown);
            writer.WriteBoolean("kill_switch", state.KillSwitch);
            WriteTime(writer, "kill_switch_changed_at", state.KillSwitchChangedAt);
            writer.WriteString("ui_mode", state.UiMode == OperatingMode.Active ? "active" : "shadow");

            writer.WriteStartObject("phases");
            foreach (var (phase, memory) in state.Budgets.OrderBy(p => p.Key))
            {
                writer.WriteStartObject(phase.ToString());
                writer.WriteNumber("budget", memory.Budget);
                WriteTime(writer, "at", memory.At);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();

            writer.WriteStartObject("devices");
            foreach (var (device, stages) in state.Devices.OrderBy(d => d.Key))
            {
                writer.WriteStartObject(device.Value);
                writer.WriteStartArray("stages");
                foreach (var stage in stages)
                {
                    if (stage is null)
                    {
                        writer.WriteNullValue();
                        continue;
                    }

                    writer.WriteStartObject();
                    switch (stage.Applied)
                    {
                        case RelayApplied relay:
                            writer.WriteString("applied", relay.On ? "on" : "off");
                            break;
                        case SetpointApplied setpoint:
                            writer.WriteNumber("setpoint_a", setpoint.Setpoint.Value);
                            break;
                    }

                    WriteTime(writer, "at", stage.At);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndObject();

            writer.WriteStartObject("mqtt");
            writer.WriteStartArray("discovered");
            foreach (var entry in state.Discovered)
            {
                writer.WriteStringValue(entry);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static RuntimeState Parse(JsonElement root)
    {
        var keys = Keys(root, "the document", "schema_version", "saved_at", "clean_shutdown", "kill_switch", "kill_switch_changed_at", "ui_mode", "phases", "devices", "mqtt");
        if (keys["schema_version"].ValueKind != JsonValueKind.Number || !keys["schema_version"].TryGetInt32(out var version) || version != RuntimeState.SchemaVersion)
        {
            throw new InvalidStateException($"schema_version is not {RuntimeState.SchemaVersion}");
        }

        var uiMode = keys["ui_mode"].ValueKind == JsonValueKind.String ? keys["ui_mode"].GetString() : null;
        if (uiMode is not ("shadow" or "active"))
        {
            throw new InvalidStateException("ui_mode is neither shadow nor active");
        }

        var budgets = ImmutableDictionary.CreateBuilder<Phase, BudgetMemory>();
        foreach (var property in Object(keys["phases"], "phases", Enum.GetValues<Phase>().Length))
        {
            if (!Enum.TryParse<Phase>(property.Name, ignoreCase: false, out var phase) || !Enum.IsDefined(phase) || property.Name != phase.ToString())
            {
                throw new InvalidStateException($"phases.{property.Name} is not a phase");
            }

            var entry = Keys(property.Value, $"phases.{property.Name}", "budget", "at");
            var budget = Number(entry["budget"], $"phases.{property.Name}.budget");
            if (budget is < 0 or > 1)
            {
                throw new InvalidStateException($"phases.{property.Name}.budget is outside [0, 1]");
            }

            budgets.Add(phase, new BudgetMemory(budget, Time(entry["at"], $"phases.{property.Name}.at")!.Value));
        }

        var devices = ImmutableDictionary.CreateBuilder<DeviceName, ImmutableArray<StageLastKnown?>>();
        foreach (var property in Object(keys["devices"], "devices", MaxDevices))
        {
            if (!DeviceName.TryParse(property.Name, out var device))
            {
                throw new InvalidStateException($"devices.{property.Name} is not a device name");
            }

            var entry = Keys(property.Value, $"devices.{property.Name}", "stages");
            if (entry["stages"].ValueKind != JsonValueKind.Array || entry["stages"].GetArrayLength() is 0 or > MaxStages)
            {
                throw new InvalidStateException($"devices.{property.Name}.stages is not a list of 1 to {MaxStages} entries");
            }

            devices.Add(device.Value, [.. entry["stages"].EnumerateArray().Select((s, i) => Stage(s, $"devices.{property.Name}.stages[{i}]"))]);
        }

        var mqtt = Keys(keys["mqtt"], "mqtt", "discovered");
        if (mqtt["discovered"].ValueKind != JsonValueKind.Array || mqtt["discovered"].GetArrayLength() > MaxDiscovered)
        {
            throw new InvalidStateException($"mqtt.discovered is not a list of at most {MaxDiscovered} entries");
        }

        var discovered = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var entry in mqtt["discovered"].EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || !IsDiscoveryKey(entry.GetString()) || !discovered.Add(entry.GetString()!))
            {
                throw new InvalidStateException("mqtt.discovered holds an entry that is not a unique component/object_id");
            }
        }

        return new RuntimeState(
            Time(keys["saved_at"], "saved_at", nullable: true),
            Bool(keys["clean_shutdown"], "clean_shutdown"),
            Bool(keys["kill_switch"], "kill_switch"),
            Time(keys["kill_switch_changed_at"], "kill_switch_changed_at", nullable: true),
            uiMode == "active" ? OperatingMode.Active : OperatingMode.Shadow,
            budgets.ToImmutable(),
            devices.ToImmutable(),
            discovered.ToImmutable());
    }

    private static StageLastKnown? Stage(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("applied", out _))
        {
            var relay = Keys(element, path, "applied", "at");
            var applied = relay["applied"].ValueKind == JsonValueKind.String ? relay["applied"].GetString() : null;
            if (applied is not ("on" or "off"))
            {
                throw new InvalidStateException($"{path}.applied is neither on nor off");
            }

            return new StageLastKnown(new RelayApplied(applied == "on"), Time(relay["at"], $"{path}.at")!.Value);
        }

        var modulating = Keys(element, path, "setpoint_a", "at");
        var setpoint = Number(modulating["setpoint_a"], $"{path}.setpoint_a");
        if (setpoint < 0)
        {
            throw new InvalidStateException($"{path}.setpoint_a is negative");
        }

        return new StageLastKnown(new SetpointApplied(new Amperes(setpoint)), Time(modulating["at"], $"{path}.at")!.Value);
    }

    /// <summary>The object's properties, which must be exactly <paramref name="expected"/>, each once.</summary>
    private static Dictionary<string, JsonElement> Keys(JsonElement element, string path, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidStateException($"{path} is not an object");
        }

        var found = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name, StringComparer.Ordinal) || !found.TryAdd(property.Name, property.Value))
            {
                throw new InvalidStateException($"{path} has an unexpected or repeated key '{property.Name}'");
            }
        }

        if (found.Count != expected.Length)
        {
            throw new InvalidStateException($"{path} lacks {string.Join(", ", expected.Where(k => !found.ContainsKey(k)))}");
        }

        return found;
    }

    private static List<JsonProperty> Object(JsonElement element, string path, int maxEntries)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidStateException($"{path} is not an object");
        }

        var properties = element.EnumerateObject().ToList();
        if (properties.Count > maxEntries || properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Count)
        {
            throw new InvalidStateException($"{path} has too many or repeated entries");
        }

        return properties;
    }

    private static double Number(JsonElement element, string path) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value) && double.IsFinite(value)
            ? value
            : throw new InvalidStateException($"{path} is not a finite number");

    private static bool Bool(JsonElement element, string path) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new InvalidStateException($"{path} is not true or false"),
    };

    private static DateTimeOffset? Time(JsonElement element, string path, bool nullable = false)
    {
        if (element.ValueKind == JsonValueKind.Null && nullable)
        {
            return null;
        }

        return element.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParseExact(element.GetString(), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
                ? value
                : throw new InvalidStateException($"{path} is not an ISO 8601 round-trip time");
    }

    private static void WriteTime(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is { } time)
        {
            // UTC with a Z: unambiguous for a person editing the file, and read back by "O".
            writer.WriteString(name, time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static bool IsDiscoveryKey(string? value)
    {
        var parts = value?.Split('/');
        return parts is { Length: 2 } && parts.All(p => p.Length is > 0 and <= 64 && p.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'));
    }

    private RuntimeStateLoad Reset(string reason)
    {
        var warning = $"state.json was not usable ({reason}); it was kept as {FileName}{CorruptSuffix} and Switchboard started with full budgets, kill switch off and shadow mode.";
        try
        {
            File.Move(FilePath, FilePath + CorruptSuffix, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warning = $"state.json was not usable ({reason}) and could not be renamed ({ex.Message}); Switchboard started with full budgets, kill switch off and shadow mode.";
        }

        _logger.LogWarning("{Warning}", warning);
        return new RuntimeStateLoad(RuntimeState.Fresh, StateFileStatus.ResetCorrupt, warning);
    }

    private sealed class InvalidStateException(string message) : Exception(message);
}
