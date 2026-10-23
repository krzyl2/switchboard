namespace Switchboard.Infrastructure.HomeAssistant;

/// <summary>
/// The socket under the Home Assistant client, small enough that tests can script it frame by frame.
/// One instance per connection attempt.
/// </summary>
public interface IWebSocketTransport : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, string accessToken, CancellationToken cancellationToken);

    Task SendAsync(ReadOnlyMemory<byte> utf8Json, CancellationToken cancellationToken);

    /// <summary>Returns the next complete text message, or null once the peer has closed the connection.</summary>
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>Starts a clean close handshake; a pending receive then sees the peer's close.</summary>
    Task CloseAsync(CancellationToken cancellationToken);

    /// <summary>Drops the connection immediately; pending operations fail.</summary>
    void Abort();
}

public interface IWebSocketTransportFactory
{
    IWebSocketTransport Create();
}
