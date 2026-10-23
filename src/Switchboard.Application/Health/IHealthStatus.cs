namespace Switchboard.Application.Health;

public interface IHealthStatus
{
    /// <summary>True only when every link the add-on depends on is up.</summary>
    bool IsHealthy { get; }
}
