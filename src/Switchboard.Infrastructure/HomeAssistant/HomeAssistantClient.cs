using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Switchboard.Application.Configuration;
using Switchboard.Application.Connectivity;
using Switchboard.Application.Effector;
using Switchboard.Application.Options;
using Switchboard.Application.Resilience;
using Switchboard.Application.Signals;

namespace Switchboard.Infrastructure.HomeAssistant;

/// <summary>
/// In-house client for the Home Assistant WebSocket API: auth handshake, keepalive, reconnect with
/// backoff, and a <c>subscribe_entities</c> subscription filtered to the mapped entities, replaced
/// (and the old one ended with <c>unsubscribe_events</c>) when that set changes. The setup
/// wizard's discovery link adds read-only queries (<see cref="QueryAsync"/>), and the command path
/// (through its channel only) service calls on declared actuators (<see cref="CallServiceAsync"/>).
/// It can only send what <see cref="HomeAssistantCommandAllowList"/> permits, in exactly the
/// allowed shape, and it has no public send method at all.
/// </summary>
public sealed class HomeAssistantClient
{
    private const string AuthRequired = "auth_required";
    private const string AuthOk = "auth_ok";
    private const string AuthInvalid = "auth_invalid";
    private const string Pong = "pong";
    private const string Result = "result";
    private const string Event = "event";

    private readonly IWebSocketTransportFactory _transportFactory;
    private readonly HomeAssistantEndpoint _endpoint;
    private readonly HomeAssistantConnectionState _state;
    private readonly HomeAssistantOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<HomeAssistantClient> _logger;
    private readonly IEntitySubscriptionSink _subscriptions;
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _pendingPongs = new();
    private readonly ConcurrentDictionary<int, PendingQuery> _pendingQueries = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<CallOutcome>> _pendingCalls = new();
    private readonly ConcurrentDictionary<int, byte> _subscribeIds = new();

    // unsubscribe_events request id -> the subscription it ends.
    private readonly ConcurrentDictionary<int, int> _unsubscribes = new();

    // Wakes the subscription loop; one pending wake-up is enough, it re-reads the tracked set anyway.
    private readonly Channel<bool> _trackedChanged = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    // ClientWebSocket allows a single outstanding send.
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private IWebSocketTransport? _transport;
    private ActiveSubscription? _active;
    private Exception? _sessionFailure;
    private int _lastId;
    private volatile bool _stopping;

    private readonly EventHandler _onTrackedChanged;

    // Set once the session delivered what it is for (the subscription's initial state, or nothing to
    // subscribe); only then does the reconnect backoff start over.
    private volatile bool _sessionWorked;

    // Sessions that delivered their initial states; the command path re-asserts protective targets once per new one.
    private long _sessionGeneration;

    public HomeAssistantClient(
        IWebSocketTransportFactory transportFactory,
        HomeAssistantEndpoint endpoint,
        HomeAssistantConnectionState state,
        IOptions<HomeAssistantOptions> options,
        TimeProvider timeProvider,
        ILogger<HomeAssistantClient> logger,
        IEntitySubscriptionSink subscriptions)
    {
        _transportFactory = transportFactory;
        _endpoint = endpoint;
        _state = state;
        _options = options.Value;
        _time = timeProvider;
        _logger = logger;
        _subscriptions = subscriptions;
        _onTrackedChanged = (_, _) => _trackedChanged.Writer.TryWrite(true);
        _subscriptions.TrackedChanged += _onTrackedChanged;
    }

    /// <summary>Grows by one each time a session has delivered its initial states (the gate then re-asserts protective targets once).</summary>
    public long SessionGeneration => Interlocked.Read(ref _sessionGeneration);

    /// <summary>A session is up and has delivered its initial states: service calls can be sent.</summary>
    public bool Ready => _sessionWorked && _state.Current == ConnectionState.Connected && Volatile.Read(ref _transport) is not null;

    /// <summary>
    /// Stops following the sink's tracked set. A client whose sink outlives it (the discovery link
    /// opens a new client per session) calls this when it is closed, so the closed client is not kept
    /// alive by the sink's event.
    /// </summary>
    internal void Detach() => _subscriptions.TrackedChanged -= _onTrackedChanged;

