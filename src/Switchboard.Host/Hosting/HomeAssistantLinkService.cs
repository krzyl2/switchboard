using Switchboard.Host.Composition;
using Switchboard.Infrastructure.HomeAssistant;

namespace Switchboard.Host.Hosting;

/// <summary>
/// Runs the Home Assistant client for the life of the process; without an endpoint it logs why and
/// idles. In development replay mode no client is ever created.
/// </summary>
internal sealed class HomeAssistantLinkService(
    IServiceProvider services,
    SignalSourceSelection selection,
    ILogger<HomeAssistantLinkService> logger) : BackgroundService
{
    private HomeAssistantClient? _client;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (selection.IsReplay)
        {
            return Task.CompletedTask;
        }

        var resolution = services.GetRequiredService<HomeAssistantEndpointResolution>();
        if (resolution.Endpoint is null)
        {
            logger.LogError("{Problem}", resolution.Problem);
            return Task.CompletedTask;
        }

        _client = ActivatorUtilities.CreateInstance<HomeAssistantClient>(services, resolution.Endpoint);
        return _client.RunAsync(stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Close first: cancelling the run loop would abort the socket instead of closing it.
        if (_client is not null)
        {
            await _client.CloseAsync(cancellationToken).ConfigureAwait(false);
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
