using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Switchboard.Application;
using Switchboard.Application.Decisions;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Options;
using Switchboard.Application.Signals;
using Switchboard.Application.State;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Infrastructure.Mqtt;

/// <summary>
/// The guard's entities (BRK-04, SAFE-03, PKG-03), following the frozen identity scheme of
/// <see cref="MqttIdentity"/>: per phase in the topology the headroom to the reserve threshold (A
/// and W), the headroom to the continuous current, the overload budget, the remaining recovery
/// time and the guard state; the effective mode, the last decision (its reason code, or the action
/// for a decision that ends a condition), the kill switch and the control switch ("control
/// allowed", MODE-02); per declared device its state.
/// Headroom is computed from the measured current, never from the "as if applied" estimate: in
/// shadow mode nothing is applied, and an automation must not act on a hypothetical number. Watts are derived at this presentation edge from the configured voltage only.
/// <para>
/// While no valid configuration is known in this process, the phase and device entities announced
/// earlier (the ledger in state.json) are kept, with unknown state, and nothing is retracted.
/// </para>
/// </summary>
public sealed partial class GuardEntities : IMqttEntitySource
{
    public const string KillSwitchObjectId = "kill_switch";
    public const string ControlAllowedObjectId = "control_allowed";
    public const string ModeObjectId = "mode";
    public const string LastDecisionObjectId = "last_decision";
    public const string PayloadOn = "ON";
    public const string PayloadOff = "OFF";

    /// <summary>What Home Assistant's MQTT sensor reads as an unknown value.</summary>
    public const string Unknown = "None";

    private const string Sensor = "sensor";
    private const string Switch = "switch";

    private static readonly string[] PhaseStates = Snakes<PhaseGuardState>();
    private static readonly string[] DeviceStates = Snakes<DeviceGuardState>();
    private static readonly string[] ModeStates = ["shadow", "active"];
    private static readonly string[] ReasonNames = [.. ReasonCodes.All.Select(ReasonCodes.Name)];

    /// <summary>
    /// Decisions that end a condition keep the reason code of the condition that ended (DECISIONS,
    /// Stage 2): as a state they would read exactly like its start (a released kill switch would show
    /// kill_switch). The last-decision state names these actions instead, verbatim.
    /// </summary>
    private static readonly DecisionAction[] ConditionEnding = [DecisionAction.KillSwitchDisengaged, DecisionAction.SafeStateExited];

    /// <summary>The last-decision options: every reason code, then the condition-ending actions.</summary>
    public static IReadOnlyList<string> LastDecisionStates { get; } = [.. ReasonNames, .. ConditionEnding.Select(Snake)];

    private readonly IGuardStatusSource _status;
    private readonly IDecisionRecorder _decisions;
    private readonly IKillSwitchControl _killSwitch;
    private readonly IControlSwitch? _control;
    private readonly IDiscoveryLedger _ledger;
    private readonly Func<Volts?> _voltage;
    private readonly MqttOptions _mqtt;
    private readonly AppVersion _version;
    private readonly ILogger<GuardEntities> _logger;

