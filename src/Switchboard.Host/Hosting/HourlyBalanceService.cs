using Switchboard.Application.Balance;

namespace Switchboard.Host.Hosting;

/// <summary>
/// Creates the hourly balance monitor at start, so that it listens from the first published cycle, and
/// runs its writer loop: the baseline of each new hour is written as it is taken (no write at stop,
/// nothing is pending then).
/// </summary>
internal sealed class HourlyBalanceService(HourlyBalanceMonitor monitor) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => monitor.RunAsync(stoppingToken);
}
