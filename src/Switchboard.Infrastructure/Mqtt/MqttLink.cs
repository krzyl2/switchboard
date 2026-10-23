using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Switchboard.Application;
using Switchboard.Application.Connectivity;
using Switchboard.Application.Health;
using Switchboard.Application.Options;
using Switchboard.Application.Resilience;

namespace Switchboard.Infrastructure.Mqtt;

/// <summary>
/// Owns the broker session: connect with a Last Will, announce availability and discovery, keep the
/// health state current, re-announce when Home Assistant restarts, reconnect with backoff, and on
/// shutdown leave the entity OFF and unavailable rather than stale.
/// </summary>
public sealed class MqttLink : IHealthPublisher
{
    private readonly IMqttTransport _transport;
    private readonly IMqttCredentialSource _credentials;
    private readonly MqttConnectionState _state;
    private readonly IHealthStatus _health;
    private readonly MqttOptions _mqtt;
    private readonly HeartbeatOptions _heartbeat;
    private readonly AppVersion _version;
    private readonly TimeProvider _time;
    private readonly ILogger<MqttLink> _logger;

    private readonly Channel<Signal> _signals = Channel.CreateUnbounded<Signal>();

    // Serializes every health-state publish with shutdown, so no heartbeat can put ON back
    // after the final OFF.
    private readonly SemaphoreSlim _stateGate = new(1, 1);

    private readonly string _availabilityTopic;
    private readonly string _statusTopic;
    private bool _shuttingDown;

    public MqttLink(
        IMqttTransport transport,
        IMqttCredentialSource credentials,
        MqttConnectionState state,
        IHealthStatus health,
        IOptions<MqttOptions> mqtt,
        IOptions<HeartbeatOptions> heartbeat,
        AppVersion version,
        TimeProvider timeProvider,
        ILogger<MqttLink> logger)
    {
        _transport = transport;
        _credentials = credentials;
        _state = state;
        _health = health;
        _mqtt = mqtt.Value;
        _heartbeat = heartbeat.Value;
        _version = version;
        _time = timeProvider;
        _logger = logger;
        _availabilityTopic = MqttIdentity.AvailabilityTopic(_mqtt.BaseTopic);
        _statusTopic = MqttIdentity.HomeAssistantStatusTopic(_mqtt.DiscoveryPrefix);

        _transport.Disconnected += OnDisconnectedAsync;
        _transport.MessageReceived += OnMessageAsync;
    }

    private enum Signal
    {
        Disconnected,
        HomeAssistantOnline,
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_credentials.ConfigurationProblem is { } problem)
        {
            _logger.LogError("{Problem}", problem);
            return;
        }

        var backoff = new ExponentialBackoff(_mqtt.ReconnectMinDelay, _mqtt.ReconnectMaxDelay);
        while (!cancellationToken.IsCancellationRequested && !Volatile.Read(ref _shuttingDown))
        {
            try
            {
                await ConnectAndServeAsync(backoff, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The connection is left open on purpose: ShutdownAsync publishes OFF and offline on it.
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("MQTT connection failed: {Reason}", ex.Message);
            }

            if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _shuttingDown))
            {
                return;
            }

