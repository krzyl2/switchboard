using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Infrastructure.Persistence;

/// <summary>
/// One decision as one line of the daily decision log (MODE-03): snake_case keys and values, the
/// reason code as 04 §6 writes it, the list version (<c>codes</c>) and the inputs that produced it.
/// Reading is the reverse and treats the file as untrusted: a line that is not exactly a decision
/// of a known shape (a partial line after a crash, a gap marker, an edit) is not a decision.
/// <para>
/// Format 2 (Stage 3) adds <c>origin</c>, <c>group</c> and <c>target</c> on every line and the
/// dispatch inputs; lines of format 1 (0.3.x, kept for the retention period) still read, without them.
/// Reason-code list version 3 (0.7.0) only appended codes, so lines of version 2 still read; a
/// version 2 line carrying a version 3 code is not a decision. 0.7.0 adds, inside the same format,
/// the control switch to the mode inputs and <c>reasserted</c> to the dispatch inputs (both optional
/// on read). The dispatch inputs may carry <c>balance</c> (the hourly balance of the step, BAL-07; absent
/// on older lines and while no energy counters are mapped).
/// </para>
/// </summary>
public static class DecisionJson
{
    public const int FormatVersion = 2;

    /// <summary>The format 0.3.x wrote: no origin, group, target or dispatch inputs.</summary>
    public const int LegacyFormatVersion = 1;

    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    // A bound on untrusted content, far above any stepped device a configuration declares.
    private const int MaxStages = 64;

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
            WriteOptional(writer, "origin", decision.Origin is { } origin ? Snake(origin) : null);
            WriteOptional(writer, "group", decision.Group?.Value);
            WriteLevel(writer, "target", decision.Target);
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
                || Int(root, "v") is not (FormatVersion or LegacyFormatVersion)
                || Int(root, "codes") is not (ReasonCodes.ListVersion or ReasonCodes.PreviousListVersion or ReasonCodes.OldestListVersion))
            {
                return null;
            }

            var codes = Int(root, "codes");
            var current = Int(root, "v") == FormatVersion;
            var device = OptionalString(root, "device") is { } name ? DeviceName.Parse(name) : (DeviceName?)null;
            ReasonCode Code(string text) => Reason(text) is var code && ReasonCodes.InVersion(code, codes) ? code : throw new FormatException($"reason {text} in list version {codes}");
            return new Decision(
                String(root, "id"),
                Time(root, "at"),
                EnumValue<DecisionScope>(root, "scope"),
                device,
                OptionalInt(root, "stage"),
                OptionalPhase(root, "phase"),
                EnumValue<DecisionAction>(root, "action"),
                Code(String(root, "reason")),
                OptionalString(root, "overridden") is { } overridden ? Code(overridden) : null,
                EnumValue<OperatingMode>(root, "mode"),
                Bool(root, "commanded"),
                OptionalString(root, "refused_by") is { } refused ? Code(refused) : null,
                Inputs(Property(root, "inputs", JsonValueKind.Object), current),
                current ? OptionalEnum<DecisionOrigin>(root, "origin") : null,
                current && OptionalString(root, "group") is { } group ? GroupName.Parse(group) : null,
                current ? Level(root, "target") : null);
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
            writer.WriteString("note", Snake(mode.Note));
            if (mode.ControlAllowed is { } allowed)
            {
                writer.WriteBoolean("control_allowed", allowed);
                writer.WriteBoolean("configuration_valid", mode.ConfigurationValid ?? true);
                writer.WriteBoolean("state_confirmed", mode.StateConfirmed ?? true);
                writer.WriteStartArray("missing");
                foreach (var condition in mode.Missing.IsDefault ? [] : mode.Missing)
                {
                    writer.WriteStringValue(Snake(condition));
                }

                writer.WriteEndArray();
                WriteOptional(writer, "source", mode.Source is { } changedBy ? Snake(changedBy) : null);
            }

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

        if (inputs.Dispatch is { } dispatch)
        {
            WriteDispatch(writer, dispatch);
        }
    }

    private static void WriteDispatch(Utf8JsonWriter writer, DispatchDecisionInputs dispatch)
    {
        writer.WriteStartObject("dispatch");
        writer.WriteStartArray("headroom");
        foreach (var headroom in dispatch.Headroom.IsDefault ? [] : dispatch.Headroom)
        {
            writer.WriteStartObject();
            WriteKey(writer, headroom.Key);
            WriteOptional(writer, "envelope_a", headroom.Envelope?.Value);
            WriteOptional(writer, "remaining_a", headroom.Remaining?.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteNumber("start_hysteresis_a", dispatch.StartHysteresis.Value);
        writer.WriteNumber("stop_hysteresis_a", dispatch.StopHysteresis.Value);
        WriteOptional(writer, "start_rank", dispatch.StartRank);
        writer.WriteStartObject("contribution_a");
        foreach (var (phase, current) in dispatch.Contribution.OrderBy(c => c.Key))
        {
            writer.WriteNumber(phase.ToString(), current.Value);
        }

        writer.WriteEndObject();
        writer.WriteStartArray("in_flight_a");
        foreach (var (key, current) in dispatch.InFlight.OrderBy(c => c.Key.Group?.Value, StringComparer.Ordinal).ThenBy(c => c.Key.Phase))
        {
            writer.WriteStartObject();
            WriteKey(writer, key);
            writer.WriteNumber("current_a", current.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        WriteOptional(writer, "settling", dispatch.Settling is { } settling ? Snake(settling) : null);
        if (dispatch.Verification is { } verification)
        {
            writer.WriteStartObject("verification");
            writer.WriteNumber("expected", verification.Expected);
            writer.WriteNumber("measured", verification.Measured);
            writer.WriteString("unit", Snake(verification.Unit));
            writer.WriteString("verdict", Snake(verification.Verdict));
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNull("verification");
        }

        writer.WriteNumber("min_on_remaining_s", dispatch.MinOnRemaining.TotalSeconds);
        writer.WriteNumber("min_off_remaining_s", dispatch.MinOffRemaining.TotalSeconds);
        WriteOptional(writer, "deferred_since", dispatch.DeferredSince is { } since ? FormatTime(since) : null);
        writer.WriteString("override", Snake(dispatch.Override));
        if (dispatch.Reasserted)
        {
            writer.WriteBoolean("reasserted", true);
        }

        if (dispatch.Balance is { } balance)
        {
            writer.WriteStartObject("balance");
            WriteOptional(writer, "balance_wh", balance.BalanceWh);
            WriteOptional(writer, "free_w", balance.FreeW);
            WriteOptional(writer, "load_w", balance.LoadW);
            WriteOptional(writer, "projection_wh", balance.ProjectionWh);
            WriteOptional(writer, "lo_wh", balance.LoWh);
            WriteOptional(writer, "hi_wh", balance.HiWh);
            writer.WriteNumber("t_rem_s", balance.RemainingS);
            writer.WriteBoolean("burning", balance.Burning);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteKey(Utf8JsonWriter writer, ConstraintKey key)
    {
        WriteOptional(writer, "group", key.Group?.Value);
        writer.WriteString("phase", key.Phase.ToString());
    }

    /// <summary>A device level: relay flags (null for a setpoint device), the setpoint in A and the stop actuator's enabled flag.</summary>
    private static void WriteLevel(Utf8JsonWriter writer, string name, DeviceLevel? level)
    {
        if (level is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteStartObject(name);
        if (level.Setpoint is null)
        {
            writer.WriteStartArray("relays");
            foreach (var relay in level.Relays.IsDefault ? [] : level.Relays)
            {
                writer.WriteBooleanValue(relay);
            }

            writer.WriteEndArray();
        }
        else
        {
            writer.WriteNull("relays");
        }

        WriteOptional(writer, "setpoint_a", level.Setpoint?.Value);
        writer.WriteBoolean("enabled", level.Enabled);
        writer.WriteEndObject();
    }

    private static DeviceLevel? Level(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 3)
        {
            throw new FormatException(name);
        }

        var setpoint = OptionalAmperes(value, "setpoint_a");
        var enabled = Bool(value, "enabled");
        if (setpoint is { } amps)
        {
            return IsNull(value, "relays") ? DeviceLevel.At(amps, enabled) : throw new FormatException($"{name}.relays");
        }

        var relays = Property(value, "relays", JsonValueKind.Array);
        if (relays.GetArrayLength() is 0 or > MaxStages || !enabled)
        {
            throw new FormatException($"{name}.relays");
        }

        return DeviceLevel.Relay([.. relays.EnumerateArray().Select(r => r.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new FormatException($"{name}.relays"),
        })]);
    }

    private static DispatchDecisionInputs Dispatch(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("inputs.dispatch");
        }

        var headroom = Property(d, "headroom", JsonValueKind.Array).EnumerateArray()
            .Select(h => new ConstraintHeadroom(Key(h), OptionalAmperes(h, "envelope_a"), OptionalAmperes(h, "remaining_a")))
            .ToImmutableArray();
        var contribution = ImmutableDictionary.CreateBuilder<Phase, Amperes>();
        foreach (var property in Property(d, "contribution_a", JsonValueKind.Object).EnumerateObject())
        {
            var phase = Enum.GetValues<Phase>().Single(p => p.ToString() == property.Name);
            contribution.Add(phase, new Amperes(Finite(property.Value, "inputs.dispatch.contribution_a")));
        }

        var inFlight = ImmutableDictionary.CreateBuilder<ConstraintKey, Amperes>();
        foreach (var entry in Property(d, "in_flight_a", JsonValueKind.Array).EnumerateArray())
        {
            inFlight.Add(Key(entry), new Amperes(Double(entry, "current_a")));
        }

        VerificationInputs? verification = null;
        if (!IsNull(d, "verification"))
        {
            var v = Property(d, "verification", JsonValueKind.Object);
            verification = new VerificationInputs(Double(v, "expected"), Double(v, "measured"), EnumValue<MeasureUnit>(v, "unit"), EnumValue<Verdict>(v, "verdict"));
        }

        return new DispatchDecisionInputs(
            headroom,
            new Amperes(Double(d, "start_hysteresis_a")),
            new Amperes(Double(d, "stop_hysteresis_a")),
            OptionalInt(d, "start_rank"),
            contribution.ToImmutable(),
            inFlight.ToImmutable(),
            OptionalEnum<SettlingState>(d, "settling"),
            verification,
            Seconds(d, "min_on_remaining_s"),
            Seconds(d, "min_off_remaining_s"),
            OptionalString(d, "deferred_since") is { } since ? ParseTime(since) : null,
            EnumValue<OverrideMode>(d, "override"))
        {
            Reasserted = d.TryGetProperty("reasserted", out var reasserted) && reasserted.ValueKind == JsonValueKind.True,
            Balance = d.TryGetProperty("balance", out var balance) && balance.ValueKind == JsonValueKind.Object ? Balance(balance) : null,
        };
    }

    private static BalanceDecisionInputs Balance(JsonElement b) => new(
        OptionalDouble(b, "balance_wh"),
        OptionalDouble(b, "free_w"),
        OptionalDouble(b, "load_w"),
        OptionalDouble(b, "projection_wh"),
        OptionalDouble(b, "lo_wh"),
        OptionalDouble(b, "hi_wh"),
        Double(b, "t_rem_s"),
        b.TryGetProperty("burning", out var burning) && burning.ValueKind == JsonValueKind.True);

    private static ConstraintKey Key(JsonElement element) =>
        new(
            OptionalString(element, "group") is { } group ? GroupName.Parse(group) : null,
            OptionalPhase(element, "phase") ?? throw new FormatException("phase"));

    private static double Finite(JsonElement element, string path)
    {
        var value = element.ValueKind == JsonValueKind.Number ? element.GetDouble() : throw new FormatException(path);
        return double.IsFinite(value) ? value : throw new FormatException(path);
    }

    private static DecisionInputs Inputs(JsonElement element, bool current)
    {
        DispatchDecisionInputs? dispatch = null;
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
                    // ui_choice (0.3.x … 0.7.0, the Stage 5 UI choice) is accepted on old lines and ignored.
                    mode = new ModeDecisionInputs(EnumValue<OperatingMode>(m, "ceiling"), EnumValue<ModeNote>(m, "note"));
                    if (m.TryGetProperty("control_allowed", out _))
                    {
                        mode = mode with
                        {
                            ControlAllowed = Bool(m, "control_allowed"),
                            ConfigurationValid = Bool(m, "configuration_valid"),
                            StateConfirmed = Bool(m, "state_confirmed"),
                            Missing = [.. Property(m, "missing", JsonValueKind.Array).EnumerateArray().Select(c => Parse<ModeCondition>(c.GetString()))],
                            Source = OptionalEnum<ControlSwitchSource>(m, "source"),
                        };
                    }

                    break;
                case "kill_switch" when killSwitch is null:
                    killSwitch = Parse<KillSwitchSource>(property.Value.GetString());
                    break;
                case "safe_state_cause" when cause is null:
                    cause = Parse<SafeStateCause>(property.Value.GetString());
                    break;
                case "dispatch" when dispatch is null && current:
                    dispatch = Dispatch(property.Value);
                    break;
                default:
                    throw new FormatException($"inputs.{property.Name}");
            }
        }

        return new DecisionInputs(phase, device, mode, killSwitch, cause, dispatch);
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
