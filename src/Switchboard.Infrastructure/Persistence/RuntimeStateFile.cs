using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Options;
using Switchboard.Application.State;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Infrastructure.Persistence;

/// <summary>
/// <c>/data/state.json</c>. Read as untrusted input (a user may edit it): exact keys, exact types,
/// values in range, at most StateMaxFileBytes. Anything else renames the file to
/// <c>state.json.corrupt</c> and starts fresh with a warning; it never stops the process. Written
/// atomically: a temporary file is written and flushed, then moved over the old one, so a crash
/// mid-write leaves the previous state whole.
/// <para>
/// Schema 2 (Stage 3) adds the manual overrides and the circuit-group budgets. A schema 1 file
/// (0.3.x) reads as schema 2 with neither. While the state holds neither, the file is still written
/// as schema 1, so a rollback to 0.3.x keeps budgets, kill switch and the retraction ledger; 0.3.x
/// sets a schema 2 file aside as corrupt.
/// </para>
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
    private const int MaxGroups = 64;

    private static readonly string[] Version1Keys = ["schema_version", "saved_at", "clean_shutdown", "kill_switch", "kill_switch_changed_at", "ui_mode", "phases", "devices", "mqtt"];
    private static readonly string[] Version2Keys = [.. Version1Keys, "overrides", "group_budgets"];

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
            // Auto is the absence of an override: never written (it would not read back).
            var overrides = state.Overrides.Where(o => o.Value.Mode != OverrideMode.Auto).OrderBy(o => o.Key).ToList();
            var version = overrides.Count == 0 && state.GroupBudgets.IsEmpty ? RuntimeState.LegacySchemaVersion : RuntimeState.SchemaVersion;
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", version);
            WriteTime(writer, "saved_at", state.SavedAt);
            writer.WriteBoolean("clean_shutdown", state.CleanShutdown);
            writer.WriteBoolean("kill_switch", state.KillSwitch);
            WriteTime(writer, "kill_switch_changed_at", state.KillSwitchChangedAt);
            writer.WriteString("ui_mode", state.UiMode == OperatingMode.Active ? "active" : "shadow");

            writer.WriteStartObject("phases");
            WriteBudgets(writer, state.Budgets);
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

            if (version == RuntimeState.SchemaVersion)
            {
                writer.WriteStartObject("overrides");
                foreach (var (device, entry) in overrides)
                {
                    writer.WriteStartObject(device.Value);
                    writer.WriteString("mode", Snake(entry.Mode));
                    WriteTime(writer, "changed_at", entry.ChangedAt);
                    if (entry.Source is { } source)
                    {
                        writer.WriteString("source", Snake(source));
                    }
                    else
                    {
                        writer.WriteNull("source");
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
                writer.WriteStartObject("group_budgets");
                foreach (var (group, budgets) in state.GroupBudgets.OrderBy(g => g.Key))
                {
                    writer.WriteStartObject(group.Value);
                    WriteBudgets(writer, budgets);
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteBudgets(Utf8JsonWriter writer, ImmutableDictionary<Phase, BudgetMemory> budgets)
    {
        foreach (var (phase, memory) in budgets.OrderBy(p => p.Key))
        {
            writer.WriteStartObject(phase.ToString());
            writer.WriteNumber("budget", memory.Budget);
            WriteTime(writer, "at", memory.At);
            writer.WriteEndObject();
        }
    }

    private static RuntimeState Parse(JsonElement root)
    {
        var version = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("schema_version", out var declared)
            && declared.ValueKind == JsonValueKind.Number
            && declared.TryGetInt32(out var number)
                ? number
                : 0;
        if (version is not (RuntimeState.SchemaVersion or RuntimeState.LegacySchemaVersion))
        {
            throw new InvalidStateException($"schema_version is neither {RuntimeState.LegacySchemaVersion} nor {RuntimeState.SchemaVersion}");
        }

        var keys = Keys(root, "the document", version == RuntimeState.SchemaVersion ? Version2Keys : Version1Keys);

        var uiMode = keys["ui_mode"].ValueKind == JsonValueKind.String ? keys["ui_mode"].GetString() : null;
        if (uiMode is not ("shadow" or "active"))
        {
            throw new InvalidStateException("ui_mode is neither shadow nor active");
        }

        var budgets = Budgets(keys["phases"], "phases");

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

        var state = new RuntimeState(
            Time(keys["saved_at"], "saved_at", nullable: true),
            Bool(keys["clean_shutdown"], "clean_shutdown"),
            Bool(keys["kill_switch"], "kill_switch"),
            Time(keys["kill_switch_changed_at"], "kill_switch_changed_at", nullable: true),
            uiMode == "active" ? OperatingMode.Active : OperatingMode.Shadow,
            budgets,
            devices.ToImmutable(),
            discovered.ToImmutable());
        return version == RuntimeState.SchemaVersion
            ? state with { Overrides = Overrides(keys["overrides"]), GroupBudgets = GroupBudgets(keys["group_budgets"]) }
            : state;
    }

    private static ImmutableDictionary<Phase, BudgetMemory> Budgets(JsonElement element, string path)
    {
        var budgets = ImmutableDictionary.CreateBuilder<Phase, BudgetMemory>();
        foreach (var property in Object(element, path, Enum.GetValues<Phase>().Length))
        {
            if (!Enum.TryParse<Phase>(property.Name, ignoreCase: false, out var phase) || !Enum.IsDefined(phase) || property.Name != phase.ToString())
            {
                throw new InvalidStateException($"{path}.{property.Name} is not a phase");
            }

            var entry = Keys(property.Value, $"{path}.{property.Name}", "budget", "at");
            var budget = Number(entry["budget"], $"{path}.{property.Name}.budget");
            if (budget is < 0 or > 1)
            {
                throw new InvalidStateException($"{path}.{property.Name}.budget is outside [0, 1]");
            }

            budgets.Add(phase, new BudgetMemory(budget, Time(entry["at"], $"{path}.{property.Name}.at")!.Value));
        }

        return budgets.ToImmutable();
    }

    /// <summary>Only force_on and force_off are stored: auto is the absence of an override.</summary>
    private static ImmutableDictionary<DeviceName, DeviceOverride> Overrides(JsonElement element)
    {
        var overrides = ImmutableDictionary.CreateBuilder<DeviceName, DeviceOverride>();
        foreach (var property in Object(element, "overrides", MaxDevices))
        {
            if (!DeviceName.TryParse(property.Name, out var device))
            {
                throw new InvalidStateException($"overrides.{property.Name} is not a device name");
            }

            var path = $"overrides.{property.Name}";
            var entry = Keys(property.Value, path, "mode", "changed_at", "source");
            var mode = entry["mode"].ValueKind == JsonValueKind.String ? entry["mode"].GetString() : null;
            var parsed = Enum.GetValues<OverrideMode>().Where(m => m != OverrideMode.Auto && Snake(m) == mode).Select(m => (OverrideMode?)m).SingleOrDefault()
                ?? throw new InvalidStateException($"{path}.mode is neither force_on nor force_off");
            OverrideSource? source = null;
            if (entry["source"].ValueKind != JsonValueKind.Null)
            {
                var text = entry["source"].ValueKind == JsonValueKind.String ? entry["source"].GetString() : null;
                source = Enum.GetValues<OverrideSource>().Where(v => Snake(v) == text).Select(v => (OverrideSource?)v).SingleOrDefault()
                    ?? throw new InvalidStateException($"{path}.source is not api, mqtt or restored");
            }

            overrides.Add(device.Value, new DeviceOverride(parsed, Time(entry["changed_at"], $"{path}.changed_at", nullable: true), source));
        }

        return overrides.ToImmutable();
    }

    private static ImmutableDictionary<GroupName, ImmutableDictionary<Phase, BudgetMemory>> GroupBudgets(JsonElement element)
    {
        var groups = ImmutableDictionary.CreateBuilder<GroupName, ImmutableDictionary<Phase, BudgetMemory>>();
        foreach (var property in Object(element, "group_budgets", MaxGroups))
        {
            if (!GroupName.TryParse(property.Name, out var group))
            {
                throw new InvalidStateException($"group_budgets.{property.Name} is not a group name");
            }

            groups.Add(group.Value, Budgets(property.Value, $"group_budgets.{property.Name}"));
        }

        return groups.ToImmutable();
    }

    private static string Snake<TEnum>(TEnum value)
        where TEnum : struct, Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

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

        // A last-known setpoint is display only, never a protection input; a storage device's
        // discharge is negative (the signal layer accepts it), so only a non-finite value is refused.
        var modulating = Keys(element, path, "setpoint_a", "at");
        var setpoint = Number(modulating["setpoint_a"], $"{path}.setpoint_a");
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
