using System.Threading.Channels;
using Switchboard.Application.Configuration;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Signals;

/// <summary>
/// Feeds the signal store. <see cref="Track"/> replaces the set of entities to deliver (an empty
/// set stops delivery); <see cref="RequestProbe"/> asks for the entity's current state including
/// its last report time, answered later as a <see cref="BatchOrigin.Probe"/> batch. Implementations
/// write everything they learn into the <see cref="SignalInbox"/>.
/// </summary>
public interface ISignalSource
{
    void Track(IReadOnlyCollection<TrackedEntity> entities);

    void RequestProbe(EntityId entity);
}

/// <summary>An input to the store; processed strictly in arrival order by one reader.</summary>
public abstract record SignalInput;

public sealed record BatchInput(EntityUpdateBatch Batch) : SignalInput;

public sealed record SourceStatusInput(SignalSourceStatus Status) : SignalInput;

public sealed record ConfigurationInput(ConfigurationResult Result) : SignalInput;

/// <summary>A deadline of the store has come (a staleness transition, a probe, a direction pairing).</summary>
public sealed record TickInput : SignalInput
{
    public static TickInput Instance { get; } = new();
}

/// <summary>
/// The single queue into the signal store. Sources, the configuration watcher and the store's own
/// timer write; only the runner reads, so the store never sees concurrent updates.
/// </summary>
public sealed class SignalInbox
{
    private readonly Channel<SignalInput> _channel = Channel.CreateUnbounded<SignalInput>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });

    public ChannelReader<SignalInput> Reader => _channel.Reader;

    public void Post(EntityUpdateBatch batch) => Write(new BatchInput(batch));

    public void Post(SignalSourceStatus status) => Write(new SourceStatusInput(status));

    public void Post(ConfigurationResult result) => Write(new ConfigurationInput(result));

    public void PostTick() => Write(TickInput.Instance);

    private void Write(SignalInput input) => _channel.Writer.TryWrite(input);
}

/// <summary>
/// The clock of the signal pipeline (store, runner, source, signal ages). It is the host clock,
/// except in development replay mode where it runs on the recorded data's time; nothing else in the
/// process ever uses a replay clock.
/// </summary>
public sealed class SignalClock(TimeProvider time)
{
    public TimeProvider Time { get; } = time;
}