            var wait = backoff.Next();
            _state.Set(ConnectionState.Disconnected);
            try
            {
                await LeaveFailedSessionAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Reconnecting to the MQTT broker in {Delay}", wait);
                await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>Publishes the heartbeat state; dropped unless the session is fully announced.</summary>
    public async Task PublishHealthAsync(bool healthy, CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_shuttingDown || _state.Current != ConnectionState.Connected)
            {
                return;
            }

            await PublishStateAsync(healthy, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    /// <summary>
    /// State OFF, then availability "offline" (retained), then a clean disconnect. A clean disconnect
    /// suppresses the Last Will, so "offline" has to be published explicitly first.
    /// </summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _shuttingDown, true);
            if (_transport.IsConnected)
            {
                // Own deadline, well inside the container's stop grace: a broker that stops
                // acknowledging must not hold the process until it is killed, with the Home
                // Assistant socket, closed after this, never closed at all.
                using var deadline = new CancellationTokenSource(_mqtt.ShutdownTimeout, _time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
                try
                {
                    await PublishStateAsync(healthy: false, linked.Token).ConfigureAwait(false);
                    await _transport.PublishAsync(_availabilityTopic, MqttIdentity.AvailabilityOffline, retain: true, linked.Token).ConfigureAwait(false);
                    await _transport.DisconnectAsync(linked.Token).ConfigureAwait(false);
                    _logger.LogInformation("MQTT session closed; health entity left OFF and offline");
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("MQTT shutdown sequence did not complete within {Timeout}; the Last Will covers it", _mqtt.ShutdownTimeout);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The Last Will still covers this case once the broker notices the socket is gone.
                    _logger.LogWarning("MQTT shutdown sequence did not complete: {Reason}", ex.Message);
                }
            }

            _state.Set(ConnectionState.Disconnected);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task ConnectAndServeAsync(ExponentialBackoff backoff, CancellationToken cancellationToken)
    {
        _state.Set(ConnectionState.Connecting);
        while (_signals.Reader.TryRead(out _))
        {
            // Signals from a previous session are stale.
        }

        var credentials = await _credentials.GetAsync(cancellationToken).ConfigureAwait(false);
        var settings = new MqttConnectSettings
        {
            Credentials = credentials,
            ClientId = _mqtt.ClientId,
            WillTopic = _availabilityTopic,
            WillPayload = MqttIdentity.AvailabilityOffline,
            WillRetain = true,
            KeepAlivePeriod = _mqtt.KeepAlivePeriod,
        };
        await _transport.ConnectAsync(settings, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Connected to the MQTT broker at {Broker}", credentials);

        await _transport.PublishAsync(_availabilityTopic, MqttIdentity.AvailabilityOnline, retain: true, cancellationToken).ConfigureAwait(false);
        await PublishDiscoveryAsync(cancellationToken).ConfigureAwait(false);

        // Connected only once HA knows the entity, so no state is ever published ahead of its discovery.
        _state.Set(ConnectionState.Connected);
        await PublishHealthAsync(_health.IsHealthy, cancellationToken).ConfigureAwait(false);
        await _transport.SubscribeAsync(_statusTopic, cancellationToken).ConfigureAwait(false);

        // Not on CONNACK: a broker that accepts the connection but refuses what follows would
        // otherwise be retried at the minimum delay forever.
        backoff.Reset();

        while (true)
        {
            var signal = await _signals.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (signal == Signal.Disconnected)
            {
                if (!_transport.IsConnected)
                {
                    throw new InvalidOperationException("connection to the broker was lost");
                }

                continue;
            }

            // A restarted HA re-reads retained discovery but has lost the non-retained state;
            // announce both so the entity is current at once instead of after the next beat.
            _logger.LogInformation("Home Assistant came online; republishing discovery");
            await PublishDiscoveryAsync(cancellationToken).ConfigureAwait(false);
            await PublishHealthAsync(_health.IsHealthy, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A session can fail after CONNACK (a refused publish, a request timeout) with the client still
    /// connected, and a connected client refuses every further connect: without this the link never
    /// recovers. "offline" first, as at shutdown, so the retained "online" does not outlive the session.
    /// </summary>
    private async Task LeaveFailedSessionAsync(CancellationToken cancellationToken)
    {
        if (!_transport.IsConnected)
        {
            return;
        }

        try
        {
            await _transport.PublishAsync(_availabilityTopic, MqttIdentity.AvailabilityOffline, retain: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("Could not mark the failed MQTT session offline: {Reason}", ex.Message);
        }

        try
        {
            await _transport.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("Could not close the failed MQTT session: {Reason}", ex.Message);
        }
    }

    private Task PublishDiscoveryAsync(CancellationToken cancellationToken) =>
        _transport.PublishAsync(
            HealthEntityDiscovery.DiscoveryTopic(_mqtt),
            HealthEntityDiscovery.BuildConfig(_mqtt, _heartbeat, _version),
            retain: true,
            cancellationToken);

    // Not retained: a replayed ON after a restart would claim health nobody has checked.
    private Task PublishStateAsync(bool healthy, CancellationToken cancellationToken) =>
        _transport.PublishAsync(
            HealthEntityDiscovery.StateTopic(_mqtt),
            HealthEntityDiscovery.Payload(healthy),
            retain: false,
            cancellationToken);

    private Task OnDisconnectedAsync()
    {
        if (_state.Current == ConnectionState.Connected)
        {
            _state.Set(ConnectionState.Connecting);
        }

        _signals.Writer.TryWrite(Signal.Disconnected);
        return Task.CompletedTask;
    }

    private Task OnMessageAsync(string topic, string payload)
    {
        // Handled on the session loop, not here: publishing from inside the client's receive
        // callback can stall its own acknowledgement handling.
        if (topic == _statusTopic && payload == MqttIdentity.HomeAssistantOnline)
        {
            _signals.Writer.TryWrite(Signal.HomeAssistantOnline);
        }

        return Task.CompletedTask;
    }
}
