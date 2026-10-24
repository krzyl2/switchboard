using Switchboard.Infrastructure.Mqtt;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// The same broker carries topics that switch real devices (zigbee2mqtt/&lt;x&gt;/set). The guard
/// is what guarantees the add-on can only ever write inside its own namespace.
/// </summary>
public sealed class MqttTopicGuardTests
{
    private readonly MqttTopicGuard _guard = new("homeassistant", "switchboard");

    [Theory]
    [InlineData("switchboard/availability")]
    [InlineData("switchboard/healthy/state")]
    [InlineData("homeassistant/binary_sensor/switchboard/healthy/config")]
    [InlineData("homeassistant/sensor/switchboard/l1_headroom/config")]
    [InlineData("homeassistant/switch/switchboard/kill_switch/config")]
    [InlineData("switchboard/kill_switch/state")]
    [InlineData("switchboard/device_heater_l1_state/attributes")]
    public void Our_own_topics_are_allowed(string topic)
    {
        Assert.True(_guard.IsAllowed(topic));
    }

    [Theory]
    [InlineData("zigbee2mqtt/heater/set")]
    [InlineData("zigbee2mqtt/x/set")]
    [InlineData("shellies/relay/0/command")]
    [InlineData("homeassistant/switch/other/x/config")]
    [InlineData("homeassistant/switch/x/config")]
    [InlineData("homeassistant/binary_sensor/switchboard/healthy/set")]
    [InlineData("homeassistant/binary_sensor/switchboard/healthy/config/extra")]
    [InlineData("homeassistant/binary_sensor/switchboard/../config")]
    [InlineData("homeassistant/binary_sensor/switchboard//config")]
    [InlineData("homeassistant/binary_sensor/switchboard/+/config")]
    [InlineData("homeassistant/status")]
    [InlineData("other/binary_sensor/switchboard/healthy/config")]
    [InlineData("switchboardx/healthy/state")]
    [InlineData("switchboard_evil/healthy/state")]
    [InlineData("Switchboard/healthy/state")]
    [InlineData("switchboard")]
    [InlineData("switchboard/")]
    [InlineData("switchboard/#")]
    [InlineData("switchboard/+/state")]
    [InlineData("switchboard/../zigbee2mqtt/x/set")]
    [InlineData("/switchboard/healthy/state")]
    [InlineData("switchboard/healthy/state ")]
    [InlineData("switchboard/kill_switch/set")]
    [InlineData("switchboard/anything/set")]
    [InlineData("")]
    [InlineData(null)]
    public void Everything_else_is_refused(string? topic)
    {
        Assert.False(_guard.IsAllowed(topic));
        Assert.Throws<MqttTopicRefusedException>(() => _guard.EnsureAllowed(topic));
    }

    [Theory]
    [InlineData("homeassistant/status")]
    [InlineData("switchboard/kill_switch/set")]
    public void Only_kill_switch_and_status_subscribable(string topic)
    {
        // A subscription is how a message gets in: exactly HA's birth topic and our one command.
        Assert.True(_guard.IsSubscriptionAllowed(topic));
        _guard.EnsureSubscriptionAllowed(topic);
    }

    [Theory]
    [InlineData("#")]
    [InlineData("switchboard/#")]
    [InlineData("switchboard/+/set")]
    [InlineData("homeassistant/#")]
    [InlineData("homeassistant/+")]
    [InlineData("switchboard/kill_switch/state")]
    [InlineData("switchboard/healthy/set")]
    [InlineData("zigbee2mqtt/heater/set")]
    [InlineData("switchboard/kill_switch/set/")]
    [InlineData("")]
    [InlineData(null)]
    public void Wildcard_subscriptions_refused(string? topic)
    {
        Assert.False(_guard.IsSubscriptionAllowed(topic));
        Assert.Throws<MqttTopicRefusedException>(() => _guard.EnsureSubscriptionAllowed(topic));
    }

    [Fact]
    public void Publishing_to_our_own_command_topic_is_refused()
    {
        // A retained ON published there would engage the add-on's own kill switch at every connect.
        Assert.False(_guard.IsAllowed("switchboard/kill_switch/set"));
        Assert.True(_guard.IsAllowed("switchboard/kill_switch/state"));
    }

    [Theory]
    [InlineData("home/assistant", "switchboard")]
    [InlineData("homeassistant", "switch/board")]
    [InlineData("homeassistant", "#")]
    [InlineData("", "switchboard")]
    public void A_prefix_that_would_widen_the_guard_is_rejected(string prefix, string baseTopic)
    {
        Assert.Throws<ArgumentException>(() => new MqttTopicGuard(prefix, baseTopic));
    }
}
