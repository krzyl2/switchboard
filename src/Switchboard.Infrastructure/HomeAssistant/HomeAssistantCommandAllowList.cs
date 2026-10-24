using System.Collections.Frozen;
using System.Text.Json;

namespace Switchboard.Infrastructure.HomeAssistant;

/// <summary>
/// Every WebSocket command the client may send, besides the auth handshake, and the exact shape of
/// each. This is the hard boundary behind "never command an entity the user has not declared": a
/// type that is not listed here cannot leave the process, and neither can a listed type with any
/// key it does not need (a second "type" key would otherwise win in Home Assistant's parser). Types
/// that change state in Home Assistant (call_service, fire_event, execute_script, ...) are added
/// only together with the declared-actuator gate that is allowed to use them.
/// </summary>
public static class HomeAssistantCommandAllowList
{
    public const string Ping = "ping";

    /// <summary>Read-only: state changes of the listed entities. Always sent with an entity filter.</summary>
    public const string SubscribeEntities = "subscribe_entities";

    /// <summary>
    /// Ends one of the client's own subscriptions (a superseded subscribe_entities after the entity
    /// set changed); never anything else's, and never the active one.
    /// </summary>
    public const string UnsubscribeEvents = "unsubscribe_events";

    private const string IdKey = "id";
    private const string TypeKey = "type";
    private const string EntityIdsKey = "entity_ids";
    private const string SubscriptionKey = "subscription";

    private static readonly FrozenDictionary<string, FrozenSet<string>> Shapes = new Dictionary<string, FrozenSet<string>>
    {
        [Ping] = new[] { IdKey, TypeKey }.ToFrozenSet(StringComparer.Ordinal),
        [SubscribeEntities] = new[] { IdKey, TypeKey, EntityIdsKey }.ToFrozenSet(StringComparer.Ordinal),
        [UnsubscribeEvents] = new[] { IdKey, TypeKey, SubscriptionKey }.ToFrozenSet(StringComparer.Ordinal),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static FrozenSet<string> Allowed { get; } = Shapes.Keys.ToFrozenSet(StringComparer.Ordinal);

    public static void EnsureAllowed(string type)
    {
        if (type is null || !Allowed.Contains(type))
        {
            throw new HomeAssistantCommandRefusedException(type);
        }
    }

    /// <summary>The check without any own subscription: every unsubscribe_events is refused.</summary>
    public static void EnsurePayloadAllowed(ReadOnlySpan<byte> utf8Json) => EnsurePayloadAllowed(utf8Json, _ => false);

    /// <summary>
    /// Re-reads a serialized command exactly as Home Assistant would and refuses it unless it is one
    /// JSON object with exactly the keys of its allowed type, each once: a numeric id, the type, for
    /// subscribe_entities a non-empty list of entity IDs (without it HA would stream every entity),
    /// and for unsubscribe_events a subscription id that <paramref name="isOwnSubscription"/> accepts.
    /// </summary>
    public static void EnsurePayloadAllowed(ReadOnlySpan<byte> utf8Json, Func<int, bool> isOwnSubscription)
    {
        ArgumentNullException.ThrowIfNull(isOwnSubscription);
        string? type = null;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var reader = new Utf8JsonReader(utf8Json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                throw Refused(null, "a command must be a JSON object");
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var key = reader.GetString()!;
                if (!keys.Add(key))
                {
                    throw Refused(type, $"key '{key}' appears twice");
                }

                reader.Read();
                switch (key)
                {
                    case IdKey when reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out _):
                        break;
                    case SubscriptionKey when reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var subscription):
                        if (!isOwnSubscription(subscription))
                        {
                            throw Refused(type, $"subscription {subscription} is not one of the client's own superseded subscriptions");
                        }

                        break;
                    case TypeKey when reader.TokenType == JsonTokenType.String:
                        type = reader.GetString();
                        break;
                    case EntityIdsKey when reader.TokenType == JsonTokenType.StartArray:
                        var count = 0;
                        while (reader.Read() && reader.TokenType == JsonTokenType.String)
                        {
                            count++;
                        }

                        if (reader.TokenType != JsonTokenType.EndArray || count == 0)
                        {
                            throw Refused(type, "entity_ids must be a non-empty list of strings");
                        }

                        break;
                    default:
                        throw Refused(type, $"key '{key}' is not allowed or has the wrong type");
                }
            }

            if (reader.TokenType != JsonTokenType.EndObject || reader.Read())
            {
                throw Refused(type, "trailing content after the command object");
            }
        }
        catch (JsonException)
        {
            throw Refused(type, "the command is not valid JSON");
        }

        EnsureAllowed(type!);
        if (!keys.SetEquals(Shapes[type!]))
        {
            throw Refused(type, $"a {type} command has exactly the keys {string.Join(", ", Shapes[type!])}");
        }
    }

    private static HomeAssistantCommandRefusedException Refused(string? type, string reason) => new(type, reason);
}

public sealed class HomeAssistantCommandRefusedException : InvalidOperationException
{
    public HomeAssistantCommandRefusedException(string? type)
        : base($"Home Assistant command type '{type}' is not on the outbound allow-list.") => CommandType = type;

    public HomeAssistantCommandRefusedException(string? type, string reason)
        : base($"Home Assistant command refused: {reason}.") => CommandType = type;

    public string? CommandType { get; }
}
