using Switchboard.Application.Setup;
using Switchboard.Application.Setup.SelfTest;
using Switchboard.Infrastructure.HomeAssistant;

namespace Switchboard.Host.Hosting;

/// <summary>
/// Runs the command path's sender for the life of the process. Registered just before the guard,
/// so it stops right after it: the shutdown safe state's commands still leave.
/// </summary>
internal sealed class CommandPathService(HomeAssistantDeviceCommandPort port) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => port.RunAsync(stoppingToken);
}

/// <summary>Runs the derived-sensor check of a hand-written configuration whenever another configuration is in force (start, each reload), retrying while the registry cannot be read; warnings only.</summary>
internal sealed class RuntimeDerivedCheckService(RuntimeDerivedCheckRunner runner) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => runner.RunAsync(stoppingToken);
}

/// <summary>
/// Loads the phase evidence before the guard starts (registered before it) and writes it from its
/// own loop as observations arrive. No write of its own at stop: every observation was written when
/// it was added, and the container's stop grace belongs to the guard and the links.
/// </summary>
internal sealed class PhaseEvidenceService(PhaseEvidencePersistence persistence) : BackgroundService
{
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        persistence.Load();
        return base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => persistence.RunAsync(stoppingToken);
}
