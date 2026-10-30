using Microsoft.Extensions.Logging;
using Switchboard.Application.Signals;

namespace Switchboard.Application.Ev;

/// <summary>
/// The loop that computes the EV module's offer outside the guard's thread (docs/EV.md B1) and publishes it as an immutable <see cref="EvSnapshot"/>. One
/// <see cref="Tick"/> per dispatch cycle; a computation that throws is published as a failure (logged once per streak) and never leaves this loop, so the guard
/// can neither wait for it nor be stopped by it. Cancellation is not a failure and passes through.
/// </summary>
public sealed class EvSnapshotKeeper(EvEngine engine, IEvInputSource inputs, SignalClock clock, ILogger<EvSnapshotKeeper> logger) : IEvSnapshotSource
{
    // Between computations while there is no valid configuration to read a cycle from; not a control parameter.
    private static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(5);

    private EvPublished _current = EvPublished.None;
    private bool _failing;
    private bool _stale;

    public EvPublished Current => Volatile.Read(ref _current);

    /// <summary>Computes and publishes once.</summary>
    public void Tick()
    {
        try
        {
            var now = clock.Time.GetUtcNow();

            // The previous offer ran out before this computation: the guard has been reading "failed" since (the plan fell back to v2 for the car). Say so once per
            // streak; it means the loop is late (a slow computation or a cycle longer than control.ev.snapshot_max_age allows).
            var late = Current.Snapshot is { } previous && now > previous.ValidUntil;
            if (late && !_stale)
            {
                logger.LogWarning("The EV module's offer went stale before the next computation (late loop or a cycle longer than ev.snapshot_max_age); the car followed v2 meanwhile");
            }

            _stale = late;
            var snapshot = engine.Compute(now);
            Volatile.Write(ref _current, snapshot is null ? EvPublished.None : new EvPublished(snapshot, false));
            _failing = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            NoteFailure(ex);
            Volatile.Write(ref _current, new EvPublished(null, true));
        }
    }

    private void NoteFailure(Exception ex)
    {
        if (!_failing)
        {
            logger.LogError(ex, "The EV module could not compute its offer; it offers no goals until it recovers, protection and dispatch are unaffected");
            _failing = true;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // Nothing in the loop may end it: an exception escaping a BackgroundService stops the host, and this module must never be able to do that.
            var cycle = IdlePoll;
            try
            {
                Tick();
                cycle = inputs.Current.Configuration?.Configuration?.Control.Dispatch.Cycle ?? IdlePoll;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                NoteFailure(ex);
            }

            try
            {
                await Task.Delay(cycle > TimeSpan.Zero ? cycle : IdlePoll, clock.Time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
