using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Switchboard.Application;
using Switchboard.Application.Ev;
using Switchboard.Application.Options;
using Switchboard.Domain.Ev;
using Switchboard.Domain.Goals;

namespace Switchboard.Infrastructure.Mqtt;

/// <summary>
/// The EV module's manual controls as MQTT entities (EV-09), only while the valid configuration holds an <c>ev:</c> section: <c>select.switchboard_ev_mode</c>
/// (auto, pv_only, full_charge), <c>number.switchboard_ev_charge_now_target</c> (the SoC the next charge-now runs to, percent) and
/// <c>button.switchboard_ev_charge_now</c>. A command is exactly one of the select's options, a number in (0, 100] or the button's <c>PRESS</c>; anything else is
/// ignored. They change only the add-on's own state through <see cref="IEvControl"/> (the same door as the web API): "Off" is not offered, it is the device's own
/// override select. While no valid configuration is known the entities announced earlier are kept (nothing is retracted), and without an <c>ev:</c> section they
/// are retracted like any entity no longer declared.
/// </summary>
public sealed class EvEntities : IMqttEntitySource
{
    public const string ModeObjectId = "ev_mode";
    public const string TargetObjectId = "ev_charge_now_target";
    public const string ChargeNowObjectId = "ev_charge_now";

    private const string Select = "select";
    private const string Number = "number";
    private const string Button = "button";
    private const string PressPayload = "PRESS";

    private static readonly string[] ModeOptions = [.. Enum.GetValues<EvMode>().Select(m => JsonNamingPolicy.SnakeCaseLower.ConvertName(m.ToString()))];

    private readonly IEvControl _control;
    private readonly MqttOptions _mqtt;
    private readonly AppVersion _version;
    private readonly ILogger<EvEntities> _logger;
    private double? _target;

    public EvEntities(IEvControl control, IOptions<MqttOptions> mqtt, AppVersion version, ILogger<EvEntities> logger)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(mqtt);
        _control = control;
        _mqtt = mqtt.Value;
        _version = version;
        _logger = logger;
        _control.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    /// <summary>The three command topics while an <c>ev:</c> section is in force, none otherwise; exactly what the topic guard allows.</summary>
    public IReadOnlyList<string> CommandTopics => _control.Configured == true ? Topics(_mqtt.BaseTopic) : [];

    /// <summary>The command topic of each of the three entities: the one place the topic guard and this source agree on.</summary>
    public static IReadOnlyList<string> Topics(string baseTopic) =>
        [GuardEntities.CommandTopic(baseTopic, ModeObjectId), GuardEntities.CommandTopic(baseTopic, TargetObjectId), GuardEntities.CommandTopic(baseTopic, ChargeNowObjectId)];

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
                    Button,
                    ChargeNowObjectId,
                    Config(Button, ChargeNowObjectId, "EV charge now", state: false, new()
                    {
                        ["command_topic"] = GuardEntities.CommandTopic(_mqtt.BaseTopic, ChargeNowObjectId),
                        ["payload_press"] = PressPayload,
                        ["retain"] = false,
                    }),
                    GuardEntities.Unknown,
                    null)),
            true);
    }

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
            if (payload != PressPayload)
            {
                _logger.LogWarning("Ignored an EV charge-now command that is not {Payload}", PressPayload);
                return;
            }

            var result = _control.StartChargeNow(Target(), null);
            if (result is not (EvControlResult.Done or EvControlResult.Unchanged))
            {
                _logger.LogWarning("EV charge now was refused: {Result}", result);
            }
        }
    }

    // What the number shows and the button uses: the last value set, else the configured default.
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
