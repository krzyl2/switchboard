using Switchboard.Application.Balance;

namespace Switchboard.Host.Hosting;

/// <summary>
/// Creates the hourly balance monitor at start so that it listens from the first published cycle.
/// Nothing else to start or stop: the monitor holds no file and no timer.
/// </summary>
internal sealed class HourlyBalanceService(HourlyBalanceMonitor monitor) : IHostedService
{
    public HourlyBalanceMonitor Monitor { get; } = monitor;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
