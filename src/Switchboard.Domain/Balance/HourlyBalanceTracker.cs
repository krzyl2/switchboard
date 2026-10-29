namespace Switchboard.Domain.Balance;

/// <summary>The meter's summed-phase energy counters in kWh, as last reported (BAL-01).</summary>
public readonly record struct EnergyCounters(double ExportKwh, double ImportKwh, DateTimeOffset ReportedAt);

/// <summary>The counters at the start of a local hour; persisted so a restart inside the hour keeps it.</summary>
public sealed record HourBaseline(DateTimeOffset HourStart, double ExportKwh, double ImportKwh);

/// <summary>Why the balance of the running hour cannot be trusted.</summary>
public enum BalanceUnknown
{
    None,

    /// <summary>The counters are unavailable, unknown or missing right now.</summary>
    Unavailable,

    /// <summary>The last counter report is older than max_age; the balance returns with the next report.</summary>
    Stale,

    /// <summary>No counter report at or before the start of this hour (a start inside the hour); the balance returns at the next :00.</summary>
    NoBaseline,

    /// <summary>A counter decreased inside this hour; the balance returns at the next :00.</summary>
    CounterReset,

    /// <summary>The clock stepped back inside this hour; the balance returns at the next :00.</summary>
    ClockStepBack,
}

/// <summary>The balance of the running hour: positive means net export, in Wh (04 §7a, 13-HOURLY-BALANCE).</summary>
public readonly record struct BalanceReading(double? Wh, DateTimeOffset HourStart, BalanceUnknown Reason)
{
    public bool IsKnown => Wh is not null;
}

/// <summary>
/// The energy balance of the running local hour from the meter's total export and import counters:
/// <code>
/// balance = (export − export₀) − (import − import₀)      [kWh → Wh]
/// </code>
/// where the baseline is the last counter report at or before the start of the hour; only when there
/// is none (a start inside the hour) it is the first report after it, and the balance then covers the
/// hour from that report on. The counters are cumulative, so a gap in reports does not lose energy and the balance
/// is right again as soon as a fresh report arrives; only a decrease, a missing baseline or a clock step
/// back make it unknown for the rest of the hour. Pure state machine driven by the caller's clock; hours
/// are local clock hours of <c>zone</c> (23 and 25 hour days included, a repeated clock hour is two hours).
/// </summary>
public sealed class HourlyBalanceTracker(TimeZoneInfo zone)
{
    private readonly List<EnergyCounters> _history = [];
    private HourBaseline? _restored;
    private HourBaseline? _baseline;
    private DateTimeOffset? _lastNow;
    private DateTimeOffset? _hour;
    private BalanceUnknown _poison;

    /// <summary>The baseline of the running hour, or null when there is none; persisted by the caller.</summary>
    public HourBaseline? Baseline => _baseline;

    /// <summary>Offers a persisted baseline; it is used only if it belongs to the first hour observed.</summary>
    public void Restore(HourBaseline? baseline) => _restored = baseline;

    public static DateTimeOffset HourStartOf(DateTimeOffset instant, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        return local.AddTicks(-(local.Ticks % TimeSpan.TicksPerHour));
    }

    /// <param name="now">The injected clock.</param>
    /// <param name="counters">The counters now, or null when unavailable or unknown.</param>
    /// <param name="maxAge">How long a counter may be silent; read every call so a changed configuration keeps the baseline.</param>
    public BalanceReading Observe(DateTimeOffset now, EnergyCounters? counters, TimeSpan maxAge)
    {
        var hour = HourStartOf(now, zone);

        if (_hour != hour)
        {
            _hour = hour;
            _poison = BalanceUnknown.None;
            _baseline = null;
        }

        if (_lastNow is { } last && now < last)
        {
            _poison = BalanceUnknown.ClockStepBack;
        }

        _lastNow = now;

        if (counters is { } reading)
        {
            Record(reading);
        }

        if (_baseline is null && _poison == BalanceUnknown.None)
        {
            _baseline = ChooseBaseline(hour, maxAge);
            _restored = null;
        }

        Prune(hour, maxAge);
        return Evaluate(now, hour, counters, maxAge);
    }

    private void Record(EnergyCounters reading)
    {
        if (_history.Count > 0)
        {
            var previous = _history[^1];
            if (reading.ReportedAt <= previous.ReportedAt)
            {
                return;
            }

            if (reading.ExportKwh < previous.ExportKwh || reading.ImportKwh < previous.ImportKwh)
            {
                _poison = BalanceUnknown.CounterReset;
                _baseline = null;
                _history.Clear();
            }
        }

        _history.Add(reading);
    }

    private HourBaseline? ChooseBaseline(DateTimeOffset hour, TimeSpan maxAge)
    {
        if (_restored is { } restored && restored.HourStart == hour)
        {
            return restored;
        }

        for (var i = _history.Count - 1; i >= 0; i--)
        {
            var candidate = _history[i];
            if (candidate.ReportedAt <= hour)
            {
                if (hour - candidate.ReportedAt <= maxAge)
                {
                    return new HourBaseline(hour, candidate.ExportKwh, candidate.ImportKwh);
                }

                break;
            }
        }

        // No usable report at or before :00 (a start inside the hour, or a silent meter across :00):
        // the balance counts from the first report of this hour. It misses what flowed before it.
        var first = _history.Find(r => r.ReportedAt > hour);
        return first.ReportedAt > hour ? new HourBaseline(hour, first.ExportKwh, first.ImportKwh) : null;
    }

    /// <summary>Reports older than max_age before this hour can serve no baseline now or later.</summary>
    private void Prune(DateTimeOffset hour, TimeSpan maxAge) => _history.RemoveAll(r => hour - r.ReportedAt > maxAge);

    private BalanceReading Evaluate(DateTimeOffset now, DateTimeOffset hour, EnergyCounters? counters, TimeSpan maxAge)
    {
        if (_poison != BalanceUnknown.None)
        {
            return new BalanceReading(null, hour, _poison);
        }

        if (_baseline is null)
        {
            return new BalanceReading(null, hour, _history.Count == 0 ? BalanceUnknown.Unavailable : BalanceUnknown.NoBaseline);
        }

        if (counters is null || _history.Count == 0)
        {
            return new BalanceReading(null, hour, BalanceUnknown.Unavailable);
        }

        var latest = _history[^1];
        if (now - latest.ReportedAt > maxAge)
        {
            return new BalanceReading(null, hour, BalanceUnknown.Stale);
        }

        var wh = ((latest.ExportKwh - _baseline.ExportKwh) - (latest.ImportKwh - _baseline.ImportKwh)) * 1000;
        return new BalanceReading(wh, hour, BalanceUnknown.None);
    }
}
