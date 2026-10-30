using Switchboard.Application.Ev;

namespace Switchboard.Host.Hosting;

/// <summary>
/// Runs the EV module's own loop (docs/EV.md B1): the offer is computed here, apart from the guard's thread, and published as an immutable snapshot. A failure
/// of the computation is published as a failure by <see cref="EvSnapshotKeeper"/>; it never reaches the host or the guard.
/// </summary>
internal sealed class EvSnapshotHostedService(EvSnapshotKeeper keeper) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => keeper.RunAsync(stoppingToken);
}
