using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Switchboard.Application;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Options;
using Switchboard.Application.State;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;

namespace Switchboard.Infrastructure.Mqtt;

/// <summary>
/// Dispatch's entities, following the frozen identity scheme of <see cref="MqttIdentity"/>: per
/// declared device its dispatch state (<c>sensor.switchboard_device_&lt;id&gt;_dispatch</c>: the
/// intended state, with the reported state, the reason, a deferral, the settling window, the
/// unresponsive flag and the override as attributes, and <c>commanded: false</c> because this
/// version switches nothing) and its manual override (<c>select.switchboard_device_&lt;id&gt;_override</c>:
/// auto, force_on, force_off; SAFE-04). An override command must be exactly one of the options on
/// the device's own command topic, and only a device of the current valid configuration is
/// addressed (SAFE-05); protection outranks it either way.
/// <para>
/// While no valid configuration is known in this process, the entities announced earlier (the
/// ledger in state.json) are kept with unknown state, nothing is retracted and no override command
/// is listened to.
/// </para>
/// </summary>
public sealed partial class DispatchEntities : IMqttEntitySource
{
    private const string Sensor = "sensor";
    private const string Select = "select";

    private static readonly string[] DispatchStates = ["on", "off", "partial"];
    private static readonly string[] OverrideOptions = [.. Enum.GetValues<OverrideMode>().Select(Snake)];

    private readonly IDispatchStatusSource _status;
    private readonly IDeviceOverrideControl _overrides;
    private readonly IDiscoveryLedger _ledger;
    private readonly Func<IReadOnlyCollection<DeviceName>?> _declaredDevices;
    private readonly MqttOptions _mqtt;
    private readonly AppVersion _version;
    private readonly ILogger<DispatchEntities> _logger;

    /// <param name="declaredDevices">The devices of the current valid configuration; null while there is none (the topic guard reads the same).</param>
    public DispatchEntities(
        IDispatchStatusSource status,
        IDeviceOverrideControl overrides,
        IDiscoveryLedger ledger,
        Func<IReadOnlyCollection<DeviceName>?> declaredDevices,
        IOptions<MqttOptions> mqtt,
        AppVersion version,
        ILogger<DispatchEntities> logger)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(mqtt);
        _status = status;
        _overrides = overrides;
        _ledger = ledger;
        _declaredDevices = declaredDevices;
        _mqtt = mqtt.Value;
        _version = version;
        _logger = logger;
        _status.Published += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    /// <summary>The override command topic of every declared device, exactly what the topic guard allows; none without a valid configuration.</summary>
    public IReadOnlyList<string> CommandTopics =>
        [.. (_declaredDevices() ?? []).Order().Select(d => OverrideCommandTopic(_mqtt.BaseTopic, d))];

    public static string DispatchObjectId(DeviceName device) => $"device_{device.Value}_dispatch";

    public static string OverrideObjectId(DeviceName device) => $"device_{device.Value}_override";

    /// <summary><c>&lt;BaseTopic&gt;/device_&lt;id&gt;_override/set</c>.</summary>
    public static string OverrideCommandTopic(string baseTopic, DeviceName device) => GuardEntities.CommandTopic(baseTopic, OverrideObjectId(device));

    public MqttEntitySet Current()
    {
        var declared = _declaredDevices();
        var status = _status.Current;
        var overrides = _overrides.Current;
        var entities = ImmutableArray.CreateBuilder<MqttEntity>();
        foreach (var device in (declared ?? LedgerDevices()).Distinct().Order())
        {
            var known = declared is null ? null : status.Devices.FirstOrDefault(d => d.Id == device);
            entities.Add(DispatchEntity(device, known));
            entities.Add(OverrideEntity(device, declared is null ? null : overrides.For(device)));
        }

        return new MqttEntitySet(entities.ToImmutable(), declared is not null);
    }

