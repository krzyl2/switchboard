using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Signals;
using Switchboard.Domain.Balance;

namespace Switchboard.Application.Balance;

/// <summary>The balance of the running hour as of one store cycle (BAL-01, BAL-07).</summary>
/// <param name="At">The store cycle's time.</param>
/// <param name="UntilNextHour">Time to the next local :00.</param>
public sealed record HourlyBalanceStatus(BalanceReading Reading, DateTimeOffset At, TimeSpan UntilNextHour);

/// <summary>Read side of the hourly balance; null while no energy counters are mapped.</summary>
public interface IBalanceSource
{
    HourlyBalanceStatus? Current { get; }
}

/// <summary>
/// Keeps the hourly balance current: one observation per published store cycle, on the store's own
/// clock. A read-only listener: it never feeds protection, and an error here is logged and swallowed
/// because it runs on the thread that publishes the inputs the guard reads. The baseline of each new
/// hour is handed to its own writer loop (<see cref="RunAsync"/>), so no file I/O happens on that thread.
/// </summary>
public sealed class HourlyBalanceMonitor : IBalanceSource
{
    private readonly HourlyBalanceTracker _tracker;
    private readonly ILogger<HourlyBalanceMonitor> _logger;
    private readonly IBalanceBaselineFile? _file;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private HourlyBalanceStatus? _current;
    private HourBaseline? _pending;
    private HourBaseline? _saved;
    private bool _failing;

    /// <param name="file">Where the baseline is kept across restarts; null keeps it in memory only.</param>
    public HourlyBalanceMonitor(IControlInputSource inputs, SignalClock clock, ILogger<HourlyBalanceMonitor> logger, IBalanceBaselineFile? file = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(clock);
        _tracker = new HourlyBalanceTracker(clock.Time.LocalTimeZone);
        _logger = logger;
        _file = file;
        RestoreBaseline();

        // Subscribed last: the baseline of the running hour is back before the first cycle is seen.
        inputs.Published += OnPublished;
    }

    public HourlyBalanceStatus? Current => Volatile.Read(ref _current);

    /// <summary>Writes the baseline of each new hour from its own loop.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _wake.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                _wake.Reader.TryRead(out _);
                Flush();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping: every baseline was written when it was taken.
        }
    }

    private void RestoreBaseline()
    {
        if (_file is null)
        {
            return;
        }

        try
        {
            if (_file.Load() is { } baseline)
            {
                _tracker.Restore(baseline);
                _saved = baseline;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "The hourly balance baseline could not be loaded; the balance starts unknown until the next full hour");
        }
    }

    private void Flush()
    {
        var baseline = Volatile.Read(ref _pending);
        if (_file is null || baseline is null || baseline == _saved)
        {
            return;
        }

        try
        {
            _file.Save(baseline);
            _saved = baseline;
            if (_failing)
            {
                _logger.LogInformation("The hourly balance baseline file is writable again");
                _failing = false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_failing)
            {
                _logger.LogError("The hourly balance baseline could not be written: {Reason}; a restart inside this hour leaves the balance unknown", ex.Message);
                _failing = true;
            }
        }
    }

    private void OnPublished(object? sender, ControlInputs inputs)
    {
        try
        {
            Volatile.Write(ref _current, Observe(inputs));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "The hourly balance could not be updated; it keeps its last value");
        }
    }

    private HourlyBalanceStatus? Observe(ControlInputs inputs)
    {
        if (inputs.Dispatch.Energy is not { } energy)
        {
            return null;
        }

        var now = inputs.Dispatch.TakenAt;
        var reading = _tracker.Observe(now, energy.Counters, energy.MaxAge);
        if (_tracker.Baseline is { } baseline && baseline != Volatile.Read(ref _pending))
        {
            Volatile.Write(ref _pending, baseline);
            _wake.Writer.TryWrite(true);
        }

        return new HourlyBalanceStatus(reading, now, reading.HourStart.AddHours(1) - now);
    }
}
