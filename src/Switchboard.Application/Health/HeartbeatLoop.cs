using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Switchboard.Application.Options;

namespace Switchboard.Application.Health;

/// <summary>
/// Publishes the health state once at start and then every <see cref="HeartbeatOptions.Interval"/>.
/// The entity's expire_after turns a hung process into "unavailable" in Home Assistant, so the
/// beat must keep coming even when nothing changes.
/// </summary>
public sealed class HeartbeatLoop(
    IHealthStatus health,
    IHealthPublisher publisher,
    IOptions<HeartbeatOptions> options,
    TimeProvider timeProvider,
    ILogger<HeartbeatLoop> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // Created before the first await so the period is anchored to the start of the loop.
        using var timer = new PeriodicTimer(options.Value.Interval, timeProvider);
        try
        {
            do
            {
                await BeatAsync(cancellationToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task BeatAsync(CancellationToken cancellationToken)
    {
        var healthy = health.IsHealthy;
        try
        {
            await publisher.PublishHealthAsync(healthy, cancellationToken).ConfigureAwait(false);
            logger.LogDebug("Heartbeat published: {Healthy}", healthy);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed beat must not end the loop: the next one may succeed after a reconnect.
            logger.LogWarning(ex, "Heartbeat publish failed");
        }
    }
}
