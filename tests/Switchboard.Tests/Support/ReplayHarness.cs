using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Switchboard.Application.Signals;
using Switchboard.Infrastructure.Replay;

namespace Switchboard.Tests.Support;

/// <summary>
/// Plays recorded history through the production pipeline (replay source, inbox, runner, store) on
/// a virtual clock, jumping from event to event: the next recorded row, the store's next deadline,
/// or a checkpoint the test asked for. Probes are answered by the replay source, as in dev replay
/// mode. Seventy-two hours run in seconds; no wall-clock wait anywhere.
/// </summary>
internal sealed class ReplayHarness
{
    private readonly ReplayData _data;

    public ReplayHarness(ReplayData data, string yaml)
    {
        _data = data;
        Time = new FakeTimeProvider(data.Start);
        var clock = new SignalClock(Time);
        Store = new SignalStore(new SignalStoreSettings(TimeSpan.Zero));
        Source = new ReplaySignalSource(data, Inbox, clock, NullLogger<ReplaySignalSource>.Instance);
        Holder = new SignalSnapshotHolder(new SignalDiagnostics(Store.Snapshot(Time.GetUtcNow()), null, [], []));
        Runner = new SignalStoreRunner(Inbox, Store, Source, Holder, clock, NullLogger<SignalStoreRunner>.Instance);

        Inbox.Post(TestConfigurations.Valid(yaml));
        Source.Start();
        Drain();
    }

    public FakeTimeProvider Time { get; }

    public SignalInbox Inbox { get; } = new();

    public SignalStore Store { get; }

    public ReplaySignalSource Source { get; }

    public SignalSnapshotHolder Holder { get; }

    public SignalStoreRunner Runner { get; }

    public DateTimeOffset Now => Time.GetUtcNow();

    public SignalDiagnostics Current => Holder.Current;

    /// <summary>
    /// Advances to <paramref name="end"/>; <paramref name="observe"/> runs at each checkpoint once everything due has been processed.
    /// <paramref name="consumerDeadline"/> adds a consumer's own next deadline (the guard's) to the
    /// events jumped to, and <paramref name="afterStep"/> runs after the store has processed each one.
    /// </summary>
    public void RunUntil(
        DateTimeOffset end,
        IEnumerable<DateTimeOffset>? checkpoints = null,
        Action<DateTimeOffset, SignalDiagnostics>? observe = null,
        Func<DateTimeOffset, DateTimeOffset?>? consumerDeadline = null,
        Action? afterStep = null)
    {
        var pending = new Queue<DateTimeOffset>((checkpoints ?? []).Where(c => c > Now && c <= end).Order());
        while (Now < end)
        {
            var next = end;
            if (NextRowAfter(Now) is { } row && row < next)
            {
                next = row;
            }

            if (Store.NextDeadline(Now) is { } deadline && deadline < next)
            {
                next = deadline;
            }

            if (pending.TryPeek(out var checkpoint) && checkpoint < next)
            {
                next = checkpoint;
            }

            if (consumerDeadline?.Invoke(Now) is { } consumer && consumer > Now && consumer < next)
            {
                next = consumer;
            }

            Time.Advance(next - Now);
            Drain();
            afterStep?.Invoke();
            while (pending.TryPeek(out var due) && due <= Now)
            {
                pending.Dequeue();
                observe?.Invoke(due, Current);
            }
        }
    }

    private void Drain()
    {
        do
        {
            Runner.Step();
        }
        while (Inbox.Reader.TryPeek(out _));
    }

    private DateTimeOffset? NextRowAfter(DateTimeOffset now)
    {
        int low = 0, high = _data.Rows.Length - 1;
        DateTimeOffset? found = null;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (_data.Rows[middle].Timestamp > now)
            {
                found = _data.Rows[middle].Timestamp;
                high = middle - 1;
            }
            else
            {
                low = middle + 1;
            }
        }

        return found;
    }
}
