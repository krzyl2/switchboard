using System.Threading.Channels;

namespace Switchboard.Application.Guard;

/// <summary>
/// The single queue into the guard. The signal store's publications, the kill switch, the guard's
/// own timer and shutdown write; only the guard runner reads, so the guard never runs concurrently
/// with itself. A trigger carries no data: each evaluation reads the latest inputs.
/// </summary>
public sealed class GuardInbox
{
    private readonly Channel<GuardTrigger> _channel = Channel.CreateUnbounded<GuardTrigger>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });

    public ChannelReader<GuardTrigger> Reader => _channel.Reader;

    public void Post(GuardTrigger trigger) => _channel.Writer.TryWrite(trigger);
}
