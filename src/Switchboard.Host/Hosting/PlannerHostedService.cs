using Switchboard.Application.Planning;
using Switchboard.Application.Signals;

namespace Switchboard.Host.Hosting;

/// <summary>
/// Runs the planner's own loop (15-PLANNER): its timer and its wake-ups, apart from the guard's. A new store cycle only nudges it (throttled inside the
/// service); the planner never blocks the thread that publishes the guard's inputs and a failure here ends only this service's work, never the host's.
/// </summary>
internal sealed class PlannerHostedService(PlannerService service, IControlInputSource inputs) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void Nudge(object? sender, ControlInputs published) => service.Notify();
        inputs.Published += Nudge;
        try
        {
            await service.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            inputs.Published -= Nudge;
        }
    }
}
