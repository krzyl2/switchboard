using System.Collections.Immutable;

namespace Switchboard.Infrastructure.Mqtt;

/// <summary>One published entity: its discovery config and its current state (and attributes, if any).</summary>
/// <param name="Config">The discovery payload (JSON).</param>
/// <param name="State">The state payload; <c>None</c> for an unknown value.</param>
/// <param name="Attributes">The attributes payload (JSON), or null for an entity without attributes.</param>
public sealed record MqttEntity(string Component, string ObjectId, string Config, string State, string? Attributes)
{
    /// <summary>The ledger key: <c>component/object_id</c>.</summary>
    public string Key => $"{Component}/{ObjectId}";
}

/// <param name="Authoritative">
/// True when the set comes from a valid configuration: an object announced earlier and absent now
/// is retracted. False while no valid configuration is known: nothing is ever retracted then, so a
/// broken file cannot delete the user's entities (07-PITFALLS: identity is an API).
/// </param>
public sealed record MqttEntitySet(ImmutableArray<MqttEntity> Entities, bool Authoritative);

/// <summary>A group of entities <see cref="MqttLink"/> announces, keeps current and, when they disappear, retracts.</summary>
public interface IMqttEntitySource
{
    /// <summary>Raised when any state may have changed; may be raised on any thread and must only be noted, never handled inline.</summary>
    event EventHandler? Changed;

    /// <summary>The command topics this source listens to (each must pass the topic guard's subscription check).</summary>
    IReadOnlyList<string> CommandTopics { get; }

    MqttEntitySet Current();

    /// <summary>A non-retained message on one of <see cref="CommandTopics"/>.</summary>
    void HandleCommand(string topic, string payload);
}
