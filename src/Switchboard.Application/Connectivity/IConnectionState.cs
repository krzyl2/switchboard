namespace Switchboard.Application.Connectivity;

/// <summary>Read side of a link's connection state: queryable, with change notification.</summary>
public interface IConnectionState
{
    ConnectionState Current { get; }

    /// <summary>Raised on the thread that changed the state, only when the value actually changes.</summary>
    event EventHandler<ConnectionState>? Changed;
}

/// <summary>State of the Home Assistant WebSocket link (authenticated and answering keepalives).</summary>
public interface IHomeAssistantConnectionState : IConnectionState;

/// <summary>State of the MQTT broker link (connected and discovery published).</summary>
public interface IMqttConnectionState : IConnectionState;
