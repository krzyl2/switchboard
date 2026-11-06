using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Switchboard.Application;
using Switchboard.Application.Ev;
using Switchboard.Application.Modes;
using Switchboard.Application.Options;
using Switchboard.Domain.Ev;
using Switchboard.Domain.Goals;

namespace Switchboard.Infrastructure.Mqtt;

/// <summary>
/// The EV module's manual controls as MQTT entities (EV-09), only while the valid configuration holds an <c>ev:</c> section: <c>select.switchboard_ev_mode</c>
/// (auto, pv_only, full_charge), <c>number.switchboard_ev_charge_now_target</c> (the SoC the next charge-now runs to, percent) and
/// <c>select.switchboard_ev_command</c> (2026-11-06, replaces the charge-now button): <c>charge_now</c> charges now from any zone within the protection limits,
/// <c>hold</c> takes the grid away (PV surplus still charges), <c>auto</c> ends either; each command also ends by itself (target, unplug, its duration), and
/// the select then shows <c>auto</c> again. A command is exactly one of a select's options or a number in (0, 100]; anything else is ignored. They change only
/// the add-on's own state through <see cref="IEvControl"/> (the same door as the web API): "Off" is the device's own override select. While no valid
/// configuration is known the entities announced earlier are kept (nothing is retracted), and without an <c>ev:</c> section they are retracted like any entity
/// no longer declared.
/// Unless the add-on option <c>mqtt_enabling_commands</c> is on, MQTT may only do what takes the grid away: the mode <c>pv_only</c>, the number, the command
/// <c>hold</c>, and <c>auto</c> while it only ends a charge-now. The modes <c>auto</c> and <c>full_charge</c>, <c>charge_now</c>, and <c>auto</c> ending a hold
/// can charge from the grid, so each is refused, logged, and the select is set back to what is in force.
/// </summary>
public sealed class EvEntities : IMqttEntitySource
{
    public const string ModeObjectId = "ev_mode";
    public const string TargetObjectId = "ev_charge_now_target";
    public const string CommandObjectId = "ev_command";

    public const string CommandAuto = "auto";
    public const string CommandChargeNow = "charge_now";
    public const string CommandHold = "hold";

    private const string Select = "select";
    private const string Number = "number";

    private static readonly string[] ModeOptions = [.. Enum.GetValues<EvMode>().Select(m => JsonNamingPolicy.SnakeCaseLower.ConvertName(m.ToString()))];
    private static readonly string[] CommandOptions = [CommandAuto, CommandChargeNow, CommandHold];

    private readonly IEvControl _control;
    private readonly MqttOptions _mqtt;
    private readonly AppVersion _version;
    private readonly ILogger<EvEntities> _logger;
    private readonly bool _enablingCommands;
    private double? _target;

    /// <param name="enablingCommands">The add-on option <c>mqtt_enabling_commands</c>: whether a mode other than <c>pv_only</c>, <c>charge_now</c> and the end of a hold are taken from MQTT.</param>
    public EvEntities(IEvControl control, IOptions<MqttOptions> mqtt, AppVersion version, ILogger<EvEntities> logger, bool enablingCommands = false)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(mqtt);
        _control = control;
        _mqtt = mqtt.Value;
        _version = version;
        _logger = logger;
        _enablingCommands = enablingCommands;
        _control.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    /// <summary>The three command topics while an <c>ev:</c> section is in force, none otherwise; exactly what the topic guard allows.</summary>
    public IReadOnlyList<string> CommandTopics => _control.Configured == true ? Topics(_mqtt.BaseTopic) : [];

    /// <summary>The command topic of each of the three entities: the one place the topic guard and this source agree on.</summary>
    public static IReadOnlyList<string> Topics(string baseTopic) =>
        [GuardEntities.CommandTopic(baseTopic, ModeObjectId), GuardEntities.CommandTopic(baseTopic, TargetObjectId), GuardEntities.CommandTopic(baseTopic, CommandObjectId)];

