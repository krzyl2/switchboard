using Switchboard.Application.Setup.SelfTest;
using Switchboard.Host.Composition;

namespace Switchboard.Host.Hosting;

/// <summary>
/// The setup wizard's link lifetime: resolves where discovery comes from at start (so a development
/// setting in the wrong place stops the process at once, like replay), and at stop ends any
/// self-test session and closes the discovery link cleanly. The link itself opens only on the first
/// setup request.
/// </summary>
internal sealed class SetupLinkService(SetupSources sources, PhaseSelfTestService selfTest) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        selfTest.Dispose();
        if (sources.Discovery is IAsyncDisposable link)
        {
            await link.DisposeAsync().ConfigureAwait(false);
        }
    }
}
