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
/// because it runs on the thread that publishes the inputs the guard reads.
/// </summary>
public sealed class HourlyBalanceMonitor : IBalanceSource
{
    private readonly HourlyBalanceTracker _tracker;
    private readonly TimeZoneInfo _zone;
    private readonly ILogger<HourlyBalanceMonitor> _logger;
    private HourlyBalanceStatus? _current;

    public HourlyBalanceMonitor(IControlInputSource inputs, SignalClock clock, ILogger<HourlyBalanceMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(clock);
        _zone = clock.Time.LocalTimeZone;
        _tracker = new HourlyBalanceTracker(_zone);
        _logger = logger;
        inputs.Published += OnPublished;
    }

    public HourlyBalanceStatus? Current => Volatile.Read(ref _current);

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
        return new HourlyBalanceStatus(reading, now, reading.HourStart.AddHours(1) - now);
    }
}
