using Switchboard.Application.Connectivity;

namespace Switchboard.Application.Health;

/// <summary>
/// Healthy means: Home Assistant is reachable and authenticated AND the broker carries our
/// heartbeat. An HA-side automation sheds loads on anything else, so "unknown" must read as unhealthy.
/// </summary>
public sealed class HealthEvaluator(
    IHomeAssistantConnectionState homeAssistant,
    IMqttConnectionState mqtt) : IHealthStatus
{
    public bool IsHealthy =>
        homeAssistant.Current == ConnectionState.Connected
        && mqtt.Current == ConnectionState.Connected;
}