    public MqttEntitySet Current()
    {
        switch (_control.Configured)
        {
            case null:
                return new MqttEntitySet([], false);
            case false:
                return new MqttEntitySet([], true);
        }

        var target = Target();
        return new MqttEntitySet(
            ImmutableArray.Create(
                new MqttEntity(
                    Select,
                    ModeObjectId,
                    Config(Select, ModeObjectId, "EV mode", state: true, new()
                    {
                        ["command_topic"] = GuardEntities.CommandTopic(_mqtt.BaseTopic, ModeObjectId),
                        ["options"] = ModeOptions,
                        ["optimistic"] = false,
                        ["retain"] = false,
                    }),
                    JsonNamingPolicy.SnakeCaseLower.ConvertName(_control.Manual.Mode.ToString()),
                    null),
                new MqttEntity(
                    Number,
                    TargetObjectId,
                    Config(Number, TargetObjectId, "EV charge now target", state: true, new()
                    {
                        ["command_topic"] = GuardEntities.CommandTopic(_mqtt.BaseTopic, TargetObjectId),
                        ["min"] = 1,
                        ["max"] = 100,
                        ["step"] = 1,
                        ["unit_of_measurement"] = "%",
                        ["mode"] = "box",
                        ["optimistic"] = false,
                        ["retain"] = false,
                    }),
                    target is { } percent ? percent.ToString("0.##", CultureInfo.InvariantCulture) : GuardEntities.Unknown,
                    null),
                new MqttEntity(
                    Select,
                    CommandObjectId,
                    Config(Select, CommandObjectId, "EV command", state: true, new()
                    {
                        ["command_topic"] = GuardEntities.CommandTopic(_mqtt.BaseTopic, CommandObjectId),
                        ["options"] = CommandOptions,
                        ["optimistic"] = false,
                        ["retain"] = false,
                    }),
                    Command(_control.Manual),
                    null)),
            true);
    }

    /// <summary>What the command select shows: the command in force, <c>auto</c> when none runs.</summary>
    internal static string Command(EvManual manual) => manual.ChargeNow is not null ? CommandChargeNow : manual.Hold is not null ? CommandHold : CommandAuto;

    public void HandleCommand(string topic, string payload)
    {
        if (_control.Configured != true)
        {
            return;
        }

        var topics = Topics(_mqtt.BaseTopic);
        if (string.Equals(topic, topics[0], StringComparison.Ordinal))
        {
            if (Enum.GetValues<EvMode>().Where(m => JsonNamingPolicy.SnakeCaseLower.ConvertName(m.ToString()) == payload).Select(m => (EvMode?)m).SingleOrDefault() is not { } mode)
            {
                _logger.LogWarning("Ignored an EV mode command that is not one of {Options}", string.Join(", ", ModeOptions));
                return;
            }

            if (mode != EvMode.PvOnly && !_enablingCommands)
            {
                RefuseEnabling($"the EV mode {payload}");
                return;
            }

            _control.SetMode(mode);
        }
        else if (string.Equals(topic, topics[1], StringComparison.Ordinal))
        {
            if (!double.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) || !ManualGoal.TargetInRange(GoalKind.Soc, percent))
            {
                _logger.LogWarning("Ignored an EV charge-now target that is not above 0 and at most 100");
                return;
            }

            _target = percent;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        else if (string.Equals(topic, topics[2], StringComparison.Ordinal))
        {
            Command(payload);
        }
    }

    private void Command(string payload)
    {
        var manual = _control.Manual;
        EvControlResult result;
        switch (payload)
        {
            case CommandChargeNow:
                if (!_enablingCommands)
                {
                    RefuseEnabling("EV charge now");
                    return;
                }

                result = _control.StartChargeNow(Target(), null);
                break;
            case CommandHold:
                result = _control.StartHold(null);
                break;
            case CommandAuto:
                if (manual.Hold is not null && !_enablingCommands)
                {
                    RefuseEnabling("the end of an EV hold");
                    return;
                }

                result = manual.ChargeNow is not null ? _control.CancelChargeNow() : manual.Hold is not null ? _control.CancelHold() : EvControlResult.Unchanged;
                break;
            default:
                _logger.LogWarning("Ignored an EV command that is not one of {Options}", string.Join(", ", CommandOptions));
                return;
        }

        if (result is not (EvControlResult.Done or EvControlResult.Unchanged))
        {
            _logger.LogWarning("EV command {Command} was refused: {Result}", payload, result);

            // The select is not optimistic: publish again so Home Assistant shows what is in force, not the refused option.
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // Nothing changes; the link publishes the states again, so Home Assistant's select shows the mode in force.
    private void RefuseEnabling(string command)
    {
        _logger.LogWarning("Ignored {Command} from MQTT: the add-on option {Option} is off, so of the EV controls MQTT can only take the grid away (mode pv_only, command hold, the end of a charge-now) and set the charge-now target", command, AddonOptionsResolver.MqttEnablingCommandsKey);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // What the number shows and charge_now uses: the last value set, else the configured default.
    private double? Target() => _target ?? _control.DefaultChargeNowTargetPercent;

    private string Config(string component, string objectId, string name, bool state, Dictionary<string, object?> extra)
    {
        var config = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["has_entity_name"] = true,
            ["unique_id"] = MqttIdentity.UniqueId(objectId),
            ["default_entity_id"] = MqttIdentity.DefaultEntityId(component, objectId),
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
        if (state)
        {
            config["state_topic"] = MqttIdentity.StateTopic(_mqtt.BaseTopic, objectId);
        }

        foreach (var (key, value) in extra)
        {
            config[key] = value;
        }

        return JsonSerializer.Serialize(config);
    }
}
