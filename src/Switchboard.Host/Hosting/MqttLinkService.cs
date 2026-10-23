using Switchboard.Infrastructure.Mqtt;

namespace Switchboard.Host.Hosting;

internal sealed class MqttLinkService(MqttLink link) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => link.RunAsync(stoppingToken);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop reconnecting first, then leave the entity OFF and offline; both bounded by the host's shutdown timeout.
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await link.ShutdownAsync(cancellationToken).ConfigureAwait(false);
    }
}
