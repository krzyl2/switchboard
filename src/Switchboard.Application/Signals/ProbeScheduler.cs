using Switchboard.Application.Configuration;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Signals;

/// <summary>
/// When the store has to ask Home Assistant again: signals silent for after_fraction × max_age (a
/// repeated value produces no event, only a newer last_reported), direction signals a changed
/// current is still waiting for, and the whole signal set after a clock step. Also the next moment
/// the snapshot changes without any input.
/// </summary>
internal sealed class ProbeScheduler
{
    private bool _resyncPending;

    /// <summary>Every signal is read again at the next <see cref="TakeDue"/> (the clock stepped back).</summary>
    public void RequestResync() => _resyncPending = true;

    /// <summary>Entities whose freshness should be confirmed now. Each is marked as probed.</summary>
    public IReadOnlyList<EntityId> TakeDue(SignalMap map, SignalSourceStatus source, DateTimeOffset now)
    {
        if (map.Configuration is null || source != SignalSourceStatus.Connected)
        {
            return [];
        }

        if (_resyncPending)
        {
            _resyncPending = false;
            foreach (var signal in map.Signals)
            {
                signal.LastProbeAt = now;
            }

            return [.. map.Signals.Select(s => s.Definition.Entity)];
        }

        var due = new List<SignalRuntime>();
        foreach (var signal in map.Signals)
        {
            if (ProbeEligible(signal) && now >= ProbeDue(map, signal))
            {
                due.Add(signal);
            }
        }

        foreach (var signal in map.Signals)
        {
            if (signal.Accepted is not { PairingProbeRequested: false } accepted
                || DirectionResolver.DirectionSignalOf(map, signal) is not { } direction
                || !SignalFreshness.IsFresh(direction, now)
                || DirectionResolver.Aligned(map, signal, direction)
                || now < accepted.ArrivedAt + map.Direction.PairingWait)
            {
                continue;
            }

            signal.Accepted = accepted with { PairingProbeRequested = true };
            if (ProbeEligible(direction) && !due.Contains(direction))
            {
                due.Add(direction);
            }
        }

        foreach (var signal in due)
        {
            signal.LastProbeAt = now;
        }

        return [.. due.Select(s => s.Definition.Entity)];
    }

    /// <summary>The next moment the snapshot changes without any input: a staleness transition, a probe due, a pairing wait running out.</summary>
    public DateTimeOffset? NextDeadline(SignalMap map, SignalSourceStatus source, DateTimeOffset now)
    {
        if (map.Configuration is null)
        {
            return null;
        }

        DateTimeOffset? next = null;
        void Consider(DateTimeOffset candidate)
        {
            if (candidate > now && (next is null || candidate < next))
            {
                next = candidate;
            }
        }

        foreach (var signal in map.Signals)
        {
            if (signal.Accepted is { } accepted && signal.RawStatus != RawStatus.Unavailable && signal.Definition.Freshness == StateFreshness.Age)
            {
                Consider(accepted.Effective + signal.Definition.MaxAge);
            }

            if (source == SignalSourceStatus.Connected && ProbeEligible(signal))
            {
                Consider(ProbeDue(map, signal));
            }

            if (signal.NegativeSince is { } negativeSince)
            {
                Consider(negativeSince + map.Direction.ExportHold);
            }

            if (signal.Accepted is { PairingProbeRequested: false } pending
                && DirectionResolver.DirectionSignalOf(map, signal) is { } direction
                && SignalFreshness.IsFresh(direction, now)
                && !DirectionResolver.Aligned(map, signal, direction))
            {
                Consider(pending.ArrivedAt + map.Direction.PairingWait);
            }
        }

        return next;
    }

    // An on_change state is never probed: it has no age to confirm.
    private static bool ProbeEligible(SignalRuntime signal) =>
        signal.Presence == Presence.Present && signal.Raw is not null && signal.RawStatus != RawStatus.Unavailable
        && signal.Definition.Freshness == StateFreshness.Age;

    private static DateTimeOffset ProbeDue(SignalMap map, SignalRuntime signal)
    {
        var basis = signal.Accepted?.Effective ?? signal.Raw!.Reported;
        var last = signal.LastProbeAt is { } probed && probed > basis ? probed : basis;
        return last + (signal.Definition.MaxAge * map.Configuration!.Control.Signals.FreshnessProbe.AfterFraction);
    }
}
