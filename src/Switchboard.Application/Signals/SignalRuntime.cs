using Switchboard.Application.Configuration;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Signals;

internal enum Presence
{
    Awaiting,
    Present,
    Missing,
    Removed,
}

internal enum RawStatus
{
    None,
    Accepted,
    Unavailable,
    Invalid,
    BalanceRejected,
}

/// <summary>
/// The latest state Home Assistant reported, accepted or not. <see cref="Reported"/> is clamped to
/// arrival; <see cref="HaReported"/> is Home Assistant's own last_reported, unclamped, so a repeat is
/// recognised as a confirmation only when Home Assistant's report time itself moved on.
/// </summary>
internal sealed record RawSample(string State, string? Unit, DateTimeOffset LastUpdated, DateTimeOffset Reported, DateTimeOffset HaReported);

/// <summary>
/// The value in use. <see cref="SampleReported"/> is when this value was first reported;
/// <see cref="Effective"/> is the latest report confirmed for it (pushes and probes advance it) and
/// is the freshness basis. Both are clamped to arrival, and a repeat advances them only when Home
/// Assistant's own last_reported advanced, so a clock running ahead in Home Assistant cannot extend
/// freshness.
/// </summary>
internal sealed record AcceptedSample(
    SignalValue Value,
    DateTimeOffset LastUpdated,
    DateTimeOffset SampleReported,
    DateTimeOffset Effective,
    DateTimeOffset ArrivedAt,
    bool PairingProbeRequested);

/// <summary>Report intervals of one signal within one subscription, for refresh detection (SIG-07).</summary>
internal sealed class RefreshTracker
{
    private readonly Queue<TimeSpan> _intervals = new();
    private DateTimeOffset? _last;

    // The estimate is read on every snapshot but changes only with a new interval.
    private RefreshDetectionParameters? _estimatedWith;
    private TimeSpan? _estimate;

    public int Count => _intervals.Count;

    public void Observe(DateTimeOffset reported, int window)
    {
        if (_last is { } last)
        {
            if (reported <= last)
            {
                return;
            }

            _intervals.Enqueue(reported - last);
            while (_intervals.Count > window)
            {
                _intervals.Dequeue();
            }

            _estimatedWith = null;
        }

        _last = reported;
    }

    /// <summary>The next report starts a new chain: no interval spans a reconnect, a resubscription or an outage.</summary>
    public void Break() => _last = null;

    public TimeSpan? Estimate(RefreshDetectionParameters parameters)
    {
        if (!ReferenceEquals(parameters, _estimatedWith))
        {
            _estimate = RefreshInterval.Estimate(_intervals, parameters.Quantile, parameters.MinIntervals);
            _estimatedWith = parameters;
        }

        return _estimate;
    }
}

/// <summary>Mutable per-signal state, owned by the store and touched only by its single reader.</summary>
internal sealed class SignalRuntime(SignalDefinition definition)
{
    public SignalDefinition Definition { get; set; } = definition;

    public Presence Presence { get; set; } = Presence.Awaiting;

    public RawSample? Raw { get; set; }

    public RawStatus RawStatus { get; set; }

    public SignalDetail RawDetail { get; set; }

    public AcceptedSample? Accepted { get; set; }

    public RefreshTracker Refresh { get; } = new();

    public DateTimeOffset? LastProbeAt { get; set; }

    public long RejectedCount { get; set; }

    /// <summary>The same entity read the same way: its samples stay valid across a configuration reload.</summary>
    public bool ReadsLike(SignalDefinition other) =>
        Definition.Entity == other.Entity
        && Definition.Kind == other.Kind
        && string.Equals(Definition.Unit, other.Unit, StringComparison.Ordinal)
        && Definition.Sign == other.Sign;

    public void MarkAbsent(Presence presence)
    {
        Presence = presence;
        Raw = null;
        Accepted = null;
        RawStatus = RawStatus.None;
        RawDetail = SignalDetail.None;
        Refresh.Break();
    }
}
