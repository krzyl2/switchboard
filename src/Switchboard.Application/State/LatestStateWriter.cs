using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Switchboard.Application.State;

/// <summary>
/// The machinery a persistence loop shares: a one-slot wake-up (a burst of changes is one write,
/// and the producer never waits), one write at a time, the latest state only (the owner decides
/// what is latest and hands it over at write time), and a failure streak that is logged once when
/// it starts and once when it ends. The owner keeps what the state is and when it is due; this keeps
/// how a write is attempted and reported, so a slow or full disk costs the producer nothing.
/// </summary>
/// <typeparam name="T">What is written.</typeparam>
public sealed class LatestStateWriter<T>
{
    private readonly string _file;
    private readonly Action<T> _save;
    private readonly string _failureNote;
    private readonly ILogger _logger;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private string? _error;

    /// <param name="file">The file's name as the log and the owner's report spell it.</param>
    /// <param name="save">Writes the state; throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the disk refuses.</param>
    /// <param name="failureNote">What the log adds to the first failure of a streak: what happens next.</param>
    public LatestStateWriter(string file, Action<T> save, string failureNote, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(save);
        _file = file;
        _save = save;
        _failureNote = failureNote;
        _logger = logger;
    }

    /// <summary>The reason of the last failed write, null while writes succeed.</summary>
    public string? Error => Volatile.Read(ref _error);

    /// <summary>Asks the loop for a write; never blocks, and a wake-up already waiting absorbs this one.</summary>
    public void Wake() => _wake.Writer.TryWrite(true);

    /// <summary>
    /// The loop: waits for a wake-up and writes, until cancelled. While a write is failing and
    /// <paramref name="retry"/> is set, it retries on that clock instead of waiting for a change.
    /// </summary>
    public async Task RunAsync(Func<CancellationToken, Task> write, (TimeSpan Every, TimeProvider Time)? retry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        try
        {
            while (true)
            {
                if (retry is { } clock && Error is not null)
                {
                    await Task.Delay(clock.Every, clock.Time, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _wake.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                }

                await write(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping; the owner's final write is its FlushAsync.
        }
    }

    /// <summary>
    /// One write attempt. <paramref name="next"/> runs under the write lock and returns what to write
    /// with the step that records it as written, or null when nothing is due; the step runs only
    /// after a successful save, before <paramref name="saved"/>. A failure is kept in <see cref="Error"/>.
    /// </summary>
    public async Task WriteAsync(Func<(T State, Action Commit)?> next, Action<T>? saved, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (next() is not { } item)
            {
                return;
            }

            try
            {
                _save(item.State);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (Error is null)
                {
                    // Once per failure streak: a full disk must not flood the log at every retry.
                    _logger.LogError("{File} could not be written: {Reason}; {Note}", _file, ex.Message, _failureNote);
                }

                Volatile.Write(ref _error, ex.Message);
                return;
            }

            item.Commit();
            if (Error is not null)
            {
                _logger.LogInformation("{File} is writable again", _file);
                Volatile.Write(ref _error, null);
            }

            saved?.Invoke(item.State);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
