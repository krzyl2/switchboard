using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Switchboard.Application.Connectivity;
using Switchboard.Application.Options;
using Switchboard.Application.Resilience;

namespace Switchboard.Infrastructure.HomeAssistant;

/// <summary>
/// In-house client for the Home Assistant WebSocket API: auth handshake, keepalive, reconnect with
/// backoff. It can only send what <see cref="HomeAssistantCommandAllowList"/> permits, and it has no
/// public send method at all.
/// </summary>
public sealed class HomeAssistantClient
{
    private const string AuthRequired = "auth_required";
    private const string AuthOk = "auth_ok";
    private const string AuthInvalid = "auth_invalid";
    private const string Pong = "pong";

    private readonly IWebSocketTransportFactory _transportFactory;
    private readonly HomeAssistantEndpoint _endpoint;
    private readonly HomeAssistantConnectionState _state;
    private readonly HomeAssistantOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<HomeAssistantClient> _logger;
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _pendingPongs = new();

    // ClientWebSocket allows a single outstanding send.
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private IWebSocketTransport? _transport;
    private int _lastId;
    private volatile bool _stopping;

    public HomeAssistantClient(
        IWebSocketTransportFactory transportFactory,
        HomeAssistantEndpoint endpoint,
        HomeAssistantConnectionState state,
        IOptions<HomeAssistantOptions> options,
        TimeProvider timeProvider,
        ILogger<HomeAssistantClient> logger)
    {
        _transportFactory = transportFactory;
        _endpoint = endpoint;
        _state = state;
        _options = options.Value;
        _time = timeProvider;
        _logger = logger;
    }

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

    private async Task<SessionEnd> RunSessionAsync(ExponentialBackoff backoff, CancellationToken cancellationToken)
    {
        _state.Set(ConnectionState.Connecting);
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

            backoff.Reset();
            await ServeAsync(transport, cancellationToken).ConfigureAwait(false);
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
            foreach (var pending in _pendingPongs.Values)
            {
                pending.TrySetCanceled(CancellationToken.None);
            }

            _pendingPongs.Clear();
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
        var receive = ReceiveLoopAsync(transport, session.Token);

        var finished = await Task.WhenAny(keepAlive, receive).ConfigureAwait(false);
        await session.CancelAsync().ConfigureAwait(false);
        transport.Abort();
        try
        {
            await Task.WhenAll(keepAlive, receive).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Observed below through the task that ended the session; the other one only
            // failed because the session was torn down.
        }

        await finished.ConfigureAwait(false);
    }

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

        if (type.ValueEquals(Pong)
            && message.TryGetProperty("id", out var id) && id.TryGetInt32(out var pongId)
            && _pendingPongs.TryGetValue(pongId, out var pending))
        {
            pending.TrySetResult();
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
