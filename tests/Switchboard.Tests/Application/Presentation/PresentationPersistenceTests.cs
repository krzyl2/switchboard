using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Switchboard.Application.Options;
using Switchboard.Application.Presentation;
using Switchboard.Application.Signals;
using Switchboard.Application.State;
using Switchboard.Tests.Support;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Switchboard.Tests.Application.Presentation;

/// <summary>
/// The writer of statistics.json and excursions.json decides what a crash or a full disk costs the
/// runtime UI, and must never cost the guard anything: a file that cannot be loaded starts the UI
/// without history instead of stopping the start, an ended excursion is written at once, the large
/// statistics file only once a bucket closed (flash storage), the shutdown step is written last, and
/// a failed write is shown until a write succeeds again.
/// </summary>
public sealed class PresentationPersistenceTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(10);

    private readonly IOptions<StatisticsOptions> _options = MsOptions.Create(TestStatistics.Options());
    private readonly PresentationHub _hub = TestStatistics.Hub();
    private readonly MemoryStore<StatisticsState> _statistics = new();
    private readonly MemoryStore<ExcursionState> _excursions = new();
    private readonly FakeTimeProvider _hostTime = new(TestSteps.Start);
    private readonly SteppableTime _signalTime = new(TestSteps.Start);
    private readonly LogCapture _log = new();

    [Fact]
    public void Load_hands_how_each_file_was_found_to_the_hub()
    {
        _statistics.Loaded = new PresentationLoad<StatisticsState>(null, StateFileStatus.ResetCorrupt, "statistics.json was not usable");

        Persistence().Load();

        Assert.Equal(StateFileStatus.ResetCorrupt, _hub.Report.Statistics);
        Assert.Equal("statistics.json was not usable", _hub.Report.StatisticsWarning);
        Assert.Equal(StateFileStatus.Fresh, _hub.Report.Excursions);
    }

    [Fact]
    public void A_load_that_throws_starts_without_history_and_never_stops_the_start()
    {
        // PresentationService loads before GuardService starts: an exception escaping here would keep
        // the guard from ever running, for the sake of a UI file.
        _statistics.LoadThrows = new OverflowException("TimeSpan overflowed");

        Persistence().Load();

        Assert.Equal(StateFileStatus.ResetCorrupt, _hub.Report.Statistics);
        Assert.Contains("TimeSpan overflowed", _hub.Report.StatisticsWarning, StringComparison.Ordinal);
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Nothing_is_written_while_nothing_changed()
    {
        var persistence = Persistence();
        persistence.Load();

        await persistence.SaveDueAsync(CancellationToken.None);
        await persistence.FlushAsync(CancellationToken.None);

        Assert.Empty(_statistics.Saved);
        Assert.Empty(_excursions.Saved);
    }

    [Fact]
    public async Task The_open_bucket_alone_waits_a_closed_bucket_is_written_and_shutdown_writes_the_rest()
    {
        // statistics.json is hundreds of KiB on the reference installation: rewritten every minute
        // that is about half a gigabyte a day on SD or eMMC storage. A crash loses at most the open bucket.
        var persistence = Persistence();
        persistence.Load();
        var at = Observe(TestSteps.Start, TimeSpan.FromMinutes(1));
        await persistence.SaveDueAsync(CancellationToken.None);
        var first = _statistics.Saved.Count;
        Assert.Equal(1, first);

        at = Observe(at, TimeSpan.FromMinutes(2));
        await persistence.SaveDueAsync(CancellationToken.None);
        Assert.Equal(first, _statistics.Saved.Count);

        at = Observe(at, TimeSpan.FromMinutes(3));
        await persistence.SaveDueAsync(CancellationToken.None);
        Assert.Equal(first + 1, _statistics.Saved.Count);

        var last = Observe(at, TimeSpan.FromMinutes(1)) - Poll;
        await persistence.SaveDueAsync(CancellationToken.None);
        Assert.Equal(first + 1, _statistics.Saved.Count);
        await persistence.FlushAsync(CancellationToken.None);

        Assert.Equal(first + 2, _statistics.Saved.Count);
        Assert.Equal(last, _statistics.Saved[^1].IntegratedTo);
    }

    [Fact]
    public async Task A_failed_write_is_reported_until_a_write_succeeds_again()
    {
        var persistence = Persistence();
        persistence.Load();
        _statistics.Refuse = true;
        var at = Observe(TestSteps.Start, TimeSpan.FromMinutes(1));

        await persistence.SaveDueAsync(CancellationToken.None);
        Assert.Contains("statistics.json could not be written", _hub.Report.WriteError, StringComparison.Ordinal);

        // A pass with nothing to retry proves nothing about the disk.
        await persistence.SaveDueAsync(CancellationToken.None);
        Assert.NotNull(_hub.Report.WriteError);

        _statistics.Refuse = false;
        Observe(at, Poll);
        await persistence.SaveDueAsync(CancellationToken.None);
        Assert.Null(_hub.Report.WriteError);
        Assert.Single(_statistics.Saved);
    }

    [Fact]
    public async Task An_ended_excursion_wakes_the_writer_at_once()
    {
        // The Limits chart's excursion must survive a crash right after it: no waiting for the interval.
        var persistence = Persistence();
        persistence.Load();
        using var stop = new CancellationTokenSource();
        var loop = persistence.RunAsync(stop.Token);

        var at = TestSteps.Start;
        foreach (var (current, duration) in new[] { (8.0, TimeSpan.FromMinutes(1)), (20.0, TimeSpan.FromMinutes(1)), (8.0, TimeSpan.FromMinutes(3)) })
        {
            for (var end = at + duration; at < end; at += Poll)
            {
                _hub.Observe(ExcursionRecorderTests.PhaseStep(at, current));
            }
        }

        Assert.True(await _excursions.WaitForSaveAsync(), "the ended excursion was not written without the interval elapsing");
        var saved = _excursions.Saved[^1].Phases.Single().Last!;
        Assert.False(saved.Active);
        await stop.CancelAsync();
        await loop;
    }

    [Fact]
    public async Task A_closed_bucket_is_written_when_the_interval_elapses()
    {
        var persistence = Persistence();
        persistence.Load();
        using var stop = new CancellationTokenSource();
        var loop = persistence.RunAsync(stop.Token);
        Observe(TestSteps.Start, TimeSpan.FromMinutes(6));

        _hostTime.Advance(_options.Value.SaveInterval);

        Assert.True(await _statistics.WaitForSaveAsync(), "nothing was written after the save interval");
        await stop.CancelAsync();
        await loop;
    }

    private PresentationPersistence Persistence() => new(
        _hub,
        _statistics,
        _excursions,
        _options,
        _hostTime,
        new SignalClock(_signalTime),
        _log.For<PresentationPersistence>());

    /// <summary>Steps every 10 s from <paramref name="from"/> for <paramref name="duration"/>; the signal clock follows; returns the next step time.</summary>
    private DateTimeOffset Observe(DateTimeOffset from, TimeSpan duration)
    {
        var at = from;
        for (; at < from + duration; at += Poll)
        {
            _signalTime.Advance(at - _signalTime.Now);
            _hub.Observe(TestSteps.Step(at));
        }

        return at;
    }

    private sealed class MemoryStore<T> : IPresentationStore<T>
        where T : class
    {
        private readonly SemaphoreSlim _saved = new(0);
        private readonly object _sync = new();
        private readonly List<T> _states = [];

        public PresentationLoad<T> Loaded { get; set; } = new(null, StateFileStatus.Fresh, null);

        public Exception? LoadThrows { get; set; }

        public bool Refuse { get; set; }

        public IReadOnlyList<T> Saved
        {
            get
            {
                lock (_sync)
                {
                    return [.. _states];
                }
            }
        }

        public PresentationLoad<T> Load() => LoadThrows is { } exception ? throw exception : Loaded;

        public void Save(T state)
        {
            if (Refuse)
            {
                throw new IOException("No space left on device");
            }

            lock (_sync)
            {
                _states.Add(state);
            }

            _saved.Release();
        }

        /// <summary>A guard against hanging, not a wait for time to pass: the save is triggered before this is awaited.</summary>
        public Task<bool> WaitForSaveAsync() => _saved.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