    public void HandleCommand(string topic, string payload)
    {
        if ((_declaredDevices() ?? []).Where(d => string.Equals(topic, OverrideCommandTopic(_mqtt.BaseTopic, d), StringComparison.Ordinal)).Select(d => (DeviceName?)d).FirstOrDefault() is not { } device)
        {
            return;
        }

        var mode = Enum.GetValues<OverrideMode>().Where(m => Snake(m) == payload).Select(m => (OverrideMode?)m).SingleOrDefault();
        if (mode is null)
        {
            _logger.LogWarning("Ignored an override command for {Device} that is not one of {Options}", device, string.Join(", ", OverrideOptions));
            return;
        }

        var result = _overrides.Set(device, mode.Value, OverrideSource.Mqtt);
        if (result is OverrideResult.UnknownDevice or OverrideResult.NoConfiguration)
        {
            _logger.LogWarning("Ignored an override command for {Device}: {Result}", device, result);
        }
    }

    private MqttEntity DispatchEntity(DeviceName device, DispatchDeviceStatus? status)
    {
        var objectId = DispatchObjectId(device);
        var intended = status is null ? null : status.Intended ?? status.Reported;
        return new MqttEntity(
            Sensor,
            objectId,
            Config(Sensor, objectId, $"{device.Value} dispatch", new() { ["device_class"] = "enum", ["options"] = DispatchStates, ["json_attributes_topic"] = GuardEntities.AttributesTopic(_mqtt.BaseTopic, objectId) }),
            State(status?.Type, intended) ?? GuardEntities.Unknown,
            Json(new Dictionary<string, object?>
            {
                ["reported"] = State(status?.Type, status?.Reported),
                ["intended_setpoint_a"] = intended?.Setpoint?.Value,
                ["reported_setpoint_a"] = status?.Reported?.Setpoint?.Value,
                ["matches"] = status?.Matches,
                ["reason"] = Name(status?.Reason),
                ["origin"] = status?.Origin is { } origin ? Snake(origin) : null,
                ["pending"] = Name(status?.Pending),
                ["deferred"] = status?.Deferred is { } deferred ? ReasonCodes.Name(deferred.RefusedBy) : null,
                ["deferred_due_at"] = status?.Deferred?.DueAt?.ToString("O", CultureInfo.InvariantCulture),
                ["settling"] = status is null ? null : Snake(status.Settling),
                ["unresponsive"] = status is null ? null : status.Unresponsive is not null,
                ["retry_at"] = status?.Unresponsive?.RetryAt?.ToString("O", CultureInfo.InvariantCulture),
                ["override"] = status is null ? null : Snake(status.Override.Mode),
                ["commanded"] = false,
            }));
    }

    private MqttEntity OverrideEntity(DeviceName device, DeviceOverride? current)
    {
        var objectId = OverrideObjectId(device);
        return new MqttEntity(
            Select,
            objectId,
            Config(Select, objectId, $"{device.Value} override", new()
            {
                ["command_topic"] = OverrideCommandTopic(_mqtt.BaseTopic, device),
                ["options"] = OverrideOptions,
                ["optimistic"] = false,
                ["retain"] = false,
            }),
            current is null ? GuardEntities.Unknown : Snake(current.Mode),
            null);
    }

    /// <summary>on, off or partial (some stages of a stepped device); a setpoint device is on while enabled above 0 A. Null when unknown.</summary>
    private static string? State(DeviceType? type, DeviceLevel? level)
    {
        if (level is null || type is null)
        {
            return null;
        }

        if (level.Setpoint is { } setpoint)
        {
            return level.Enabled && setpoint.Value > 0 ? "on" : "off";
        }

        var relays = level.Relays.IsDefault ? [] : level.Relays;
        return relays.All(r => r) && relays.Length > 0 ? "on" : relays.Any(r => r) ? "partial" : "off";
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

    /// <summary>Devices announced earlier, from the ledger: kept while no valid configuration says otherwise.</summary>
    private IEnumerable<DeviceName> LedgerDevices() =>
        _ledger.Discovered
            .Select(key => LedgerKey().Match(key))
            .Where(m => m.Success)
            .Select(m => DeviceName.TryParse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, out var device) ? device : null)
            .OfType<DeviceName>();

    private static string? Name(ReasonCode? reason) => reason is { } code ? ReasonCodes.Name(code) : null;

    private static string Json(Dictionary<string, object?> value) => JsonSerializer.Serialize(value);

    private static string Snake<TEnum>(TEnum value)
        where TEnum : struct, Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    [GeneratedRegex("^(?:sensor/device_([a-z][a-z0-9_]*)_dispatch|select/device_([a-z][a-z0-9_]*)_override)$", RegexOptions.CultureInvariant)]
    private static partial Regex LedgerKey();
}
