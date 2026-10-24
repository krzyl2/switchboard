using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Switchboard.Application;
using Switchboard.Application.Connectivity;
using Switchboard.Application.Health;
using Switchboard.Application.Options;
using Switchboard.Application.Resilience;
using Switchboard.Application.State;

namespace Switchboard.Infrastructure.Mqtt;

/// <summary>
/// Owns the broker session: connect with a Last Will, announce availability and discovery, keep the
/// health state current, re-announce when Home Assistant restarts, reconnect with backoff, and on
/// shutdown leave the entity OFF and unavailable rather than stale.
/// <para>
/// The other entities come from <see cref="IMqttEntitySource"/>s (the guard's): discovery is
/// retained and announced with the session, states are not retained and are published on connect,
/// when Home Assistant comes online, whenever they change and all again on every heartbeat (a state
/// that reaches HA before its new entity's discovery is dropped). An object a valid configuration no longer declares is retracted (an empty retained
/// discovery payload), including objects announced before a restart (the ledger in state.json).
/// Command topics are subscribed only as the topic guard allows; a retained command is ignored, so
/// a stale message in the broker can never engage anything.
/// </para>
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
    private readonly IReadOnlyList<IMqttEntitySource> _sources;
    private readonly IDiscoveryLedger _ledger;
    private readonly TimeProvider _time;
    private readonly ILogger<MqttLink> _logger;

    private readonly Channel<Signal> _signals = Channel.CreateUnbounded<Signal>();

    // Serializes every health-state publish with shutdown, so no heartbeat can put ON back
    // after the final OFF.
    private readonly SemaphoreSlim _stateGate = new(1, 1);

    private readonly string _availabilityTopic;
    private readonly string _statusTopic;

    // Per session, touched by the session loop only: what discovery and state were last sent.
    private readonly Dictionary<string, string> _announced = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _published = new(StringComparer.Ordinal);
    private int _entitiesDirty;
    private bool _shuttingDown;

    public MqttLink(
        IMqttTransport transport,
        IMqttCredentialSource credentials,
        MqttConnectionState state,
        IHealthStatus health,
        IOptions<MqttOptions> mqtt,
        IOptions<HeartbeatOptions> heartbeat,
        AppVersion version,
        IEnumerable<IMqttEntitySource> entitySources,
        IDiscoveryLedger ledger,
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
        _sources = [.. entitySources];
        _ledger = ledger;
        _time = timeProvider;
        _logger = logger;
        _availabilityTopic = MqttIdentity.AvailabilityTopic(_mqtt.BaseTopic);
        _statusTopic = MqttIdentity.HomeAssistantStatusTopic(_mqtt.DiscoveryPrefix);

        _transport.Disconnected += OnDisconnectedAsync;
        _transport.MessageReceived += OnMessageAsync;
        foreach (var source in _sources)
        {
            source.Changed += OnEntitiesChanged;
        }
    }

    private enum Signal
    {
        Disconnected,
        HomeAssistantOnline,
        EntitiesChanged,
        CommandHandled,
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

    /// <summary>
    /// Publishes the heartbeat state, then every entity state again; dropped unless the session is
    /// fully announced. States are not retained and Home Assistant drops one that reaches an entity
    /// before its discovery is processed, so a value that never changes would otherwise stay unknown.
    /// </summary>
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
            await PublishEntityStatesUnderGateAsync(force: true, cancellationToken).ConfigureAwait(false);
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

        // The session announces everything anyway; a change noted before it must not block the next one.
        Interlocked.Exchange(ref _entitiesDirty, 0);
        _announced.Clear();
        _published.Clear();

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
        await AnnounceEntitiesAsync(force: true, cancellationToken).ConfigureAwait(false);

        // Connected only once HA knows the entity, so no state is ever published ahead of its discovery.
        _state.Set(ConnectionState.Connected);
        await PublishHealthAsync(_health.IsHealthy, cancellationToken).ConfigureAwait(false);
        await PublishEntityStatesAsync(force: true, cancellationToken).ConfigureAwait(false);
        await _transport.SubscribeAsync(_statusTopic, cancellationToken).ConfigureAwait(false);
        foreach (var topic in _sources.SelectMany(s => s.CommandTopics))
        {
            await _transport.SubscribeAsync(topic, cancellationToken).ConfigureAwait(false);
        }

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

            switch (signal)
            {
                case Signal.HomeAssistantOnline:
                    // A restarted HA re-reads retained discovery but has lost the non-retained state;
                    // announce both so the entity is current at once instead of after the next beat.
                    _logger.LogInformation("Home Assistant came online; republishing discovery");
                    await PublishDiscoveryAsync(cancellationToken).ConfigureAwait(false);
                    await AnnounceEntitiesAsync(force: true, cancellationToken).ConfigureAwait(false);
                    await PublishHealthAsync(_health.IsHealthy, cancellationToken).ConfigureAwait(false);
                    await PublishEntityStatesAsync(force: true, cancellationToken).ConfigureAwait(false);
                    break;
                case Signal.EntitiesChanged:
                    Interlocked.Exchange(ref _entitiesDirty, 0);
                    await AnnounceEntitiesAsync(force: false, cancellationToken).ConfigureAwait(false);
                    await PublishEntityStatesAsync(force: false, cancellationToken).ConfigureAwait(false);
                    break;
                case Signal.CommandHandled:
                    // Home Assistant waits for the state after a command; send it even when unchanged.
                    await PublishEntityStatesAsync(force: true, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>
    /// Discovery for every entity of every source (all of them when <paramref name="force"/>, else
    /// only new or changed configs), then retraction: when every source is authoritative, each
    /// object announced earlier (this session or, through the ledger, before a restart) and absent
    /// now gets an empty retained config. While a source is not authoritative nothing is retracted
    /// and the ledger only grows.
    /// </summary>
    private async Task AnnounceEntitiesAsync(bool force, CancellationToken cancellationToken)
    {
        if (_sources.Count == 0)
        {
            return;
        }

        var sets = _sources.Select(s => s.Current()).ToList();
        var current = sets.SelectMany(s => s.Entities).ToList();
        foreach (var entity in current)
        {
            if (force || !_announced.TryGetValue(entity.Key, out var config) || config != entity.Config)
            {
                await _transport.PublishAsync(DiscoveryTopic(entity.Component, entity.ObjectId), entity.Config, retain: true, cancellationToken).ConfigureAwait(false);
                _announced[entity.Key] = entity.Config;
            }
        }

        var keys = current.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        if (!sets.All(s => s.Authoritative))
        {
            _ledger.Replace(_ledger.Discovered.Union(keys));
            return;
        }

        foreach (var stale in _ledger.Discovered.Union(_announced.Keys).Where(k => !keys.Contains(k)).ToList())
        {
            var parts = stale.Split('/');
            await _transport.PublishAsync(DiscoveryTopic(parts[0], parts[1]), string.Empty, retain: true, cancellationToken).ConfigureAwait(false);
            _announced.Remove(stale);
            _published.Remove(MqttIdentity.StateTopic(_mqtt.BaseTopic, parts[1]));
            _published.Remove(GuardEntities.AttributesTopic(_mqtt.BaseTopic, parts[1]));
            _logger.LogInformation("Retracted MQTT entity {Entity}: the configuration no longer declares it", stale);
        }

        _ledger.Replace(keys);
    }

    /// <summary>States (and attributes) of every source's entities; unchanged payloads are skipped unless <paramref name="force"/>.</summary>
    private async Task PublishEntityStatesAsync(bool force, CancellationToken cancellationToken)
    {
        if (_sources.Count == 0)
        {
            return;
        }

        // Under the same gate as the health state, so nothing is published after the shutdown sequence.
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_shuttingDown || _state.Current != ConnectionState.Connected)
            {
                return;
            }

            await PublishEntityStatesUnderGateAsync(force, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task PublishEntityStatesUnderGateAsync(bool force, CancellationToken cancellationToken)
    {
        foreach (var entity in _sources.SelectMany(s => s.Current().Entities))
        {
            await PublishIfChangedAsync(MqttIdentity.StateTopic(_mqtt.BaseTopic, entity.ObjectId), entity.State, force, cancellationToken).ConfigureAwait(false);
            if (entity.Attributes is { } attributes)
            {
                await PublishIfChangedAsync(GuardEntities.AttributesTopic(_mqtt.BaseTopic, entity.ObjectId), attributes, force, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Not retained: a replayed state after a restart would claim what nobody has evaluated.
    private async Task PublishIfChangedAsync(string topic, string payload, bool force, CancellationToken cancellationToken)
    {
        if (!force && _published.TryGetValue(topic, out var last) && last == payload)
        {
            return;
        }

        await _transport.PublishAsync(topic, payload, retain: false, cancellationToken).ConfigureAwait(false);
        _published[topic] = payload;
    }

    private string DiscoveryTopic(string component, string objectId) =>
        MqttIdentity.DiscoveryTopic(_mqtt.DiscoveryPrefix, component, _mqtt.BaseTopic, objectId);

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

    private Task OnMessageAsync(MqttInboundMessage message)
    {
        // Handled on the session loop, not here: publishing from inside the client's receive
        // callback can stall its own acknowledgement handling.
        if (message.Topic == _statusTopic)
        {
            if (message.Payload == MqttIdentity.HomeAssistantOnline)
            {
                _signals.Writer.TryWrite(Signal.HomeAssistantOnline);
            }

            return Task.CompletedTask;
        }

        foreach (var source in _sources.Where(s => s.CommandTopics.Contains(message.Topic, StringComparer.Ordinal)))
        {
            if (message.Retained)
            {
                // A command left in the broker (by anyone) must never act on its own at the next connect.
                _logger.LogWarning("Ignored a retained message on command topic {Topic}", message.Topic);
                continue;
            }

            // Commands only change memory and wake the guard; the resulting state follows on the session loop.
            source.HandleCommand(message.Topic, message.Payload);
            _signals.Writer.TryWrite(Signal.CommandHandled);
        }

        return Task.CompletedTask;
    }

    private void OnEntitiesChanged(object? sender, EventArgs e)
    {
        // Raised on the guard's thread after every evaluation: note it, coalesce, never publish here.
        if (Interlocked.Exchange(ref _entitiesDirty, 1) == 0)
        {
            _signals.Writer.TryWrite(Signal.EntitiesChanged);
        }
    }
}
