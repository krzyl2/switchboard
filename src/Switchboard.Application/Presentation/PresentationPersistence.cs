using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Switchboard.Application.Options;
using Switchboard.Application.Signals;
using Switchboard.Application.State;

namespace Switchboard.Application.Presentation;

/// <summary>
/// Loads statistics.json and excursions.json before the first step and writes them from its own
/// loop: every SaveInterval when they changed, at once when an excursion ended, and once more at
/// shutdown. statistics.json (hundreds of KiB) is rewritten only once a bucket closed or a counter
/// moved, not for every step the open bucket grew: Home Assistant usually runs on SD or eMMC
/// storage, and a crash loses at most the open bucket. The interval runs on the host clock; every
/// time written into the files (saved_at, the retention cut-off at load) comes from the signal
/// clock, the same timeline the buckets are on, so development replay stays consistent.
/// Serialisation works on the hub's immutable snapshots, with no lock held. A failed write is
/// reported and retried at the next interval; nothing here can stop or delay the guard.
/// </summary>
public sealed class PresentationPersistence
{
    private readonly PresentationHub _hub;
    private readonly IPresentationStore<StatisticsState> _statistics;
    private readonly IPresentationStore<ExcursionState> _excursions;
    private readonly StatisticsOptions _options;
    private readonly TimeProvider _hostTime;
    private readonly TimeProvider _signalTime;
    private readonly ILogger<PresentationPersistence> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _savedStatistics = -1;
    private StatisticsMark? _savedMark;
    private long _savedExcursions = -1;
    private string? _error;

    public PresentationPersistence(
        PresentationHub hub,
        IPresentationStore<StatisticsState> statistics,
        IPresentationStore<ExcursionState> excursions,
        IOptions<StatisticsOptions> options,
        TimeProvider hostTime,
        SignalClock signalClock,
        ILogger<PresentationPersistence> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(signalClock);
        _hub = hub;
        _statistics = statistics;
        _excursions = excursions;
        _options = options.Value;
        _hostTime = hostTime;
        _signalTime = signalClock.Time;
        _logger = logger;
    }

    /// <summary>
    /// Reads both files and hands them to the hub. Call once, before the guard's first step. The
    /// stores set a bad file aside themselves; whatever still fails here starts the runtime UI
    /// without its history, because the guard starts after this and must never wait on a UI file.
    /// </summary>
    public void Load()
    {
        var now = _signalTime.GetUtcNow();
        try
        {
            _hub.Restore(_statistics.Load(), _excursions.Load(), now);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var warning = $"The runtime UI's statistics could not be loaded ({ex.Message}); it started without its history.";
            _logger.LogError(ex, "{Warning}", warning);
            _hub.Restore(new PresentationLoad<StatisticsState>(null, StateFileStatus.ResetCorrupt, warning), new PresentationLoad<ExcursionState>(null, StateFileStatus.ResetCorrupt, warning), now);
        }

        _savedStatistics = _hub.Statistics.Version;
        _savedMark = StatisticsMark.Of(_hub.Statistics);
        _savedExcursions = _hub.Excursions.Version;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                using (var interval = new CancellationTokenSource(_options.SaveInterval, _hostTime))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, interval.Token))
                {
                    try
                    {
                        await _hub.SaveRequests.WaitToReadAsync(linked.Token).ConfigureAwait(false);
                        _hub.SaveRequests.TryRead(out _);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // The interval elapsed: save whatever changed.
                    }
                }

                await SaveDueAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping; the final save is FlushAsync, after the guard's shutdown step.
        }
    }

    /// <summary>One pass of the loop: excursions.json when it changed, statistics.json when a bucket closed or a counter moved.</summary>
    internal Task SaveDueAsync(CancellationToken cancellationToken) => SaveChangedAsync(final: false, cancellationToken);

    /// <summary>Writes whatever changed, once, the open bucket included; used at shutdown after the guard's last step.</summary>
    public Task FlushAsync(CancellationToken cancellationToken) => SaveChangedAsync(final: true, cancellationToken);

    private async Task SaveChangedAsync(bool final, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _signalTime.GetUtcNow();
            var statistics = _hub.Statistics;
            var excursions = _hub.Excursions;
            string? error = null;
            var attempted = false;
            var mark = StatisticsMark.Of(statistics);
            if (statistics.Version != _savedStatistics && (final || mark != _savedMark))
            {
                attempted = true;
                error = Write(() => _statistics.Save(statistics.ToState(now)), "statistics.json");
                if (error is null)
                {
                    _savedStatistics = statistics.Version;
                    _savedMark = mark;
                }
            }

            if (excursions.Version != _savedExcursions)
            {
                attempted = true;
                var failed = Write(() => _excursions.Save(excursions.ToState(now)), "excursions.json");
                _savedExcursions = failed is null ? excursions.Version : _savedExcursions;
                error ??= failed;
            }

            // Only a write that was tried can clear a failure: a pass with nothing to save proves nothing.
            if (attempted && error != _error)
            {
                if (error is null)
                {
                    _logger.LogInformation("The runtime UI's statistics files are writable again");
                }

                _error = error;
                _hub.ReportWrite(error);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private string? Write(Action save, string file)
    {
        try
        {
            save();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_error is null)
            {
                // Once per failure streak: a full disk must not flood the log at every interval.
                _logger.LogError("{File} could not be written: {Reason}; trying again in {Interval}", file, ex.Message, _options.SaveInterval);
            }

            return $"{file} could not be written ({ex.Message})";
        }
    }

    /// <summary>What makes statistics.json worth rewriting before shutdown: a new newest bucket (the previous one closed) or a changed counter.</summary>
    private readonly record struct StatisticsMark(DateTimeOffset? NewestBucket, DateTimeOffset? Since, long Sheds, long WouldSheds, long VoltageLosses, long SupplyOutages)
    {
        public static StatisticsMark Of(StatisticsSnapshot snapshot) => new(
            snapshot.Buckets.IsEmpty ? null : snapshot.Buckets.Keys.Last(),
            snapshot.Lifetime.Since,
            snapshot.Lifetime.Sheds,
            snapshot.Lifetime.WouldSheds,
            snapshot.Lifetime.VoltageLosses,
            snapshot.Lifetime.SupplyOutages);
    }
}
