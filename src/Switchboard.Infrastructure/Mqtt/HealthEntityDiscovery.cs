using System.Text.Json;
using Switchboard.Application;
using Switchboard.Application.Options;

namespace Switchboard.Infrastructure.Mqtt;

/// <summary>
/// Discovery for <c>binary_sensor.switchboard_healthy</c>, the watchdog entity (SAFE-02) an HA-side
/// automation uses to shed loads when the add-on dies or hangs.
/// </summary>
public static class HealthEntityDiscovery
{
    public const string Component = "binary_sensor";
    public const string ObjectId = "healthy";
    public const string Name = "Healthy";
    public const string PayloadOn = "ON";
    public const string PayloadOff = "OFF";

    public static string DiscoveryTopic(MqttOptions mqtt) =>
        MqttIdentity.DiscoveryTopic(mqtt.DiscoveryPrefix, Component, mqtt.BaseTopic, ObjectId);

    public static string StateTopic(MqttOptions mqtt) => MqttIdentity.StateTopic(mqtt.BaseTopic, ObjectId);

    public static string Payload(bool healthy) => healthy ? PayloadOn : PayloadOff;

    public static string BuildConfig(MqttOptions mqtt, HeartbeatOptions heartbeat, AppVersion version)
    {
        var config = new Dictionary<string, object>
        {
            ["name"] = Name,
            ["has_entity_name"] = true,
            ["unique_id"] = MqttIdentity.UniqueId(ObjectId),

            // default_entity_id replaced the deprecated object_id key (HA 2025.10).
            ["default_entity_id"] = MqttIdentity.DefaultEntityId(Component, ObjectId),
            ["state_topic"] = StateTopic(mqtt),
            ["payload_on"] = PayloadOn,
            ["payload_off"] = PayloadOff,
            ["availability_topic"] = MqttIdentity.AvailabilityTopic(mqtt.BaseTopic),
            ["payload_available"] = MqttIdentity.AvailabilityOnline,
            ["payload_not_available"] = MqttIdentity.AvailabilityOffline,

            // A hung process keeps its broker session (no LWT), so only expiry turns it unavailable.
            ["expire_after"] = (long)heartbeat.ExpireAfter.TotalSeconds,
            ["device"] = new Dictionary<string, object>
            {
                ["identifiers"] = new[] { MqttIdentity.DeviceIdentifier },
                ["name"] = MqttIdentity.DeviceName,
                ["model"] = MqttIdentity.DeviceModel,
                ["sw_version"] = version.Value,
            },
        };

        return JsonSerializer.Serialize(config);
    }
}
