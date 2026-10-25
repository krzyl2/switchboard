using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Switchboard.Application.Setup.Discovery;
using Switchboard.Application.Signals;
using Switchboard.Infrastructure.Mqtt;

namespace Switchboard.Infrastructure.HomeAssistant;

/// <summary>
/// Turns the answers of get_states, config/entity_registry/list and config/device_registry/list into
/// one discovery. The answers are untrusted: a state list of the wrong shape is refused as a whole
/// (never half-read), and a registry of the wrong shape counts as not available (no entity is then
/// judged by its platform). At most <c>max</c> entities are kept, in entity-ID order, and the result
/// says when that cut anything. A select's options are read where the state carries a bounded list
/// of texts, and Switchboard's own MQTT entities are marked so they are never offered as actuators.
/// </summary>
public static class DiscoveryResponseParser
{
    // The integration of Switchboard's own discovered entities (with the add-on's unique_id scheme).
    private const string MqttPlatform = "mqtt";

    public static DiscoverySnapshot Parse(JsonElement states, JsonElement? entityRegistry, JsonElement? deviceRegistry, DateTimeOffset at, int max)
    {
        var stateEntries = ReadStates(states)
            ?? throw new DiscoveryUnavailableException(DiscoveryUnavailableException.HomeAssistantUnavailable, "Home Assistant answered get_states in an unexpected shape.");
        var registry = entityRegistry is { } entries ? ReadRegistry(entries) : null;
        var devices = deviceRegistry is { } list ? ReadDevices(list) ?? [] : [];

        var ids = stateEntries.Keys.Concat(registry?.Keys ?? Enumerable.Empty<EntityId>()).Distinct().Order().ToList();
        var entities = ImmutableArray.CreateBuilder<DiscoveredEntity>();
        foreach (var id in ids.Take(max))
        {
            stateEntries.TryGetValue(id, out var state);
            RegistryEntry? entry = null;
            registry?.TryGetValue(id, out entry);
            entities.Add(new DiscoveredEntity(
                id,
                entry?.Name ?? state?.FriendlyName ?? entry?.OriginalName,
                entry?.DeviceClass ?? state?.DeviceClass ?? entry?.OriginalDeviceClass,
                state?.StateClass,
                state?.Unit,
                entry?.Platform,
                entry?.DeviceId,
                entry?.Disabled ?? false,
                entry?.Hidden ?? false,
                state?.State,
                state?.Reported,
                entry is not null)
            {
                Options = state?.Options,
                Own = entry is { Platform: MqttPlatform } && MqttIdentity.IsOwnUniqueId(entry.UniqueId),
            });
        }

        // The platform index covers the whole registry: only the list shown to the user is cut.
        return new DiscoverySnapshot(at, entities.ToImmutable(), devices, registry is not null, ids.Count > max)
        {
            RegistryPlatforms = registry?.ToFrozenDictionary(e => e.Key, e => e.Value.Platform) ?? FrozenDictionary<EntityId, string?>.Empty,
        };
    }

    private sealed record StateEntry(string State, string? Unit, string? DeviceClass, string? StateClass, string? FriendlyName, DateTimeOffset? Reported)
    {
        public ImmutableArray<string>? Options { get; init; }
    }

    private sealed record RegistryEntry(string? Platform, string? DeviceId, bool Disabled, bool Hidden, string? Name, string? OriginalName, string? DeviceClass, string? OriginalDeviceClass)
    {
        public string? UniqueId { get; init; }
    }

    private static Dictionary<EntityId, StateEntry>? ReadStates(JsonElement states)
    {
        if (states.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new Dictionary<EntityId, StateEntry>();
        foreach (var item in states.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !EntityId.TryParse(Text(item, "entity_id"), out var id)
                || Text(item, "state") is not { } state
                || (item.TryGetProperty("attributes", out var attributes) && attributes.ValueKind != JsonValueKind.Object))
            {
                return null;
            }

            var hasAttributes = item.TryGetProperty("attributes", out attributes);
            string? Attribute(string key) => hasAttributes ? Text(attributes, key) : null;
            var reported = Time(item, "last_reported") ?? Time(item, "last_updated") ?? Time(item, "last_changed");
            result[id.Value] = new StateEntry(state, Attribute("unit_of_measurement"), Attribute("device_class"), Attribute("state_class"), Attribute("friendly_name"), reported)
            {
                Options = hasAttributes ? Options(attributes) : null,
            };
        }

        return result;
    }

    private static Dictionary<EntityId, RegistryEntry>? ReadRegistry(JsonElement registry)
    {
        if (registry.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new Dictionary<EntityId, RegistryEntry>();
        foreach (var item in registry.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !EntityId.TryParse(Text(item, "entity_id"), out var id))
            {
                return null;
            }

            result[id.Value] = new RegistryEntry(
                Text(item, "platform"),
                Text(item, "device_id"),
                Present(item, "disabled_by"),
                Present(item, "hidden_by"),
                Text(item, "name"),
                Text(item, "original_name"),
                Text(item, "device_class"),
                Text(item, "original_device_class"))
            {
                UniqueId = Text(item, "unique_id"),
            };
        }

        return result;
    }

    /// <summary>A select's options: a bounded list of texts, anything else read as not known (never a refusal of the whole list).</summary>
    private static ImmutableArray<string>? Options(JsonElement attributes) =>
        attributes.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array && options.GetArrayLength() <= EntityUpdate.MaxOptions
            ? EntityUpdate.BoundedOptions([.. options.EnumerateArray().Select(o => o.ValueKind == JsonValueKind.String ? o.GetString() : null)])
            : null;

    private static ImmutableArray<DiscoveredDevice>? ReadDevices(JsonElement devices)
    {
        if (devices.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = ImmutableArray.CreateBuilder<DiscoveredDevice>();
        foreach (var item in devices.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || Text(item, "id") is not { } id)
            {
                return null;
            }

            result.Add(new DiscoveredDevice(id, Text(item, "name_by_user") ?? Text(item, "name"), Text(item, "manufacturer"), Text(item, "model")));
        }

        return result.ToImmutable();
    }

    private static string? Text(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Present(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    private static DateTimeOffset? Time(JsonElement item, string key) =>
        Text(item, key) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) ? value : null;
}
