using System.Text;
using MQTTnet;
using MQTTnet.Protocol;

namespace Switchboard.Infrastructure.Mqtt;

/// <summary><see cref="IMqttTransport"/> over MQTTnet 5. The topic guard sits here, the last step before the wire.</summary>
public sealed class MqttNetTransport : IMqttTransport, IDisposable
{
    private readonly IMqttClient _client;
    private readonly MqttTopicGuard _guard;

    public MqttNetTransport(MqttTopicGuard guard)
    {
        _guard = guard;
        _client = new MqttClientFactory().CreateMqttClient();
        _client.DisconnectedAsync += _ => Disconnected?.Invoke() ?? Task.CompletedTask;
        _client.ApplicationMessageReceivedAsync += args =>
            MessageReceived?.Invoke(new MqttInboundMessage(
                args.ApplicationMessage.Topic,
                args.ApplicationMessage.ConvertPayloadToString() ?? string.Empty,
                args.ApplicationMessage.Retain))
            ?? Task.CompletedTask;
    }

    public event Func<Task>? Disconnected;

    public event Func<MqttInboundMessage, Task>? MessageReceived;

    public bool IsConnected => _client.IsConnected;

    public async Task ConnectAsync(MqttConnectSettings settings, CancellationToken cancellationToken)
    {
        _guard.EnsureAllowed(settings.WillTopic);
        await _client.ConnectAsync(BuildClientOptions(settings), cancellationToken).ConfigureAwait(false);
    }

    public async Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken)
    {
        // Before anything touches the client: a refused topic never leaves the process.
        _guard.EnsureAllowed(topic);

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        var result = await _client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"Broker refused the publish to '{topic}': {result.ReasonCode}.");
        }
    }

    public async Task SubscribeAsync(string topicFilter, CancellationToken cancellationToken)
    {
        // Inbound is guarded as tightly as outbound: a subscription is how a message gets in.
        _guard.EnsureSubscriptionAllowed(topicFilter);
        var options = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(topicFilter, MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        await _client.SubscribeAsync(options, cancellationToken).ConfigureAwait(false);
    }

    public async Task UnsubscribeAsync(string topicFilter, CancellationToken cancellationToken)
    {
        var options = new MqttClientUnsubscribeOptionsBuilder().WithTopicFilter(topicFilter).Build();
        await _client.UnsubscribeAsync(options, cancellationToken).ConfigureAwait(false);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        var options = new MqttClientDisconnectOptionsBuilder()
            .WithReason(MqttClientDisconnectOptionsReason.NormalDisconnection)
            .Build();
        await _client.DisconnectAsync(options, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _client.Dispose();

    /// <summary>
    /// Maps our settings onto MQTTnet options. The Last Will is part of the CONNECT packet, so it is
    /// set here on every attempt: if the process dies the broker marks every entity unavailable.
    /// </summary>
    internal static MqttClientOptions BuildClientOptions(MqttConnectSettings settings)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(settings.Credentials.Host, settings.Credentials.Port)
            .WithClientId(settings.ClientId)
            .WithCleanSession()
            .WithKeepAlivePeriod(settings.KeepAlivePeriod)
            .WithWillTopic(settings.WillTopic)
            .WithWillPayload(Encoding.UTF8.GetBytes(settings.WillPayload))
            .WithWillRetain(settings.WillRetain)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);

        if (settings.Credentials.Username is not null)
        {
            builder = builder.WithCredentials(settings.Credentials.Username, settings.Credentials.Password);
        }

        if (settings.Credentials.UseTls)
        {
            builder = builder.WithTlsOptions(tls => tls.UseTls());
        }

        return builder.Build();
    }
}