    private enum SessionEnd
    {
        Closed,
        AuthRejected,
    }

    /// <summary>Connects and keeps reconnecting until cancelled or <see cref="CloseAsync"/> is called.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var backoff = new ExponentialBackoff(_options.ReconnectMinDelay, _options.ReconnectMaxDelay);
        while (!cancellationToken.IsCancellationRequested && !_stopping)
        {
            TimeSpan wait;
            try
            {
                var end = await RunSessionAsync(backoff, cancellationToken).ConfigureAwait(false);
                wait = end == SessionEnd.AuthRejected ? _options.ReconnectMaxDelay : backoff.Next();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                wait = backoff.Next();
                _logger.LogWarning("Home Assistant connection to {Uri} failed: {Reason}", _endpoint.DisplayUri, ex.Message);
            }

            if (_stopping || cancellationToken.IsCancellationRequested)
            {
                break;
            }

            _state.Set(ConnectionState.Disconnected);
            _logger.LogInformation("Reconnecting to Home Assistant in {Delay}", wait);
            try
            {
                await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        _state.Set(ConnectionState.Disconnected);
    }

    /// <summary>
    /// Starts a clean close of the current connection and stops reconnecting. Call before cancelling
    /// <see cref="RunAsync"/>: cancelling a pending receive aborts the socket instead of closing it.
    /// </summary>
    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        var transport = Volatile.Read(ref _transport);
        if (transport is null)
        {
            return;
        }

        try
        {
            await transport.CloseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort: the socket may already be gone, which is the end state we want anyway.
            _logger.LogDebug("Closing the Home Assistant connection did not complete cleanly: {Reason}", ex.Message);
        }
    }

