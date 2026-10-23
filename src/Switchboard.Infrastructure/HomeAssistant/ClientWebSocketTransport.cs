using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace Switchboard.Infrastructure.HomeAssistant;

public sealed class ClientWebSocketTransportFactory : IWebSocketTransportFactory
{
    public IWebSocketTransport Create() => new ClientWebSocketTransport();
}

/// <summary><see cref="IWebSocketTransport"/> over <see cref="ClientWebSocket"/>.</summary>
public sealed class ClientWebSocketTransport : IWebSocketTransport
{
    // A protocol sanity limit, not a tuning knob: no single HA message we handle comes close,
    // and an unbounded buffer would let a broken peer exhaust memory.
    private const int MaxMessageBytes = 16 * 1024 * 1024;
    private const int ReceiveChunkBytes = 16 * 1024;

    private readonly ClientWebSocket _socket = new();

    public async Task ConnectAsync(Uri uri, string accessToken, CancellationToken cancellationToken)
    {
        // The Supervisor proxy authenticates the upgrade request itself; HA core ignores the header
        // and relies on the in-protocol auth message that follows.
        _socket.Options.SetRequestHeader("Authorization", $"Bearer {accessToken}");
        await _socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendAsync(ReadOnlyMemory<byte> utf8Json, CancellationToken cancellationToken) =>
        await _socket.SendAsync(utf8Json, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
            .ConfigureAwait(false);

    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveChunkBytes);
        try
        {
            using var message = new MemoryStream();
            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                if (message.Length + result.Count > MaxMessageBytes)
                {
                    throw new WebSocketException(WebSocketError.Faulted, "Home Assistant message exceeds the size limit.");
                }

                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "shutdown", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public void Abort() => _socket.Abort();

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
