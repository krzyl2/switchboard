namespace Switchboard.Infrastructure.Mqtt;

/// <summary>
/// Approved by the maintainer 2026-10-24. The MQTT discovery identity scheme is an API: changing
/// it after release orphans every entity users put on dashboards (07-PITFALLS.md). Do not change
/// it; new entities follow the same pattern.
/// <para>
/// unique_id <c>switchboard_&lt;object_id&gt;</c>; suggested entity id <c>&lt;component&gt;.switchboard_&lt;object_id&gt;</c>
/// via default_entity_id; discovery <c>&lt;prefix&gt;/&lt;component&gt;/&lt;BaseTopic&gt;/&lt;object_id&gt;/config</c>;
/// state <c>&lt;BaseTopic&gt;/&lt;object_id&gt;/state</c>; availability <c>&lt;BaseTopic&gt;/availability</c>.
/// The unique_id and device identifier do not follow BaseTopic on purpose: a topic is plumbing,
/// the identity is what HA's entity registry remembers.
/// </para>
/// </summary>
public static class MqttIdentity
{
    public const string DeviceIdentifier = "switchboard";
    public const string DeviceName = "Switchboard";
    public const string DeviceModel = "Home Assistant add-on";

    public const string AvailabilityOnline = "online";
    public const string AvailabilityOffline = "offline";

    /// <summary>Payload Home Assistant publishes on &lt;prefix&gt;/status after it (re)starts.</summary>
    public const string HomeAssistantOnline = "online";

    private const string UniqueIdPrefix = "switchboard_";

    public static string UniqueId(string objectId) => UniqueIdPrefix + objectId;

    /// <summary>Whether a registry unique_id is one of the add-on's own (the scheme above, unchanged): such an entity is never an actuator.</summary>
    public static bool IsOwnUniqueId(string? uniqueId) => uniqueId is not null && uniqueId.StartsWith(UniqueIdPrefix, StringComparison.Ordinal);

    public static string DefaultEntityId(string component, string objectId) => $"{component}.{UniqueId(objectId)}";

    public static string DiscoveryTopic(string discoveryPrefix, string component, string baseTopic, string objectId) =>
        $"{discoveryPrefix}/{component}/{baseTopic}/{objectId}/config";

    public static string StateTopic(string baseTopic, string objectId) => $"{baseTopic}/{objectId}/state";

    public static string AvailabilityTopic(string baseTopic) => $"{baseTopic}/availability";

    public static string HomeAssistantStatusTopic(string discoveryPrefix) => $"{discoveryPrefix}/status";
}