    /// <param name="voltage">The configured nominal voltage (connection.voltage_v), for the headroom in W; null without a valid configuration.</param>
    public GuardEntities(
        IGuardStatusSource status,
        IDecisionRecorder decisions,
        IKillSwitchControl killSwitch,
        IDiscoveryLedger ledger,
        Func<Volts?> voltage,
        IOptions<MqttOptions> mqtt,
        AppVersion version,
        ILogger<GuardEntities> logger,
        IControlSwitch? control = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(mqtt);
        _status = status;
        _decisions = decisions;
        _killSwitch = killSwitch;
        _ledger = ledger;
        _voltage = voltage;
        _mqtt = mqtt.Value;
        _version = version;
        _logger = logger;
        _control = control;
        CommandTopics = control is null
            ? [CommandTopic(_mqtt.BaseTopic, KillSwitchObjectId)]
            : [CommandTopic(_mqtt.BaseTopic, KillSwitchObjectId), CommandTopic(_mqtt.BaseTopic, ControlAllowedObjectId)];
        _status.Published += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<string> CommandTopics { get; }

    /// <summary>The command topic of one of our entities: <c>&lt;BaseTopic&gt;/&lt;object_id&gt;/set</c>.</summary>
    public static string CommandTopic(string baseTopic, string objectId) => $"{baseTopic}/{objectId}/set";

    /// <summary>The attributes topic of one of our entities: <c>&lt;BaseTopic&gt;/&lt;object_id&gt;/attributes</c>.</summary>
    public static string AttributesTopic(string baseTopic, string objectId) => $"{baseTopic}/{objectId}/attributes";

    public static string PhaseObjectId(Phase phase, string suffix) => $"{phase.ToString().ToLowerInvariant()}_{suffix}";

    public static string DeviceObjectId(DeviceName device) => $"device_{device.Value}_state";

    public MqttEntitySet Current()
    {
        var status = _status.Current;
        var authoritative = status.Breaker is not null;
        var entities = ImmutableArray.CreateBuilder<MqttEntity>();
        entities.Add(Mode(status));
        entities.Add(LastDecision());
        entities.Add(KillSwitch(status));
        if (_control is not null)
        {
            entities.Add(ControlAllowed(status));
        }

        var phases = authoritative ? status.Phases.Select(p => p.Phase) : LedgerPhases();
        foreach (var phase in phases.Order())
        {
            entities.AddRange(PhaseEntities(phase, authoritative ? status.Phases.FirstOrDefault(p => p.Phase == phase) : null));
        }

        var devices = authoritative ? status.Devices.Select(d => d.Id) : LedgerDevices();
        foreach (var device in devices.Order())
        {
            entities.Add(DeviceEntity(device, authoritative ? status.Devices.FirstOrDefault(d => d.Id == device) : null));
        }

        return new MqttEntitySet(entities.ToImmutable(), authoritative);
    }

    public void HandleCommand(string topic, string payload)
    {
        if (topic == CommandTopics[0])
        {
            switch (payload)
            {
                case PayloadOn:
                    _killSwitch.Set(true, KillSwitchSource.Mqtt);
                    break;
                case PayloadOff when _killSwitch.Set(false, KillSwitchSource.Mqtt) == KillSwitchSetResult.RefusedLocked:
                    // Held until the state after a corrupt state.json is confirmed in the UI: Home
                    // Assistant's switch is set back to what is in force.
                    _logger.LogWarning("Ignored a kill switch release from Home Assistant: the state must be confirmed in the Switchboard UI first");
                    Changed?.Invoke(this, EventArgs.Empty);
                    break;
                case PayloadOff:
                    break;
                default:
                    _logger.LogWarning("Ignored a kill switch command that is neither {On} nor {Off}", PayloadOn, PayloadOff);
                    break;
            }

            return;
        }

        if (_control is null || CommandTopics.Count < 2 || topic != CommandTopics[1])
        {
            return;
        }

        switch (payload)
        {
            case PayloadOn when _control.Set(true, ControlSwitchSource.Mqtt) == ControlSetResult.RefusedLocked:
                _logger.LogWarning("Ignored control allowed from Home Assistant: the state after a corrupt state.json must be confirmed in the Switchboard UI first");
                Changed?.Invoke(this, EventArgs.Empty);
                break;
            case PayloadOn:
                break;
            case PayloadOff:
                _control.Set(false, ControlSwitchSource.Mqtt);
                break;
            default:
                _logger.LogWarning("Ignored a control command that is neither {On} nor {Off}", PayloadOn, PayloadOff);
                Changed?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private MqttEntity Mode(GuardStatus status) => new(
        Sensor,
        ModeObjectId,
        Config(Sensor, ModeObjectId, "Mode", new() { ["device_class"] = "enum", ["options"] = ModeStates, ["json_attributes_topic"] = AttributesTopic(_mqtt.BaseTopic, ModeObjectId) }),
        Snake(status.Mode.Effective),
        Json(new Dictionary<string, object?>
        {
            ["ceiling"] = Snake(status.Mode.Ceiling),
            ["note"] = Snake(status.Mode.Note),
            ["control_allowed"] = status.Mode.ControlAllowed,
            ["missing"] = status.Mode.Missing.Select(Snake).ToArray(),
        }));

    private MqttEntity LastDecision()
    {
        var last = _decisions.Query(new DecisionQuery(null, null, null, 1, null)).Items.FirstOrDefault();
        return new MqttEntity(
            Sensor,
            LastDecisionObjectId,
            Config(Sensor, LastDecisionObjectId, "Last decision", new() { ["device_class"] = "enum", ["options"] = LastDecisionStates, ["json_attributes_topic"] = AttributesTopic(_mqtt.BaseTopic, LastDecisionObjectId) }),
            last is null ? Unknown : ConditionEnding.Contains(last.Action) ? Snake(last.Action) : ReasonCodes.Name(last.Reason),
            Json(new Dictionary<string, object?>
            {
                ["action"] = last is null ? null : Snake(last.Action),
                ["reason"] = last is null ? null : ReasonCodes.Name(last.Reason),
                ["device"] = last?.Device?.Value,
                ["stage"] = last?.Stage,
                ["phase"] = last?.Phase?.ToString(),
                ["at"] = last?.At.ToString("O", CultureInfo.InvariantCulture),
                ["commanded"] = last?.Commanded,
            }));
    }

    /// <summary>
    /// The kill switch. Its attributes say whether it switches anything: while the effective mode is
    /// not active its holds are recorded only, and a user reaching for it in an emergency must not
    /// believe the loads were switched off.
    /// </summary>
    private MqttEntity KillSwitch(GuardStatus status) => new(
        Switch,
        KillSwitchObjectId,
        Config(Switch, KillSwitchObjectId, "Kill switch", new()
        {
            ["command_topic"] = CommandTopics[0],
            ["payload_on"] = PayloadOn,
            ["payload_off"] = PayloadOff,
            ["optimistic"] = false,
            ["retain"] = false,
            ["json_attributes_topic"] = AttributesTopic(_mqtt.BaseTopic, KillSwitchObjectId),
        }),
        status.KillSwitch.Engaged ? PayloadOn : PayloadOff,
        Json(new Dictionary<string, object?>
        {
            ["switches_devices"] = status.Mode.Commands,
            ["note"] = status.Mode.Commands ? null : "nothing is switched: control is not active",
            ["locked"] = status.KillSwitch.Locked,
        }));

    /// <summary>The global "control allowed" switch (MODE-02): commands also need the add-on option active and a valid configuration.</summary>
    private MqttEntity ControlAllowed(GuardStatus status)
    {
        var control = _control!.Status;
        return new MqttEntity(
            Switch,
            ControlAllowedObjectId,
            Config(Switch, ControlAllowedObjectId, "Control allowed", new()
            {
                ["command_topic"] = CommandTopics[1],
                ["payload_on"] = PayloadOn,
                ["payload_off"] = PayloadOff,
                ["optimistic"] = false,
                ["retain"] = false,
                ["json_attributes_topic"] = AttributesTopic(_mqtt.BaseTopic, ControlAllowedObjectId),
            }),
            control.Allowed ? PayloadOn : PayloadOff,
            Json(new Dictionary<string, object?>
            {
                ["effective"] = Snake(status.Mode.Effective),
                ["missing"] = status.Mode.Missing.Select(Snake).ToArray(),
                ["locked"] = control.Locked,
            }));
    }

    private IEnumerable<MqttEntity> PhaseEntities(Phase phase, PhaseGuardStatus? status)
    {
        var name = phase.ToString();
        var voltage = _voltage();
        yield return Measurement(phase, "headroom", $"{name} headroom", "A", "current", Amps(status?.Headroom));
        yield return Measurement(
            phase,
            "headroom_power",
            $"{name} headroom power",
            "W",
            "power",
            status?.Headroom is { } headroom && voltage is { } volts ? Number(SignalUnits.PowerAt(headroom, volts).Value, "0") : Unknown);
        yield return Measurement(phase, "breaker_headroom", $"{name} breaker headroom", "A", "current", Amps(status?.BreakerHeadroom));
        yield return Measurement(phase, "overload_budget", $"{name} overload budget", "%", null, status is null ? Unknown : Number(status.Budget * 100, "0.0"));
        yield return Measurement(phase, "recovery_remaining", $"{name} recovery remaining", "s", "duration", status is null ? Unknown : Number(Math.Round(status.RecoveryRemaining.TotalSeconds), "0"));

        var stateId = PhaseObjectId(phase, "guard_state");
        yield return new MqttEntity(
            Sensor,
            stateId,
            Config(Sensor, stateId, $"{name} guard state", new() { ["device_class"] = "enum", ["options"] = PhaseStates }),
            status is null ? Unknown : Snake(status.State),
            null);
    }

    private MqttEntity Measurement(Phase phase, string suffix, string name, string unit, string? deviceClass, string state)
    {
        var objectId = PhaseObjectId(phase, suffix);
        var extra = new Dictionary<string, object?> { ["unit_of_measurement"] = unit, ["state_class"] = "measurement" };
        if (deviceClass is not null)
        {
            extra["device_class"] = deviceClass;
        }

        return new MqttEntity(Sensor, objectId, Config(Sensor, objectId, name, extra), state, null);
    }

    private MqttEntity DeviceEntity(DeviceName device, DeviceGuardStatus? status)
    {
        var objectId = DeviceObjectId(device);
        return new MqttEntity(
            Sensor,
            objectId,
            Config(Sensor, objectId, $"{device.Value} state", new() { ["device_class"] = "enum", ["options"] = DeviceStates, ["json_attributes_topic"] = AttributesTopic(_mqtt.BaseTopic, objectId) }),
            status is null ? Unknown : Snake(status.GuardState),
            Json(new Dictionary<string, object?>
            {
                ["reason"] = status?.Reason is { } reason ? ReasonCodes.Name(reason) : null,
                ["can_switch_off"] = status?.CanSwitchOff,
                ["phases"] = status?.Phases.Select(p => p.ToString()).ToArray(),
            }));
    }

    private string Config(string component, string objectId, string name, Dictionary<string, object?> extra)
    {
        var config = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["has_entity_name"] = true,
            ["unique_id"] = MqttIdentity.UniqueId(objectId),
            ["default_entity_id"] = MqttIdentity.DefaultEntityId(component, objectId),
            ["state_topic"] = MqttIdentity.StateTopic(_mqtt.BaseTopic, objectId),
            ["availability_topic"] = MqttIdentity.AvailabilityTopic(_mqtt.BaseTopic),
            ["payload_available"] = MqttIdentity.AvailabilityOnline,
            ["payload_not_available"] = MqttIdentity.AvailabilityOffline,
            ["device"] = new Dictionary<string, object>
            {
                ["identifiers"] = new[] { MqttIdentity.DeviceIdentifier },
                ["name"] = MqttIdentity.DeviceName,
                ["model"] = MqttIdentity.DeviceModel,
                ["sw_version"] = _version.Value,
            },
        };
        foreach (var (key, value) in extra)
        {
            config[key] = value;
        }

        return Json(config);
    }

    /// <summary>Phases announced earlier, from the ledger: kept while no valid configuration says otherwise.</summary>
    private IEnumerable<Phase> LedgerPhases() =>
        _ledger.Discovered
            .Select(key => PhaseKey().Match(key))
            .Where(m => m.Success)
            .Select(m => Enum.Parse<Phase>(m.Groups[1].Value.ToUpperInvariant()))
            .Distinct();

    private IEnumerable<DeviceName> LedgerDevices() =>
        _ledger.Discovered
            .Select(key => DeviceKey().Match(key))
            .Where(m => m.Success)
            .Select(m => DeviceName.TryParse(m.Groups[1].Value, out var device) ? device : null)
            .OfType<DeviceName>()
            .Distinct();

    private static string Amps(Amperes? value) => value is { } amps ? Number(amps.Value, "0.00") : Unknown;

    private static string Number(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

    private static string Json(Dictionary<string, object?> value) => JsonSerializer.Serialize(value);

    private static string Snake<TEnum>(TEnum value)
        where TEnum : struct, Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private static string[] Snakes<TEnum>()
        where TEnum : struct, Enum => [.. Enum.GetValues<TEnum>().Select(Snake)];

    [GeneratedRegex("^sensor/(l[1-3])_[a-z_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PhaseKey();

    [GeneratedRegex("^sensor/device_([a-z][a-z0-9_]*)_state$", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceKey();
}
