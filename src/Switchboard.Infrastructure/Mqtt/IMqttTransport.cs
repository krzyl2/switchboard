namespace Switchboard.Infrastructure.Mqtt;

/// <summary>Everything needed for one broker connection, including the Last Will. Never printed with its password.</summary>
public sealed class MqttConnectSettings
{
    public required MqttCredentials Credentials { get; init; }

    public required string ClientId { get; init; }

    public required string WillTopic { get; init; }

    public required string WillPayload { get; init; }

    public required bool WillRetain { get; init; }

    public required TimeSpan KeepAlivePeriod { get; init; }

    public override string ToString() => $"{Credentials} as {ClientId}";
}

/// <summary>One message on a subscribed topic. <see cref="Retained"/> is set for a message the broker replayed from its store.</summary>
public sealed record MqttInboundMessage(string Topic, string Payload, bool Retained);

/// <summary>
/// The broker connection under <see cref="MqttLink"/>, narrow enough for a hand-written fake.
/// Implementations must refuse every topic the <see cref="MqttTopicGuard"/> refuses, for
/// publishing and for subscribing.
/// </summary>
public interface IMqttTransport
{
    bool IsConnected { get; }

    /// <summary>Raised when an established or attempted connection is lost.</summary>
    event Func<Task>? Disconnected;

    /// <summary>Raised for each message on a subscribed topic (payload decoded as UTF-8).</summary>
    event Func<MqttInboundMessage, Task>? MessageReceived;

    Task ConnectAsync(MqttConnectSettings settings, CancellationToken cancellationToken);

    /// <summary>QoS 1 publish.</summary>
    Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken);

    Task SubscribeAsync(string topicFilter, CancellationToken cancellationToken);

    /// <summary>Clean disconnect: the broker does not publish the Last Will.</summary>
    Task DisconnectAsync(CancellationToken cancellationToken);
}
