using System.Text.Json;
using Switchboard.Application;
using Switchboard.Infrastructure.Mqtt;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// The discovery payload is an API with the user's HA instance: the unique_id and entity id must
/// stay stable (dashboards and automations bind to them), and expire_after is what turns a hung
/// add-on into "unavailable" so the HA-side shedding automation fires.
/// </summary>
public sealed class HealthEntityDiscoveryTests
{
    private readonly JsonElement _config = JsonDocument.Parse(
        HealthEntityDiscovery.BuildConfig(TestOptions.Mqtt(), TestOptions.Heartbeat(), new AppVersion("0.1.0"))).RootElement;

    [Fact]
    public void Discovery_topic_is_under_the_prefix_with_our_node()
    {
        Assert.Equal("homeassistant/binary_sensor/switchboard/healthy/config", HealthEntityDiscovery.DiscoveryTopic(TestOptions.Mqtt()));
    }

    [Fact]
    public void Identity_is_the_provisional_stable_scheme()
    {
        Assert.Equal("switchboard_healthy", _config.GetProperty("unique_id").GetString());
        Assert.Equal("binary_sensor.switchboard_healthy", _config.GetProperty("default_entity_id").GetString());
        Assert.True(_config.GetProperty("has_entity_name").GetBoolean());
        Assert.Equal("Healthy", _config.GetProperty("name").GetString());
    }

    [Fact]
    public void Deprecated_object_id_key_is_not_used()
    {
        // HA 2025.10 replaced object_id with default_entity_id and warns on the old key.
        Assert.False(_config.TryGetProperty("object_id", out _));
    }

    [Fact]
    public void State_and_availability_topics_and_payloads_match_what_we_publish()
    {
        Assert.Equal("switchboard/healthy/state", _config.GetProperty("state_topic").GetString());
        Assert.Equal("ON", _config.GetProperty("payload_on").GetString());
        Assert.Equal("OFF", _config.GetProperty("payload_off").GetString());
        Assert.Equal("switchboard/availability", _config.GetProperty("availability_topic").GetString());
        Assert.Equal("online", _config.GetProperty("payload_available").GetString());
        Assert.Equal("offline", _config.GetProperty("payload_not_available").GetString());
    }

    [Fact]
    public void Expire_after_is_the_configured_heartbeat_expiry_in_whole_seconds()
    {
        Assert.Equal(JsonValueKind.Number, _config.GetProperty("expire_after").ValueKind);
        Assert.Equal((long)TestOptions.HeartbeatExpireAfter.TotalSeconds, _config.GetProperty("expire_after").GetInt64());
    }

    [Fact]
    public void Device_block_groups_the_entity_under_one_Switchboard_device()
    {
        var device = _config.GetProperty("device");

        Assert.Equal(["switchboard"], device.GetProperty("identifiers").EnumerateArray().Select(i => i.GetString()));
        Assert.Equal("Switchboard", device.GetProperty("name").GetString());
        Assert.Equal("Home Assistant add-on", device.GetProperty("model").GetString());
        Assert.Equal("0.1.0", device.GetProperty("sw_version").GetString());
    }

    [Fact]
    public void Every_published_topic_passes_the_topic_guard()
    {
        var guard = new MqttTopicGuard("homeassistant", "switchboard");

        Assert.True(guard.IsAllowed(HealthEntityDiscovery.DiscoveryTopic(TestOptions.Mqtt())));
        Assert.True(guard.IsAllowed(HealthEntityDiscovery.StateTopic(TestOptions.Mqtt())));
        Assert.True(guard.IsAllowed(_config.GetProperty("availability_topic").GetString()));
    }
}
