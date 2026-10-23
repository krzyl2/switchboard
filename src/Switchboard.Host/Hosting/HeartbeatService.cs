using Switchboard.Application.Health;

namespace Switchboard.Host.Hosting;

internal sealed class HeartbeatService(HeartbeatLoop loop) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => loop.RunAsync(stoppingToken);
}
