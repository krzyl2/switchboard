using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Switchboard.Application.Decisions;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Infrastructure.Persistence;

/// <summary>
/// One decision as one line of the daily decision log (MODE-03): snake_case keys and values, the
/// reason code as 04 §6 writes it, the list version (<c>codes</c>) and the inputs that produced it.
/// Reading is the reverse and treats the file as untrusted: a line that is not exactly a decision
/// of a known shape (a partial line after a crash, a gap marker, an edit) is not a decision.
/// </summary>
public static class DecisionJson
{
    public const int FormatVersion = 1;

    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    private static readonly FrozenDictionary<string, ReasonCode> ReasonsByName =
        ReasonCodes.All.ToFrozenDictionary(ReasonCodes.Name, StringComparer.Ordinal);

    public static string Serialize(Decision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return Write(writer =>
        {
            writer.WriteNumber("v", FormatVersion);
            writer.WriteNumber("codes", ReasonCodes.ListVersion);
            writer.WriteString("id", decision.Id);
            writer.WriteString("at", FormatTime(decision.At));
            writer.WriteString("scope", Snake(decision.Scope));
            WriteOptional(writer, "device", decision.Device?.Value);
            WriteOptional(writer, "stage", decision.Stage);
            WriteOptional(writer, "phase", decision.Phase?.ToString());
            writer.WriteString("action", Snake(decision.Action));
            writer.WriteString("reason", ReasonCodes.Name(decision.Reason));
            WriteOptional(writer, "overridden", decision.Overridden is { } overridden ? ReasonCodes.Name(overridden) : null);
            writer.WriteString("mode", Snake(decision.Mode));
            writer.WriteBoolean("commanded", decision.Commanded);
            WriteOptional(writer, "refused_by", decision.RefusedBy is { } refused ? ReasonCodes.Name(refused) : null);
            writer.WriteStartObject("inputs");
            WriteInputs(writer, decision.Inputs);
            writer.WriteEndObject();
        });
    }

    /// <summary>A marker for decisions lost while the log could not be written; readers skip it.</summary>
    public static string Gap(long dropped, DateTimeOffset firstAt, DateTimeOffset lastAt) => Write(writer =>
    {
        writer.WriteNumber("v", FormatVersion);
        writer.WriteStartObject("gap");
        writer.WriteNumber("dropped", dropped);
        writer.WriteString("first_at", FormatTime(firstAt));
        writer.WriteString("last_at", FormatTime(lastAt));
        writer.WriteEndObject();
    });

