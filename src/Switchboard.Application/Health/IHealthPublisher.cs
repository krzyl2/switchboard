namespace Switchboard.Application.Health;

/// <summary>Publishes the watchdog heartbeat state (SAFE-02) to wherever Home Assistant reads it.</summary>
public interface IHealthPublisher
{
    Task PublishHealthAsync(bool healthy, CancellationToken cancellationToken);
}
