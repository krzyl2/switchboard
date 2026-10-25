using Microsoft.Extensions.Options;
using Switchboard.Application.Options;
using Switchboard.Host.Composition;
using Switchboard.Infrastructure.HomeAssistant;

namespace Switchboard.Host.Hosting;

/// <summary>
/// Runs the Home Assistant client for the life of the process; without an endpoint it logs why and
/// idles. In development replay mode no client is ever created. The command path's channel is bound
/// to the same client: service calls ride the protection link.
/// </summary>
internal sealed class HomeAssistantLinkService(
    IServiceProvider services,
    SignalSourceSelection selection,
    HomeAssistantCallServiceChannel channel,
    IOptions<HomeAssistantOptions> options,
    TimeProvider time,
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
        channel.Bind(_client);
        return _client.RunAsync(stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Close first: cancelling the run loop would abort the socket instead of closing it. Bounded,
        // so the container's stop grace still covers the links behind it.
        if (_client is not null)
        {
            using var deadline = new CancellationTokenSource(options.Value.CloseTimeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
            try
            {
                await _client.CloseAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                logger.LogDebug("The Home Assistant connection did not close within {Timeout}; it is dropped", options.Value.CloseTimeout);
            }
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
