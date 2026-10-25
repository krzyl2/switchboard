using Microsoft.Extensions.Options;
using Switchboard.Application.Guard;
using Switchboard.Application.Options;
using Switchboard.Application.Presentation;

namespace Switchboard.Host.Hosting;

/// <summary>
/// Runs the runtime UI's read side. Registered just before the guard, so it starts first (the
/// statistics and excursions are loaded before the guard's first step) and stops right after it (the
/// guard's shutdown step is counted, then the files are written once more), within
/// Persistence:ShutdownTimeout. A failure here never touches protection: the hub is a read-only
/// listener whose errors the guard runner isolates.
/// </summary>
internal sealed class PresentationService(
    PresentationHub hub,
    PresentationPersistence persistence,
    GuardRunner runner,
    IOptions<PersistenceOptions> options,
    TimeProvider time,
    ILogger<PresentationService> logger) : IHostedService
{
    private readonly CancellationTokenSource _stop = new();
    private Task _writer = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        persistence.Load();
        hub.Attach(runner);
        var report = hub.Report;
        logger.LogInformation("Runtime UI statistics: statistics.json {Statistics}, excursions.json {Excursions}", report.Statistics, report.Excursions);
        _writer = persistence.RunAsync(_stop.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(options.Value.ShutdownTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        try
        {
            await _writer.WaitAsync(linked.Token).ConfigureAwait(false);
            await persistence.FlushAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            logger.LogWarning("The runtime UI's statistics were not written within {Timeout}; the next start loses at most the last interval", options.Value.ShutdownTimeout);
        }
    }
}