    /// <summary>
    /// The only way a command reaches Home Assistant. The allow-list check runs before the socket is
    /// touched, so a refused type sends nothing.
    /// </summary>
    internal async Task SendCommandAsync(string type, CancellationToken cancellationToken)
    {
        HomeAssistantCommandAllowList.EnsureAllowed(type);
        var transport = Volatile.Read(ref _transport);
        if (transport is null || _state.Current != ConnectionState.Connected)
        {
            throw new InvalidOperationException("Home Assistant is not connected.");
        }

        await SendCommandAsync(transport, type, NextId(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one read-only query and returns its result. Refused before the socket is touched unless
    /// the type is one of <see cref="HomeAssistantCommandAllowList.ReadOnlyQueries"/>; the answer is
    /// matched by id and timed out on the injected clock; <c>success: false</c> throws
    /// <see cref="HomeAssistantQueryFailedException"/>, and a session that ends fails the query.
    /// </summary>
    internal async Task<JsonElement> QueryAsync(string type, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (type is null || !HomeAssistantCommandAllowList.ReadOnlyQueries.Contains(type))
        {
            throw new HomeAssistantCommandRefusedException(type);
        }

        var transport = Volatile.Read(ref _transport);
        if (transport is null || _state.Current != ConnectionState.Connected)
        {
            throw new InvalidOperationException("Home Assistant is not connected.");
        }

        var id = NextId();
        var result = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingQueries[id] = new PendingQuery(type, result);
        try
        {
            // Armed before the query leaves, like a ping's timeout.
            var answered = result.Task.WaitAsync(timeout, _time, cancellationToken);
            await SendCommandAsync(transport, type, id, cancellationToken).ConfigureAwait(false);
            return await answered.ConfigureAwait(false);
        }
        finally
        {
            _pendingQueries.TryRemove(id, out _);
        }
    }

    /// <summary>A read-only query waiting for its result.</summary>
    private sealed record PendingQuery(string Type, TaskCompletionSource<JsonElement> Result);

    /// <summary>
    /// Sends one service call for one declared actuator and returns its outcome; only the command
    /// path's channel calls this. The serialized call is checked by
    /// <see cref="HomeAssistantCommandAllowList.EnsureCallServiceAllowed"/> against
    /// <paramref name="isDeclared"/> (the configuration loaded now) before the socket is touched; a
    /// refusal throws <see cref="HomeAssistantCommandRefusedException"/>. The answer is matched by
    /// id and timed out on the injected clock; success:false is a failure with Home Assistant's
    /// message. A session that ends first is no answer, like a timeout (the call may or may not have
    /// run): the device's reported state decides, never a mismatch counted against it.
    /// </summary>
    internal async Task<CallOutcome> CallServiceAsync(ActuatorCall call, ActuatorDeclaredCheck isDeclared, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        var transport = Volatile.Read(ref _transport);
        if (transport is null || !Ready)
        {
            return new CallOutcome(CommandOutcome.NotSent, "Home Assistant is not connected");
        }

        var id = NextId();
        var payload = CallServicePayload.Build(id, call);
        HomeAssistantCommandAllowList.EnsureCallServiceAllowed(payload.Span, isDeclared);
        var answer = new TaskCompletionSource<CallOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCalls[id] = answer;
        try
        {
            // Armed before the call leaves, like a ping's timeout.
            var answered = answer.Task.WaitAsync(timeout, _time, cancellationToken);
            await SendFrameAsync(transport, payload, cancellationToken).ConfigureAwait(false);
            return await answered.ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new CallOutcome(CommandOutcome.TimedOut, $"no answer within {timeout}");
        }
        catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            return new CallOutcome(CommandOutcome.TimedOut, $"link closed ({ex.Message})");
        }
        finally
        {
            _pendingCalls.TryRemove(id, out _);
        }
    }

    /// <summary>The subscription currently routed to the sink; events of superseded ones are ignored.</summary>
    private sealed record ActiveSubscription(int Id, SubscribeEntitiesParser Parser);

    private async Task<SessionEnd> RunSessionAsync(ExponentialBackoff backoff, CancellationToken cancellationToken)
    {
        _state.Set(ConnectionState.Connecting);
        _sessionWorked = false;
        var transport = _transportFactory.Create();
        Volatile.Write(ref _transport, transport);
        Interlocked.Exchange(ref _lastId, 0);
        try
        {
            // A peer that accepts TCP but never answers would otherwise hold us in "connecting"
            // forever; the handshake gets the same answer deadline as a ping.
            await transport.ConnectAsync(_endpoint.WebSocketUri, _endpoint.AccessToken, cancellationToken)
                .WaitAsync(_options.PongTimeout, _time, cancellationToken).ConfigureAwait(false);

            var hello = await ReceiveMessageAsync(transport, cancellationToken).ConfigureAwait(false);
            if (TypeOf(hello) != AuthRequired)
            {
                throw new HomeAssistantProtocolException($"expected '{AuthRequired}', got '{TypeOf(hello)}'");
            }

            await SendAuthAsync(transport, cancellationToken).ConfigureAwait(false);

            var reply = await ReceiveMessageAsync(transport, cancellationToken).ConfigureAwait(false);
            switch (TypeOf(reply))
            {
                case AuthOk:
                    break;
                case AuthInvalid:
                    _logger.LogError(
                        "Home Assistant rejected the access token ({Message}); retrying in {Delay}",
                        MessageOf(reply), _options.ReconnectMaxDelay);
                    return SessionEnd.AuthRejected;
                default:
                    throw new HomeAssistantProtocolException($"unexpected auth reply '{TypeOf(reply)}'");
            }

            try
            {
                await ServeAsync(transport, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _sessionFailure = ex;
                throw;
            }

            if (_stopping)
            {
                _logger.LogInformation("Home Assistant connection closed");
            }
            else
            {
                _logger.LogWarning("Home Assistant closed the connection");
            }

            return SessionEnd.Closed;
        }
        finally
        {
            // Not on auth_ok: a subscription Home Assistant refuses (an entity ID it rejects) would
            // otherwise reconnect every ReconnectMinDelay forever.
            if (_sessionWorked)
            {
                backoff.Reset();
            }

            foreach (var pending in _pendingPongs.Values)
            {
                pending.TrySetCanceled(CancellationToken.None);
            }

            _pendingPongs.Clear();

            // A query must never wait for an answer that cannot come any more.
            foreach (var query in _pendingQueries.Values)
            {
                query.Result.TrySetException(_sessionFailure is WebSocketMessageTooLargeException tooLarge
                    ? tooLarge
                    : new HomeAssistantQueryFailedException(query.Type, "the connection ended before the answer arrived"));
            }

            _pendingQueries.Clear();

            // A service call whose answer cannot come any more: it may or may not have run, so it
            // counts as no answer (the device's reported state decides), never as an error.
            foreach (var call in _pendingCalls.Values)
            {
                call.TrySetResult(new CallOutcome(CommandOutcome.TimedOut, "link closed before the answer arrived"));
            }

            _pendingCalls.Clear();
            _sessionFailure = null;
            _subscribeIds.Clear();
            _unsubscribes.Clear();
            Volatile.Write(ref _active, null);
            Interlocked.CompareExchange(ref _transport, null, transport);
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ServeAsync(IWebSocketTransport transport, CancellationToken cancellationToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Started before the state flips to Connected so its timer is armed by the time anyone
        // reacts to "connected".
        var keepAlive = KeepAliveAsync(transport, session.Token);
        _state.Set(ConnectionState.Connected);
        _logger.LogInformation("Connected to Home Assistant at {Uri}", _endpoint.DisplayUri);
        _subscriptions.SessionStarted();
        try
        {
            var receive = ReceiveLoopAsync(transport, session.Token);
            var subscribe = SubscriptionLoopAsync(transport, session.Token);

            var finished = await Task.WhenAny(keepAlive, receive, subscribe).ConfigureAwait(false);
            await session.CancelAsync().ConfigureAwait(false);
            transport.Abort();
            try
            {
                await Task.WhenAll(keepAlive, receive, subscribe).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Observed below through the task that ended the session; the others only
                // failed because the session was torn down.
            }

            await finished.ConfigureAwait(false);
        }
        finally
        {
            _subscriptions.SessionEnded();
        }
    }

    /// <summary>
    /// Subscribes to exactly the tracked entities, and again whenever that set changes. Home
    /// Assistant gets an explicit entity filter every time: without one, subscribe_entities streams
    /// every entity in the house. The superseded subscription is ended with unsubscribe_events after
    /// its replacement is in place (or at once when nothing is tracked any more), so nothing keeps
    /// streaming entities the configuration no longer maps; until HA confirms, its events are ignored.
    /// </summary>
    private async Task SubscriptionLoopAsync(IWebSocketTransport transport, CancellationToken cancellationToken)
    {
        IReadOnlyList<EntityId>? subscribed = null;
        IReadOnlyDictionary<EntityId, SeriesSpec>? subscribedSpecs = null;
        IReadOnlyDictionary<EntityId, ImmutableArray<string>>? subscribedAttributes = null;
        while (true)
        {
            var tracked = _subscriptions.Tracked;
            var specs = _subscriptions.SeriesSpecs;
            var attributes = _subscriptions.TimestampAttributes;
            if (subscribed is null || !tracked.SequenceEqual(subscribed) || !HomeAssistantSignalSource.SameSpecs(specs, subscribedSpecs!) || !HomeAssistantSignalSource.SameAttributes(attributes, subscribedAttributes!))
            {
                var superseded = Volatile.Read(ref _active);
                if (tracked.Count == 0)
                {
                    Volatile.Write(ref _active, null);
                    MarkWorked();
                    _subscriptions.SessionReady();
                }
                else
                {
                    await SubscribeEntitiesAsync(transport, tracked, specs, attributes, cancellationToken).ConfigureAwait(false);
                }

                if (superseded is not null)
                {
                    await UnsubscribeAsync(transport, superseded.Id, cancellationToken).ConfigureAwait(false);
                }

                subscribed = tracked;
                subscribedSpecs = specs;
                subscribedAttributes = attributes;
            }

            await _trackedChanged.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SubscribeEntitiesAsync(IWebSocketTransport transport, IReadOnlyList<EntityId> entities, IReadOnlyDictionary<EntityId, SeriesSpec> series, IReadOnlyDictionary<EntityId, ImmutableArray<string>> attributes, CancellationToken cancellationToken)
    {
        var id = NextId();
        _subscribeIds[id] = 0;

        // Routed before the request leaves, so the initial state cannot arrive ahead of its parser.
        Volatile.Write(ref _active, new ActiveSubscription(id, new SubscribeEntitiesParser(entities, series, attributes, _time.LocalTimeZone)));
        var payload = Serialize(writer =>
        {
            writer.WriteNumber("id", id);
            writer.WriteString("type", HomeAssistantCommandAllowList.SubscribeEntities);
            writer.WriteStartArray("entity_ids");
            foreach (var entity in entities)
            {
                writer.WriteStringValue(entity.Value);
            }

            writer.WriteEndArray();
        });
        await SendCommandPayloadAsync(transport, payload, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Subscribed to {Count} Home Assistant entities", entities.Count);
    }

    private async Task UnsubscribeAsync(IWebSocketTransport transport, int subscription, CancellationToken cancellationToken)
    {
        var id = NextId();
        var payload = Serialize(writer =>
        {
            writer.WriteNumber("id", id);
            writer.WriteString("type", HomeAssistantCommandAllowList.UnsubscribeEvents);
            writer.WriteNumber("subscription", subscription);
        });

        // Checked by the common gate while the subscription still counts as ours, then forgotten,
        // so the same id can never pass the check a second time.
        _unsubscribes[id] = subscription;
        await SendCommandPayloadAsync(transport, payload, cancellationToken).ConfigureAwait(false);
        _subscribeIds.TryRemove(subscription, out _);
    }

    /// <summary>One of this session's subscriptions, and not the one in use.</summary>
    private bool IsOwnSubscription(int subscription) =>
        _subscribeIds.ContainsKey(subscription) && Volatile.Read(ref _active)?.Id != subscription;

    private async Task KeepAliveAsync(IWebSocketTransport transport, CancellationToken cancellationToken)
    {
        // A PeriodicTimer is armed once here; ticks are not lost while a ping is in flight.
        using var timer = new PeriodicTimer(_options.PingInterval, _time);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = NextId();
            var pong = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingPongs[id] = pong;
            try
            {
                // The timeout is armed before the ping leaves, so a fast peer or a virtual clock
                // cannot answer "before" the wait begins.
                var answered = pong.Task.WaitAsync(_options.PongTimeout, _time, cancellationToken);
                await SendCommandAsync(transport, HomeAssistantCommandAllowList.Ping, id, cancellationToken).ConfigureAwait(false);
                await answered.ConfigureAwait(false);
            }
            catch (TimeoutException) when (pong.Task.IsCompletedSuccessfully)
            {
                // The pong arrived exactly at the deadline; the link answered.
            }
            catch (TimeoutException)
            {
                // TCP can look alive long after HA stopped answering; only a pong proves it is there.
                _logger.LogWarning("Home Assistant did not answer a ping within {Timeout}; dropping the connection", _options.PongTimeout);
                throw new HomeAssistantProtocolException("keepalive timed out");
            }
            finally
            {
                _pendingPongs.TryRemove(id, out _);
            }
        }
    }

    private async Task ReceiveLoopAsync(IWebSocketTransport transport, CancellationToken cancellationToken)
    {
        while (true)
        {
            var text = await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (text is null)
            {
                return;
            }

            Dispatch(text);
        }
    }

    private void Dispatch(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in root.EnumerateArray())
                {
                    DispatchMessage(element);
                }
            }
            else
            {
                DispatchMessage(root);
            }
        }
        catch (JsonException)
        {
            _logger.LogDebug("Ignored a Home Assistant frame that is not JSON");
        }
    }

    private void DispatchMessage(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var id = message.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var parsed) ? parsed : (int?)null;
        if (id is not { } messageId)
        {
            return;
        }

        if (type.ValueEquals(Pong) && _pendingPongs.TryGetValue(messageId, out var pending))
        {
            pending.TrySetResult();
        }
        else if (type.ValueEquals(Result) && _pendingQueries.TryRemove(messageId, out var query))
        {
            if (message.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True && message.TryGetProperty("result", out var answer))
            {
                query.Result.TrySetResult(answer.Clone());
            }
            else
            {
                query.Result.TrySetException(new HomeAssistantQueryFailedException(query.Type, ErrorMessageOf(message) ?? "no reason given"));
            }
        }
        else if (type.ValueEquals(Result) && _pendingCalls.TryRemove(messageId, out var call))
        {
            call.TrySetResult(message.TryGetProperty("success", out var called) && called.ValueKind == JsonValueKind.True
                ? new CallOutcome(CommandOutcome.Acknowledged, null)
                : new CallOutcome(ErrorCodeOf(message) == "service_validation_error" ? CommandOutcome.Rejected : CommandOutcome.Failed, ErrorMessageOf(message) ?? "Home Assistant refused the call without a reason"));
        }
        else if (type.ValueEquals(Result) && _unsubscribes.TryRemove(messageId, out var ended))
        {
            if (message.TryGetProperty("success", out var done) && done.ValueKind == JsonValueKind.True)
            {
                _logger.LogInformation("Home Assistant ended superseded subscription {Subscription}", ended);
            }
            else
            {
                // Its events are ignored anyway; the next reconnect ends it for good.
                _logger.LogWarning("Home Assistant did not end superseded subscription {Subscription}: {Reason}", ended, ErrorMessageOf(message) ?? "no reason given");
            }
        }
        else if (type.ValueEquals(Result) && _subscribeIds.ContainsKey(messageId)
                 && !(message.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True))
        {
            // Without the subscription no signal is fresh; start over rather than sit connected and blind.
            throw new HomeAssistantProtocolException($"subscribe_entities was refused: {ErrorMessageOf(message) ?? "no reason given"}");
        }
        else if (type.ValueEquals(Event) && Volatile.Read(ref _active) is { } active && active.Id == messageId
                 && message.TryGetProperty("event", out var body))
        {
            var batch = active.Parser.Parse(body, out var problem);
            if (batch is null)
            {
                _logger.LogWarning("Ignored a malformed Home Assistant state event: {Problem}", problem);
                return;
            }

            // The batch first: the command path's re-assert must see the states this session brought.
            _subscriptions.Deliver(batch);
            MarkWorked();
        }
    }

    private async Task SendAuthAsync(IWebSocketTransport transport, CancellationToken cancellationToken)
    {
        var payload = Serialize(writer =>
        {
            writer.WriteString("type", "auth");
            writer.WriteString("access_token", _endpoint.AccessToken);
        });
        await SendFrameAsync(transport, payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendCommandAsync(IWebSocketTransport transport, string type, int id, CancellationToken cancellationToken)
    {
        // Checked again here so internal callers go through the same gate.
        HomeAssistantCommandAllowList.EnsureAllowed(type);
        var payload = Serialize(writer =>
        {
            writer.WriteNumber("id", id);
            writer.WriteString("type", type);
        });
        await SendCommandPayloadAsync(transport, payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every command passes here: the serialized bytes are checked against the allowed shape before the socket sees them.</summary>
    private async Task SendCommandPayloadAsync(IWebSocketTransport transport, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        HomeAssistantCommandAllowList.EnsurePayloadAllowed(payload.Span, IsOwnSubscription);
        await SendFrameAsync(transport, payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendFrameAsync(IWebSocketTransport transport, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await transport.SendAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private int NextId() => Interlocked.Increment(ref _lastId);

    private void MarkWorked()
    {
        if (!_sessionWorked)
        {
            _sessionWorked = true;
            Interlocked.Increment(ref _sessionGeneration);
        }
    }

    private async Task<JsonElement> ReceiveMessageAsync(IWebSocketTransport transport, CancellationToken cancellationToken)
    {
        var text = await transport.ReceiveAsync(cancellationToken)
                .WaitAsync(_options.PongTimeout, _time, cancellationToken).ConfigureAwait(false)
            ?? throw new HomeAssistantProtocolException("connection closed during the handshake");
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new HomeAssistantProtocolException("handshake frame is not JSON");
        }
    }

    private static string? TypeOf(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object
        && message.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            ? type.GetString()
            : null;

    private static string? MessageOf(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object
        && message.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString()
            : null;

    private static string? ErrorCodeOf(JsonElement message) =>
        message.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
        && error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
            ? code.GetString()
            : null;

    private static string? ErrorMessageOf(JsonElement message) =>
        message.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
        && error.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString()
            : null;

    private static ReadOnlyMemory<byte> Serialize(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return buffer.WrittenMemory;
    }
}

public sealed class HomeAssistantProtocolException(string message) : Exception(message);

/// <summary>Home Assistant answered a read-only query with <c>success: false</c>, or the connection ended first.</summary>
public sealed class HomeAssistantQueryFailedException(string type, string reason) : Exception($"Home Assistant query {type} failed: {reason}.")
{
    public string QueryType { get; } = type;
}
