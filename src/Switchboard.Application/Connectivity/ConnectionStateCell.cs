namespace Switchboard.Application.Connectivity;

/// <summary>
/// Thread-safe holder written by exactly one link and read by health, heartbeat and the UI.
/// Concrete subclasses exist so the container can tell the two links apart without keys.
/// </summary>
public abstract class ConnectionStateCell : IConnectionState
{
    private int _state = (int)ConnectionState.Disconnected;

    public ConnectionState Current => (ConnectionState)Volatile.Read(ref _state);

    public event EventHandler<ConnectionState>? Changed;

    public void Set(ConnectionState next)
    {
        var previous = (ConnectionState)Interlocked.Exchange(ref _state, (int)next);
        if (previous != next)
        {
            Changed?.Invoke(this, next);
        }
    }
}

public sealed class HomeAssistantConnectionState : ConnectionStateCell, IHomeAssistantConnectionState;

public sealed class MqttConnectionState : ConnectionStateCell, IMqttConnectionState;
