using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Switchboard.Infrastructure.HomeAssistant;

namespace Switchboard.Tests.Support;

/// <summary>
/// Test double for the HA socket: the test pushes inbound frames and reads what the client sent.
/// <see cref="PushAndWaitAsync"/> returns only once the client has finished handling the frame and
/// asked for the next one, so the test never advances the clock ahead of the client.
/// </summary>
internal sealed class ScriptedWebSocketTransport : IWebSocketTransport
{
    private readonly Channel<string?> _inbound = Channel.CreateUnbounded<string?>();
    private readonly Channel<string> _outbound = Channel.CreateUnbounded<string>();
    private readonly CancellationTokenSource _aborted = new();
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly List<string> _sent = [];
    private readonly List<(int Calls, TaskCompletionSource Signal)> _receiveWaiters = [];
    private int _receiveCalls;
    private int _framesPushed;

    public ScriptedWebSocketTransport(bool failConnect = false) => FailConnect = failConnect;

    public bool FailConnect { get; }

    public Uri? ConnectedUri { get; private set; }

    public string? ConnectedToken { get; private set; }

    public bool IsAborted => _aborted.IsCancellationRequested;

    public bool CloseRequested { get; private set; }

    public bool IsDisposed { get; private set; }

    /// <summary>When set, every send fails like a socket Home Assistant just reset, while receiving still looks open.</summary>
    public bool FailSends { get; set; }

    public Task Connected => _connected.Task.Soon();

    public IReadOnlyList<string> Sent
    {
        get
        {
            lock (_gate)
            {
                return [.. _sent];
            }
        }
    }

    public Task ConnectAsync(Uri uri, string accessToken, CancellationToken cancellationToken)
    {
        ConnectedUri = uri;
        ConnectedToken = accessToken;
        _connected.TrySetResult();
        return FailConnect
            ? Task.FromException(new WebSocketException(WebSocketError.Faulted, "scripted connect failure"))
            : Task.CompletedTask;
    }

    public Task SendAsync(ReadOnlyMemory<byte> utf8Json, CancellationToken cancellationToken)
    {
        if (IsAborted)
        {
            throw new WebSocketException(WebSocketError.InvalidState, "aborted");
        }

        if (FailSends)
        {
            throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "scripted reset");
        }

        var text = Encoding.UTF8.GetString(utf8Json.Span);
        lock (_gate)
        {
            _sent.Add(text);
        }

        _outbound.Writer.TryWrite(text);
        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        SignalReceiveCall();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _aborted.Token);
        try
        {
            return await _inbound.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (IsAborted && !cancellationToken.IsCancellationRequested)
        {
            throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "aborted");
        }
    }

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        CloseRequested = true;

        // A well-behaved peer answers our close with its own.
        _inbound.Writer.TryWrite(null);
        return Task.CompletedTask;
    }

    public void Abort() => _aborted.Cancel();

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>Delivers a frame without waiting (use when the client will not read again afterwards).</summary>
    public void Push(string? frame)
    {
        Interlocked.Increment(ref _framesPushed);
        _inbound.Writer.TryWrite(frame);
    }

    /// <summary>Delivers a frame and waits until the client has handled it and is reading again.</summary>
    public Task PushAndWaitAsync(string frame)
    {
        var pushed = Interlocked.Increment(ref _framesPushed);
        var waiter = WaitForReceiveCallsAsync(pushed + 1);
        _inbound.Writer.TryWrite(frame);
        return waiter;
    }

    public async Task<string> NextSentAsync() => await _outbound.Reader.ReadAsync().Soon();

    private Task WaitForReceiveCallsAsync(int calls)
    {
        lock (_gate)
        {
            if (_receiveCalls >= calls)
            {
                return Task.CompletedTask;
            }

            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _receiveWaiters.Add((calls, signal));
            return signal.Task.Soon();
        }
    }

    private void SignalReceiveCall()
    {
        List<TaskCompletionSource> release;
        lock (_gate)
        {
            _receiveCalls++;
            release = _receiveWaiters.Where(w => w.Calls <= _receiveCalls).Select(w => w.Signal).ToList();
            _receiveWaiters.RemoveAll(w => w.Calls <= _receiveCalls);
        }

        release.ForEach(s => s.TrySetResult());
    }
}

internal sealed class ScriptedTransportFactory : IWebSocketTransportFactory
{
    private readonly Channel<ScriptedWebSocketTransport> _created = Channel.CreateUnbounded<ScriptedWebSocketTransport>();
    private int _count;

    /// <summary>When set, every new transport fails to connect.</summary>
    public bool FailConnects { get; set; }

    public int CreatedCount => Volatile.Read(ref _count);

    public IWebSocketTransport Create()
    {
        var transport = new ScriptedWebSocketTransport(FailConnects);
        Interlocked.Increment(ref _count);
        _created.Writer.TryWrite(transport);
        return transport;
    }

    public async Task<ScriptedWebSocketTransport> NextAsync() => await _created.Reader.ReadAsync().Soon();
}
