using Switchboard.Host.Composition;
using Switchboard.Infrastructure.Mqtt;

namespace Switchboard.Host.Hosting;

/// <summary>
/// Runs the MQTT link. In development replay mode the link is never created: a replay instance
/// pointed at the house broker would otherwise take over the live add-on's identity and report it
/// unhealthy, and the documented shed automation would switch real loads off.
/// </summary>
internal sealed class MqttLinkService(IServiceProvider services, SignalSourceSelection selection) : BackgroundService
{
    private MqttLink? _link;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (selection.IsReplay)
        {
            return Task.CompletedTask;
        }

        _link = services.GetRequiredService<MqttLink>();
        return _link.RunAsync(stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop reconnecting first, then leave the entity OFF and offline; both bounded by the host's shutdown timeout.
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        if (_link is not null)
        {
            await _link.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
