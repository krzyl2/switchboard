using Switchboard.Application.Options;
using Switchboard.Domain.Devices;

namespace Switchboard.Infrastructure.Mqtt;

/// <summary>
/// Hard boundary on what the add-on may publish. Other topics on the same broker command real
/// devices (zigbee2mqtt/&lt;x&gt;/set switches a relay), so anything outside our own namespace is
/// refused before it reaches the client:
/// <list type="bullet">
/// <item><c>&lt;BaseTopic&gt;/...</c> — our availability, state and attribute topics, but never one
/// of our own command topics (<c>&lt;BaseTopic&gt;/&lt;object_id&gt;/set</c>): a retained ON there would
/// command the add-on itself;</item>
/// <item>exactly <c>&lt;DiscoveryPrefix&gt;/&lt;component&gt;/&lt;BaseTopic&gt;/&lt;object_id&gt;/config</c> — our discovery configs.</item>
/// </list>
/// Subscriptions are narrower still: exactly Home Assistant's birth topic, the kill switch's and the
/// control switch's command topics, for each device of the current valid configuration (the
/// declared set, re-read at every check), exactly its override select's command topic and, while that configuration holds an <c>ev:</c> section, exactly the
/// three command topics of the EV controls. A subscription is how a message gets
/// in, so nothing else may be listened to: no wildcard, no prefix match, no undeclared device.
/// </summary>
public sealed class MqttTopicGuard
{
    private const string ConfigLevel = "config";
    private const string CommandLevel = "set";
    private const int DiscoveryLevels = 5;
    private const int CommandLevels = 3;

    private readonly string _discoveryPrefix;
    private readonly string _baseTopic;
    private readonly string[] _subscriptions;
    private readonly Func<IReadOnlyCollection<DeviceName>?> _declaredDevices;
    private readonly Func<bool> _evControlsEnabled;

    /// <param name="declaredDevices">The devices of the current valid configuration; null (or no provider) while there is none.</param>
    /// <param name="evControlsEnabled">Whether the current valid configuration holds an <c>ev:</c> section: only then are the EV controls' command topics listened to.</param>
    public MqttTopicGuard(string discoveryPrefix, string baseTopic, Func<IReadOnlyCollection<DeviceName>?>? declaredDevices = null, Func<bool>? evControlsEnabled = null)
    {
        if (!TopicLevel.IsPlain(discoveryPrefix) || !TopicLevel.IsPlain(baseTopic))
        {
            throw new ArgumentException("Discovery prefix and base topic must each be a single plain topic level.");
        }

        _discoveryPrefix = discoveryPrefix;
        _baseTopic = baseTopic;
        _subscriptions =
        [
            MqttIdentity.HomeAssistantStatusTopic(discoveryPrefix),
            GuardEntities.CommandTopic(baseTopic, GuardEntities.KillSwitchObjectId),
            GuardEntities.CommandTopic(baseTopic, GuardEntities.ControlAllowedObjectId),
        ];
        _declaredDevices = declaredDevices ?? (() => null);
        _evControlsEnabled = evControlsEnabled ?? (() => false);
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
            return !(levels.Length == CommandLevels && levels[2] == CommandLevel);
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

    /// <summary>Exactly Home Assistant's birth topic, the kill switch and control switch command topics, the override command topic of a declared device and, with an <c>ev:</c> section, the EV controls' command topics.</summary>
    public bool IsSubscriptionAllowed(string? topicFilter) =>
        topicFilter is not null
        && (_subscriptions.Contains(topicFilter, StringComparer.Ordinal)
            || (_declaredDevices() ?? []).Any(d => string.Equals(topicFilter, DispatchEntities.OverrideCommandTopic(_baseTopic, d), StringComparison.Ordinal))
            || (_evControlsEnabled() && EvEntities.Topics(_baseTopic).Contains(topicFilter, StringComparer.Ordinal)));

    public void EnsureSubscriptionAllowed(string? topicFilter)
    {
        if (!IsSubscriptionAllowed(topicFilter))
        {
            throw new MqttTopicRefusedException(topicFilter, subscription: true);
        }
    }
}

public sealed class MqttTopicRefusedException(string? topic, bool subscription = false)
    : InvalidOperationException(subscription
        ? $"Subscribing to MQTT topic '{topic}' is not allowed; the add-on listens only to Home Assistant's birth topic, its kill switch and control switch commands, the override commands of declared devices and the EV controls of a configured ev: section."
        : $"Publishing to MQTT topic '{topic}' is outside the add-on's own namespace and was refused.")
{
    public string? Topic { get; } = topic;
}
