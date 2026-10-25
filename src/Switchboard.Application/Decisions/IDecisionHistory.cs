using System.Collections.Immutable;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Decisions;

/// <summary>
/// The decision log's full retained history (MODE-04), read from the daily files rather than the
/// in-memory window. Newest first, filtered, paged with an opaque cursor that stays valid while
/// today's file grows and across restarts.
/// </summary>
public interface IDecisionHistory
{
    /// <exception cref="ArgumentException">The cursor is not one this history issues.</exception>
    DecisionHistoryPage Query(DecisionHistoryQuery query);
}

/// <summary>A history search: [From, To) in UTC (the browser computes local-day bounds), filters, a page size and a cursor.</summary>
/// <param name="Cursor">The <see cref="DecisionHistoryPage.Next"/> of the previous page; null for the newest.</param>
public sealed record DecisionHistoryQuery(
    DateTimeOffset? From,
    DateTimeOffset? To,
    DeviceName? Device,
    Phase? Phase,
    ReasonCode? Reason,
    DecisionKind? Kind,
    int Limit,
    string? Cursor);

/// <param name="Next">Where the next (older) page starts; null when the retained history is exhausted.</param>
/// <param name="RetainedFrom">The UTC date of the oldest retained daily file; null when there is none.</param>
/// <param name="Scanned">Lines read for this page; a page cut by the scan budget is shorter than the limit but still has a cursor.</param>
/// <param name="Partial">The scan budget ended the page before it was full.</param>
public sealed record DecisionHistoryPage(
    ImmutableArray<Decision> Items,
    string? Next,
    DateOnly? RetainedFrom,
    int RetentionDays,
    long Scanned,
    bool Partial);
