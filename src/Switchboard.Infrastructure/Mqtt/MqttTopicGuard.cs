using Switchboard.Application.Options;

namespace Switchboard.Infrastructure.Mqtt;

/// <summary>
/// Hard boundary on what the add-on may publish. Other topics on the same broker command real
/// devices (zigbee2mqtt/&lt;x&gt;/set switches a relay), so anything outside our own namespace is
/// refused before it reaches the client:
/// <list type="bullet">
/// <item><c>&lt;BaseTopic&gt;/...</c> — our availability and state topics;</item>
/// <item>exactly <c>&lt;DiscoveryPrefix&gt;/&lt;component&gt;/&lt;BaseTopic&gt;/&lt;object_id&gt;/config</c> — our discovery configs.</item>
/// </list>
/// </summary>
public sealed class MqttTopicGuard
{
    private const string ConfigLevel = "config";
    private const int DiscoveryLevels = 5;

    private readonly string _discoveryPrefix;
    private readonly string _baseTopic;

    public MqttTopicGuard(string discoveryPrefix, string baseTopic)
    {
        if (!TopicLevel.IsPlain(discoveryPrefix) || !TopicLevel.IsPlain(baseTopic))
        {
            throw new ArgumentException("Discovery prefix and base topic must each be a single plain topic level.");
        }

        _discoveryPrefix = discoveryPrefix;
        _baseTopic = baseTopic;
    }

    public bool IsAllowed(string? topic)
    {
        if (string.IsNullOrEmpty(topic))
        {
            return false;
        }

        var levels = topic.Split('/');

        // Wildcards, empty levels and anything that is not a plain identifier are refused outright:
        // our own topics never need them, and they are how a prefix check gets tricked.
        if (!levels.All(TopicLevel.IsPlain))
        {
            return false;
        }

        if (levels.Length >= 2 && levels[0] == _baseTopic)
        {
            return true;
        }

        return levels.Length == DiscoveryLevels
            && levels[0] == _discoveryPrefix
            && levels[2] == _baseTopic
            && levels[4] == ConfigLevel;
    }

    public void EnsureAllowed(string? topic)
    {
        if (!IsAllowed(topic))
        {
            throw new MqttTopicRefusedException(topic);
        }
    }
}

public sealed class MqttTopicRefusedException(string? topic)
    : InvalidOperationException($"Publishing to MQTT topic '{topic}' is outside the add-on's own namespace and was refused.")
{
    public string? Topic { get; } = topic;
}
