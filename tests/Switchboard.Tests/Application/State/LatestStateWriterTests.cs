using Microsoft.Extensions.Logging;
using Switchboard.Application.State;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.State;

/// <summary>
/// The writer machinery the state file and the phase evidence file share: the producer never waits,
/// a burst is one write of the latest state, a failure is logged once per streak, and a failed write
/// is retried on the writer's clock without losing the state.
/// </summary>
public sealed class LatestStateWriterTests
{
    private readonly LogCapture _log = new();
    private readonly List<int> _saved = [];
    private bool _diskFull;

    private LatestStateWriter<int> Writer() => new("x.json", Save, "retrying soon", _log.For<LatestStateWriterTests>());

    [Fact]
    public async Task Latest_wins_the_owner_hands_over_the_newest_state_at_write_time_and_a_clean_pass_writes_nothing()
    {
        var writer = Writer();
        var latest = 1;
        var written = 0;
        (int, Action)? Next() => written == latest ? null : (latest, () => written = latest);

        latest = 2;
        latest = 3;
        await writer.WriteAsync(Next, saved: null, CancellationToken.None);
        await writer.WriteAsync(Next, saved: null, CancellationToken.None);

        Assert.Equal([3], _saved);
    }

    [Fact]
    public async Task A_failed_write_keeps_the_state_pending_reports_the_reason_and_logs_once_per_streak()
    {
        var writer = Writer();
        var written = false;
        (int, Action)? Next() => written ? null : (7, () => written = true);
        _diskFull = true;

        for (var i = 0; i < 3; i++)
        {
            await writer.WriteAsync(Next, saved: null, CancellationToken.None);
        }

        Assert.False(written);
        Assert.Equal("disk full", writer.Error);
        Assert.Single(_log.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("x.json could not be written: disk full; retrying soon", _log.AllText, StringComparison.Ordinal);

        _diskFull = false;
        var notified = new List<int>();
        await writer.WriteAsync(Next, notified.Add, CancellationToken.None);

        Assert.True(written);
        Assert.Null(writer.Error);
        Assert.Equal([7], _saved);
        Assert.Equal([7], notified);
        Assert.Contains("x.json is writable again", _log.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_path_is_a_failed_write_and_any_other_exception_is_not_swallowed()
    {
        var refused = new LatestStateWriter<int>("x.json", _ => throw new UnauthorizedAccessException("denied"), "n", _log.For<LatestStateWriterTests>());
        await refused.WriteAsync(() => (1, () => { }), saved: null, CancellationToken.None);
        Assert.Equal("denied", refused.Error);

        var broken = new LatestStateWriter<int>("x.json", _ => throw new InvalidOperationException("bug"), "n", _log.For<LatestStateWriterTests>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => broken.WriteAsync(() => (1, () => { }), saved: null, CancellationToken.None));
    }

    [Fact]
    public async Task The_loop_writes_after_a_wake_up_and_a_burst_of_wake_ups_is_one_write()
    {
        var writer = Writer();
        var calls = 0;
        var written = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        var loop = writer.RunAsync(
            token => writer.WriteAsync(
                () =>
                {
                    calls++;
                    return (calls, () => written.TrySetResult());
                },
                saved: null,
                token),
            retry: null,
            cts.Token);

        writer.Wake();
        writer.Wake();
        writer.Wake();
        await written.Task.WaitAsync(TimeSpan.FromSeconds(10), RealClock.Provider, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await loop;

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task While_failing_the_loop_retries_on_its_own_clock_without_a_new_wake_up()
    {
        var clock = new ObservableTimeProvider();
        var writer = Writer();
        var written = false;
        var done = new TaskCompletionSource();
        _diskFull = true;
        using var cts = new CancellationTokenSource();
        var loop = writer.RunAsync(
            token => writer.WriteAsync(() => written ? null : (5, () => { written = true; done.TrySetResult(); }), saved: null, token),
            (TimeSpan.FromSeconds(30), clock),
            cts.Token);

        writer.Wake();
        await clock.WaitForTimerAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("disk full", writer.Error);
        _diskFull = false;
        clock.Advance(TimeSpan.FromSeconds(30));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10), RealClock.Provider, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await loop;

        Assert.Equal([5], _saved);
        Assert.Null(writer.Error);
    }

    [Fact]
    public async Task Stopping_ends_the_loop_quietly_and_a_flush_afterwards_still_writes()
    {
        var writer = Writer();
        var pending = true;
        using var cts = new CancellationTokenSource();
        var loop = writer.RunAsync(_ => Task.CompletedTask, retry: null, cts.Token);

        await cts.CancelAsync();
        await loop;
        await writer.WriteAsync(() => pending ? (9, () => pending = false) : null, saved: null, CancellationToken.None);

        Assert.Equal([9], _saved);
    }

    private void Save(int value)
    {
        if (_diskFull)
        {
            throw new IOException("disk full");
        }

        _saved.Add(value);
    }
}
