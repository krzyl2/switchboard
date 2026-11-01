using System.Collections.Immutable;
using Switchboard.Application.Configuration;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Signals;

/// <summary>
/// Takes a batch of states into the runtimes: units and signs normalized (SIG-02), late and repeated
/// reports told apart (SIG-06), refresh intervals observed (SIG-07), the balance check applied to
/// the triggering roles (SIG-09). The rejection counters belong to the process, not to a document.
/// </summary>
internal sealed class SampleIntake(BalanceChecker balance)
{
    private long _balanceRejections;
    private long _invalidRejections;

    public RejectionCounters Rejections => new(_balanceRejections, _invalidRejections);

    public void Apply(SignalMap map, EntityUpdateBatch batch, DateTimeOffset now)
    {
        if (map.Configuration is null)
        {
            return;
        }

        foreach (var id in batch.Removed)
        {
            foreach (var signal in map.Find(id))
            {
                signal.MarkAbsent(Presence.Removed);
            }
        }

        foreach (var id in batch.Missing)
        {
            foreach (var signal in map.Find(id))
            {
                signal.MarkAbsent(Presence.Missing);
            }
        }

        foreach (var update in batch.Updates)
        {
            foreach (var signal in map.Find(update.Entity))
            {
                if (batch.Origin == BatchOrigin.Initial)
                {
                    signal.Refresh.Break();
                }

                ApplyUpdate(map, signal, update, batch.Origin, now);
            }
        }
    }

    /// <summary>
    /// Reads the last state of an entity again under a changed definition, as a new initial state
    /// would deliver it. The report time stays the one Home Assistant gave: re-reading never makes a
    /// sample younger, and the options it reported stay known (a select may not report again for hours).
    /// </summary>
    public void Reread(SignalMap map, SignalRuntime runtime, RawSample raw, DateTimeOffset now)
    {
        ApplyUpdate(map, runtime, new EntityUpdate(runtime.Definition.Entity, raw.State, raw.Unit, raw.LastUpdated, raw.Reported) { Options = raw.Options, Series = raw.Series, Attributes = Attributes(runtime.Definition, raw.AttributeText), Calendar = raw.Calendar }, BatchOrigin.Probe, now);
        runtime.Raw = runtime.Raw! with { HaReported = raw.HaReported };
    }

    /// <summary>The one attribute text a timestamp signal re-reads after a reload; null when it has none (and for every other signal).</summary>
    private static ImmutableDictionary<string, string>? Attributes(SignalDefinition definition, string? text) =>
        definition.Attribute is { } name && text is not null ? ImmutableDictionary<string, string>.Empty.Add(name, text) : null;

    private void ApplyUpdate(SignalMap map, SignalRuntime signal, EntityUpdate update, BatchOrigin origin, DateTimeOffset now)
    {
        var reported = update.Reported < now ? update.Reported : now;
        if (signal.Raw is { } raw)
        {
            // Older than what we hold: a late probe answer or a reordered event. It must never
            // revert a newer pushed value.
            if (update.LastUpdated < raw.LastUpdated)
            {
                return;
            }

            // The same state, reported again: freshness only (SIG-06), and only when Home Assistant's
            // own report time moved on. Comparing the clamped time instead would let a clock running
            // ahead in Home Assistant re-clamp the same frozen report to every later arrival.
            if (update.LastUpdated == raw.LastUpdated)
            {
                if (update.Reported <= raw.HaReported)
                {
                    return;
                }

                var confirmed = reported > raw.Reported ? reported : raw.Reported;
                signal.Raw = raw with { Reported = confirmed, HaReported = update.Reported };
                if (signal.RawStatus == RawStatus.Accepted && signal.Accepted is { } held && confirmed > held.Effective)
                {
                    signal.Accepted = held with { Effective = confirmed };
                }

                return;
            }
        }

        // A probe answer carries no attributes: it may confirm a series (the same-state path above), never replace it. The push of a
        // new state brings the new series.
        if (signal.Definition.Series is not null && update.Series is null)
        {
            return;
        }

        // The same for a calendar event: only a push carries the attributes.
        if (signal.Definition.Kind == SignalKind.CalendarEvent && update.Calendar is null)
        {
            return;
        }

        string? attributeText = null;
        if (signal.Definition.Attribute is { } attribute && (attributeText = update.Attributes?.GetValueOrDefault(attribute)) is null)
        {
            return;
        }

        var normalized = SignalNormalizer.Normalize(signal.Definition, update.State, update.Unit, update.Series, attributeText, update.Calendar);
        signal.Presence = Presence.Present;
        signal.Raw = new RawSample(update.State, update.Unit, update.LastUpdated, reported, update.Reported) { Options = update.Options, Series = update.Series, AttributeText = attributeText, Calendar = update.Calendar };
        if (normalized.Outcome == NormalizationOutcome.Unavailable)
        {
            signal.RawStatus = RawStatus.Unavailable;
            signal.RawDetail = normalized.Detail;
            signal.Refresh.Break();
            return;
        }

        // Refresh is measured on pushed reports only: an initial state or a probe answer says
        // nothing about how often the source reports.
        if (origin is BatchOrigin.Change or BatchOrigin.Added)
        {
            signal.Refresh.Observe(reported, map.Configuration!.Control.Signals.Refresh.WindowIntervals);
        }

        if (normalized.Outcome == NormalizationOutcome.Rejected)
        {
            signal.RawStatus = RawStatus.Invalid;
            signal.RawDetail = normalized.Detail;
            signal.RejectedCount++;
            _invalidRejections++;
            return;
        }

        var previous = signal.Accepted;
        signal.Accepted = new AcceptedSample(normalized.Value!, update.LastUpdated, reported, reported, now, PairingProbeRequested: false);
        signal.RawStatus = RawStatus.Accepted;
        signal.RawDetail = SignalDetail.None;

        if (signal.Definition.Role == SignalRole.PhasePowerFactor)
        {
            // The hold runs from the report that began the negative run, so a pair of glitch samples never reaches export_hold.
            signal.NegativeSince = signal.Magnitude() < -map.Direction.PowerFactorDeadband ? signal.NegativeSince ?? reported : null;
        }

        if (BalanceChecker.IsTrigger(signal.Definition.Role) && !balance.Accepts(map, signal, now))
        {
            // The sample is rejected, never the decision: the last accepted value stays in use, and
            // ages (see SignalFreshness.Classify).
            signal.Accepted = previous;
            signal.RawStatus = RawStatus.BalanceRejected;
            signal.RawDetail = SignalDetail.BalanceResidual;
            signal.RejectedCount++;
            _balanceRejections++;
        }
    }
}
