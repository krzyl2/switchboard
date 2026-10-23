using Switchboard.Application.Options;

namespace Switchboard.Tests.Support;

/// <summary>
/// Option values for unit tests. Durations are chosen pairwise distinct (and distinct from every
/// backoff step) so a test can tell which timer the code armed just by its due time.
/// </summary>
internal static class TestOptions
{
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PongTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ReconnectMin = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ReconnectMax = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan HeartbeatExpireAfter = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan MqttKeepAlive = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan MqttShutdownTimeout = TimeSpan.FromSeconds(3);

    public static HomeAssistantOptions HomeAssistant() => new()
    {
        SupervisorWebSocketUrl = "ws://supervisor/core/websocket",
        PingInterval = PingInterval,
        PongTimeout = PongTimeout,
        ReconnectMinDelay = ReconnectMin,
        ReconnectMaxDelay = ReconnectMax,
    };

    public static MqttOptions Mqtt() => new()
    {
        SupervisorServiceUrl = "http://supervisor/services/mqtt",
        DiscoveryPrefix = "homeassistant",
        BaseTopic = "switchboard",
        ClientId = "switchboard",
        ReconnectMinDelay = ReconnectMin,
        ReconnectMaxDelay = ReconnectMax,
        KeepAlivePeriod = MqttKeepAlive,
        ShutdownTimeout = MqttShutdownTimeout,
    };

    public static HeartbeatOptions Heartbeat() => new()
    {
        Interval = HeartbeatInterval,
        ExpireAfter = HeartbeatExpireAfter,
    };
}
