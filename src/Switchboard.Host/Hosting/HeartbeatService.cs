using Switchboard.Application.Health;
using Switchboard.Host.Composition;

namespace Switchboard.Host.Hosting;

/// <summary>Publishes the health heartbeat; not in development replay mode, where there is no MQTT link.</summary>
internal sealed class HeartbeatService(IServiceProvider services, SignalSourceSelection selection) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        selection.IsReplay ? Task.CompletedTask : services.GetRequiredService<HeartbeatLoop>().RunAsync(stoppingToken);
}