    /// <summary>The decision on this line, or null when the line is anything else.</summary>
    public static Decision? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || Int(root, "v") != FormatVersion
                || Int(root, "codes") != ReasonCodes.ListVersion)
            {
                return null;
            }

            var device = OptionalString(root, "device") is { } name ? DeviceName.Parse(name) : (DeviceName?)null;
            return new Decision(
                String(root, "id"),
                Time(root, "at"),
                EnumValue<DecisionScope>(root, "scope"),
                device,
                OptionalInt(root, "stage"),
                OptionalPhase(root, "phase"),
                EnumValue<DecisionAction>(root, "action"),
                Reason(String(root, "reason")),
                OptionalString(root, "overridden") is { } overridden ? Reason(overridden) : null,
                EnumValue<OperatingMode>(root, "mode"),
                Bool(root, "commanded"),
                OptionalString(root, "refused_by") is { } refused ? Reason(refused) : null,
                Inputs(Property(root, "inputs", JsonValueKind.Object)));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            return null;
        }
    }

    private static void WriteInputs(Utf8JsonWriter writer, DecisionInputs inputs)
    {
        if (inputs.Phase is { } phase)
        {
            writer.WriteStartObject("phase");
            writer.WriteString("phase", phase.Phase.ToString());
            writer.WriteBoolean("data_valid", phase.DataValid);
            WriteOptional(writer, "invalid_reason", phase.InvalidReason is { } invalid ? Snake(invalid) : null);
            WriteOptional(writer, "current_a", phase.Current?.Value);
            WriteOptional(writer, "effective_current_a", phase.EffectiveCurrent?.Value);
            WriteOptional(writer, "multiple", phase.Multiple);
            WriteOptional(writer, "band", phase.Band);
            WriteOptional(writer, "reaction", phase.Reaction is { } reaction ? Snake(reaction) : null);
            writer.WriteNumber("budget", phase.Budget);
            writer.WriteNumber("continuous_a", phase.Continuous.Value);
            writer.WriteNumber("reserve_threshold_a", phase.ReserveThreshold.Value);
            WriteOptional(writer, "headroom_a", phase.Headroom?.Value);
            WriteOptional(writer, "direction", phase.Direction is { } direction ? Snake(direction) : null);
            WriteOptional(writer, "reported_at", phase.ReportedAt is { } reported ? FormatTime(reported) : null);
            writer.WriteEndObject();
        }

        if (inputs.Device is { } device)
        {
            writer.WriteStartObject("device");
            WriteOptional(writer, "applied", device.Applied is RelayApplied relay ? (relay.On ? "on" : "off") : null);
            WriteOptional(writer, "setpoint_a", device.Applied is SetpointApplied setpoint ? setpoint.Setpoint.Value : null);
            writer.WriteBoolean("state_fresh", device.StateFresh);
            WriteOptional(writer, "state_invalid_reason", device.StateInvalidReason is { } invalid ? Snake(invalid) : null);
            WriteOptional(writer, "shed_rank", device.ShedRank);
            writer.WriteNumber("value_per_kwh", device.ValuePerKwh);
            WriteOptional(writer, "shed_priority", device.ShedPriority);
            writer.WriteNumber("min_on_remaining_s", device.MinOnRemaining.TotalSeconds);
            writer.WriteNumber("min_off_remaining_s", device.MinOffRemaining.TotalSeconds);
            writer.WriteBoolean("can_switch_off", device.CanSwitchOff);
            writer.WriteEndObject();
        }

        if (inputs.Mode is { } mode)
        {
            writer.WriteStartObject("mode");
            writer.WriteString("ceiling", Snake(mode.Ceiling));
            writer.WriteString("ui_choice", Snake(mode.UiChoice));
            writer.WriteString("note", Snake(mode.Note));
            writer.WriteEndObject();
        }

        if (inputs.KillSwitch is { } source)
        {
            writer.WriteString("kill_switch", Snake(source));
        }

        if (inputs.SafeStateCause is { } cause)
        {
            writer.WriteString("safe_state_cause", Snake(cause));
        }
    }

    private static DecisionInputs Inputs(JsonElement element)
    {
        PhaseDecisionInputs? phase = null;
        DeviceDecisionInputs? device = null;
        ModeDecisionInputs? mode = null;
        KillSwitchSource? killSwitch = null;
        SafeStateCause? cause = null;
        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "phase" when phase is null:
                    var p = property.Value;
                    phase = new PhaseDecisionInputs(
                        OptionalPhase(p, "phase") ?? throw new FormatException("inputs.phase.phase"),
                        Bool(p, "data_valid"),
                        OptionalEnum<ProtectionInvalidReason>(p, "invalid_reason"),
                        OptionalAmperes(p, "current_a"),
                        OptionalAmperes(p, "effective_current_a"),
                        OptionalDouble(p, "multiple"),
                        OptionalInt(p, "band"),
                        OptionalEnum<TierReaction>(p, "reaction"),
                        Double(p, "budget"),
                        new Amperes(Double(p, "continuous_a")),
                        new Amperes(Double(p, "reserve_threshold_a")),
                        OptionalAmperes(p, "headroom_a"),
                        OptionalEnum<FlowDirection>(p, "direction"),
                        OptionalString(p, "reported_at") is { } reported ? ParseTime(reported) : null);
                    break;
                case "device" when device is null:
                    var d = property.Value;
                    AppliedState? applied = OptionalString(d, "applied") switch
                    {
                        "on" => new RelayApplied(true),
                        "off" => new RelayApplied(false),
                        null => OptionalAmperes(d, "setpoint_a") is { } setpoint ? new SetpointApplied(setpoint) : null,
                        _ => throw new FormatException("inputs.device.applied"),
                    };
                    device = new DeviceDecisionInputs(
                        applied,
                        Bool(d, "state_fresh"),
                        OptionalEnum<DeviceStateInvalidReason>(d, "state_invalid_reason"),
                        OptionalInt(d, "shed_rank"),
                        Double(d, "value_per_kwh"),
                        OptionalDouble(d, "shed_priority"),
                        Seconds(d, "min_on_remaining_s"),
                        Seconds(d, "min_off_remaining_s"),
                        Bool(d, "can_switch_off"));
                    break;
                case "mode" when mode is null:
                    var m = property.Value;
                    mode = new ModeDecisionInputs(EnumValue<OperatingMode>(m, "ceiling"), EnumValue<OperatingMode>(m, "ui_choice"), EnumValue<ModeNote>(m, "note"));
                    break;
                case "kill_switch" when killSwitch is null:
                    killSwitch = Parse<KillSwitchSource>(property.Value.GetString());
                    break;
                case "safe_state_cause" when cause is null:
                    cause = Parse<SafeStateCause>(property.Value.GetString());
                    break;
                default:
                    throw new FormatException($"inputs.{property.Name}");
            }
        }

        return new DecisionInputs(phase, device, mode, killSwitch, cause);
    }

    private static string Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string FormatTime(DateTimeOffset time) => time.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.ParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static JsonElement Property(JsonElement element, string name, JsonValueKind kind)
    {
        var value = element.GetProperty(name);
        return value.ValueKind == kind ? value : throw new FormatException(name);
    }

    private static bool IsNull(JsonElement element, string name) => element.GetProperty(name).ValueKind == JsonValueKind.Null;

    private static string String(JsonElement element, string name) => Property(element, name, JsonValueKind.String).GetString()!;

    private static string? OptionalString(JsonElement element, string name) => IsNull(element, name) ? null : String(element, name);

    private static int Int(JsonElement element, string name) => Property(element, name, JsonValueKind.Number).GetInt32();

    private static int? OptionalInt(JsonElement element, string name) => IsNull(element, name) ? null : Int(element, name);

    private static double Double(JsonElement element, string name)
    {
        var value = Property(element, name, JsonValueKind.Number).GetDouble();
        return double.IsFinite(value) ? value : throw new FormatException(name);
    }

    private static double? OptionalDouble(JsonElement element, string name) => IsNull(element, name) ? null : Double(element, name);

    /// <summary>A non-negative duration a TimeSpan can hold; anything else (1e300, -1) is not a decision.</summary>
    private static TimeSpan Seconds(JsonElement element, string name)
    {
        var value = Double(element, name);
        return value >= 0 && value <= TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(value) : throw new FormatException(name);
    }

    private static Amperes? OptionalAmperes(JsonElement element, string name) => OptionalDouble(element, name) is { } value ? new Amperes(value) : null;

    private static bool Bool(JsonElement element, string name) => element.GetProperty(name).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new FormatException(name),
    };

    private static DateTimeOffset Time(JsonElement element, string name) => ParseTime(String(element, name));

    private static Phase? OptionalPhase(JsonElement element, string name) =>
        OptionalString(element, name) is { } text
            ? Enum.GetValues<Phase>().Single(p => p.ToString() == text)
            : null;

    private static ReasonCode Reason(string name) => ReasonsByName.TryGetValue(name, out var code) ? code : throw new FormatException($"reason {name}");

    private static TEnum EnumValue<TEnum>(JsonElement element, string name)
        where TEnum : struct, Enum => Parse<TEnum>(String(element, name));

    private static TEnum? OptionalEnum<TEnum>(JsonElement element, string name)
        where TEnum : struct, Enum => OptionalString(element, name) is { } text ? Parse<TEnum>(text) : null;

    private static TEnum Parse<TEnum>(string? text)
        where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().Where(v => Snake(v) == text).Select(v => (TEnum?)v).SingleOrDefault() ?? throw new FormatException(text);

    internal static string Snake<TEnum>(TEnum value)
        where TEnum : struct, Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
