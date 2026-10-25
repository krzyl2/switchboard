using System.Collections.Frozen;
using System.Text.Json;
using Switchboard.Application.Configuration;
using Switchboard.Application.Signals;

namespace Switchboard.Infrastructure.HomeAssistant;

/// <summary>
/// Every WebSocket command the client may send, besides the auth handshake, and the exact shape of
/// each. This is the hard boundary behind "never command an entity the user has not declared": a
/// type that is not listed here cannot leave the process, and neither can a listed type with any
/// key it does not need (a second "type" key would otherwise win in Home Assistant's parser).
/// <para>
/// The only state-changing type is <c>call_service</c> (control, maintainer survey 2026-10-25),
/// in exactly one shape: one entity as <c>target.entity_id</c> (never a list, never an area or a
/// device), one of six domain/service pairs (relays and stop actuators on and off, setpoints
/// set_value with one finite number), the entity's own domain, and an entity the configuration
/// loaded at send time declares as an actuator allowing that service and value. Nothing else that
/// changes state in Home Assistant (fire_event, execute_script, registry or config writes, a service
/// on an undeclared entity) has a shape here; an architecture test keeps it that way.
/// </para>
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

    /// <summary>Read-only query (setup discovery): the current state of every entity.</summary>
    public const string GetStates = "get_states";

    /// <summary>Read-only query (setup discovery): the entity registry, whose platform tells a derived sensor from a primary one.</summary>
    public const string EntityRegistryList = "config/entity_registry/list";

    /// <summary>Read-only query (setup discovery): the device registry, to group entities by device.</summary>
    public const string DeviceRegistryList = "config/device_registry/list";

    /// <summary>The one state-changing type: a service call on one declared actuator (see <see cref="EnsureCallServiceAllowed"/>).</summary>
    public const string CallService = "call_service";

    private const string TurnOn = "turn_on";
    private const string TurnOff = "turn_off";
    private const string SetValue = "set_value";

    private const string IdKey = "id";
    private const string TypeKey = "type";
    private const string EntityIdsKey = "entity_ids";
    private const string SubscriptionKey = "subscription";
    private const string DomainKey = "domain";
    private const string ServiceKey = "service";
    private const string ServiceDataKey = "service_data";
    private const string TargetKey = "target";
    private const string EntityIdKey = "entity_id";
    private const string ValueKey = "value";

    private static readonly FrozenDictionary<string, FrozenSet<string>> Shapes = new Dictionary<string, FrozenSet<string>>
    {
        [Ping] = new[] { IdKey, TypeKey }.ToFrozenSet(StringComparer.Ordinal),
        [SubscribeEntities] = new[] { IdKey, TypeKey, EntityIdsKey }.ToFrozenSet(StringComparer.Ordinal),
        [UnsubscribeEvents] = new[] { IdKey, TypeKey, SubscriptionKey }.ToFrozenSet(StringComparer.Ordinal),
        [GetStates] = new[] { IdKey, TypeKey }.ToFrozenSet(StringComparer.Ordinal),
        [EntityRegistryList] = new[] { IdKey, TypeKey }.ToFrozenSet(StringComparer.Ordinal),
        [DeviceRegistryList] = new[] { IdKey, TypeKey }.ToFrozenSet(StringComparer.Ordinal),
        [CallService] = new[] { IdKey, TypeKey, DomainKey, ServiceKey, TargetKey }.ToFrozenSet(StringComparer.Ordinal),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenSet<string> SetValueShape = new[] { IdKey, TypeKey, DomainKey, ServiceKey, ServiceDataKey, TargetKey }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The domain/service pairs a call_service may carry, each with its meaning: relays and stop actuators on and off, setpoints set_value.</summary>
    private static readonly FrozenDictionary<(string Domain, string Service), ActuatorService> Pairs = new Dictionary<(string, string), ActuatorService>
    {
        [("switch", TurnOn)] = ActuatorService.TurnOn,
        [("switch", TurnOff)] = ActuatorService.TurnOff,
        [("input_boolean", TurnOn)] = ActuatorService.TurnOn,
        [("input_boolean", TurnOff)] = ActuatorService.TurnOff,
        [("number", SetValue)] = ActuatorService.SetValue,
        [("input_number", SetValue)] = ActuatorService.SetValue,
    }.ToFrozenDictionary();

    public static FrozenSet<string> Allowed { get; } = Shapes.Keys.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The queries that answer with a result and change nothing; the only types the client's query path sends.</summary>
    public static FrozenSet<string> ReadOnlyQueries { get; } = new[] { GetStates, EntityRegistryList, DeviceRegistryList }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Every domain/service pair a call_service may carry.</summary>
    public static IReadOnlyCollection<(string Domain, string Service)> CallServicePairs => Pairs.Keys;

    /// <summary>The wire name of a service.</summary>
    public static string ServiceName(ActuatorService service) => service switch
    {
        ActuatorService.TurnOn => TurnOn,
        ActuatorService.TurnOff => TurnOff,
        ActuatorService.SetValue => SetValue,
        _ => throw new ArgumentOutOfRangeException(nameof(service), service, "Not a service of the allow-list."),
    };

    /// <summary>The meaning of a domain/service pair; null when the pair is not on the list.</summary>
    public static ActuatorService? ServiceOf(string domain, string service) => Pairs.TryGetValue((domain, service), out var meaning) ? meaning : null;

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
    /// A call_service never passes here: it is checked against the declared actuators by
    /// <see cref="EnsureCallServiceAllowed"/>.
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
        if (type == CallService)
        {
            throw Refused(type, $"a {CallService} is checked against the declared actuators, never on this path");
        }

        if (!keys.SetEquals(Shapes[type!]))
        {
            throw Refused(type, $"a {type} command has exactly the keys {string.Join(", ", Shapes[type!])}");
        }
    }

    /// <summary>
    /// The one shape of a service call, re-read as Home Assistant would read it: exactly
    /// <c>{id, type: "call_service", domain, service, target: {entity_id: "&lt;one entity&gt;"}}</c>,
    /// plus <c>service_data: {value: &lt;finite number&gt;}</c> for set_value and only there; each key
    /// once; the pair on the list; the entity's domain equal to <c>domain</c>; and
    /// <paramref name="isDeclared"/> (the configuration loaded now) accepting the entity, the service
    /// and the value. Anything else is refused before the socket is touched.
    /// </summary>
    public static void EnsureCallServiceAllowed(ReadOnlySpan<byte> utf8Json, Func<EntityId, string, ActuatorService, double?, bool> isDeclared)
    {
        ArgumentNullException.ThrowIfNull(isDeclared);
        string? type = null, domain = null, service = null, entity = null;
        double? value = null;
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
                    throw Refused(CallService, $"key '{key}' appears twice");
                }

                reader.Read();
                switch (key)
                {
                    case IdKey when reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out _):
                        break;
                    case TypeKey when reader.TokenType == JsonTokenType.String:
                        type = reader.GetString();
                        break;
                    case DomainKey when reader.TokenType == JsonTokenType.String:
                        domain = reader.GetString();
                        break;
                    case ServiceKey when reader.TokenType == JsonTokenType.String:
                        service = reader.GetString();
                        break;
                    case TargetKey when reader.TokenType == JsonTokenType.StartObject:
                        entity = ReadSingle(ref reader, EntityIdKey, JsonTokenType.String, "target is exactly {entity_id: one entity ID}") is { } text ? text : null;
                        break;
                    case ServiceDataKey when reader.TokenType == JsonTokenType.StartObject:
                        var raw = ReadSingle(ref reader, ValueKey, JsonTokenType.Number, "service_data is exactly {value: one number}");
                        value = double.Parse(raw!, System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    default:
                        throw Refused(CallService, $"key '{key}' is not allowed in a {CallService} or has the wrong type");
                }
            }

            if (reader.TokenType != JsonTokenType.EndObject || reader.Read())
            {
                throw Refused(CallService, "trailing content after the command object");
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException)
        {
            throw Refused(CallService, "the command is not valid JSON");
        }

        if (type != CallService)
        {
            throw Refused(type, $"not a {CallService}");
        }

        if (domain is null || service is null || ServiceOf(domain, service) is not { } meaning)
        {
            throw Refused(CallService, $"{domain}.{service} is not an allowed service");
        }

        var shape = meaning == ActuatorService.SetValue ? SetValueShape : Shapes[CallService];
        if (!keys.SetEquals(shape))
        {
            throw Refused(CallService, $"a {domain}.{service} call has exactly the keys {string.Join(", ", shape)}");
        }

        if (value is { } number && !double.IsFinite(number))
        {
            throw Refused(CallService, "the value is not a finite number");
        }

        if (entity is null || !EntityId.TryParse(entity, out var id) || id.Value.Value[..id.Value.Value.IndexOf('.', StringComparison.Ordinal)] != domain)
        {
            throw Refused(CallService, $"the target is not one entity of the {domain} domain");
        }

        if (!isDeclared(id.Value, domain, meaning, value))
        {
            throw Refused(CallService, $"{id} is not a declared actuator that allows {domain}.{service} with this value in the configuration loaded now");
        }
    }

    /// <summary>Reads an object holding exactly one key of one token type; returns that value as text.</summary>
    private static string? ReadSingle(ref Utf8JsonReader reader, string name, JsonTokenType token, string shape)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.PropertyName || reader.GetString() != name)
        {
            throw Refused(CallService, shape);
        }

        reader.Read();
        if (reader.TokenType != token)
        {
            throw Refused(CallService, shape);
        }

        var text = token == JsonTokenType.String
            ? reader.GetString()
            : System.Text.Encoding.UTF8.GetString(reader.HasValueSequence ? System.Buffers.BuffersExtensions.ToArray(reader.ValueSequence) : reader.ValueSpan.ToArray());
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndObject)
        {
            throw Refused(CallService, shape);
        }

        return text;
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
