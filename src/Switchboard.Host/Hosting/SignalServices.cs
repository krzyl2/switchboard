using Switchboard.Application.Signals;
using Switchboard.Host.Composition;
using Switchboard.Infrastructure.Configuration;
using Switchboard.Infrastructure.Replay;

namespace Switchboard.Host.Hosting;

/// <summary>Runs the signal store's single reader for the life of the process.</summary>
internal sealed class SignalStoreService(SignalStoreRunner runner) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => runner.RunAsync(stoppingToken);
}

/// <summary>Loads the configuration document at start and reloads it when it changes.</summary>
internal sealed class ConfigurationWatcherService(ConfigurationFileWatcher watcher) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        watcher.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        watcher.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>Plays recorded history in development replay mode; does nothing otherwise.</summary>
internal sealed class ReplayService(SignalSourceSelection selection, IServiceProvider services) : IHostedService
{
    private ReplaySignalSource? _source;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (selection.IsReplay)
        {
            _source = services.GetRequiredService<ReplaySignalSource>();
            _source.Start();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _source?.Dispose();
        return Task.CompletedTask;
    }
}
