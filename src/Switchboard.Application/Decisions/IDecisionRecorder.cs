using System.Collections.Immutable;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Decisions;

/// <summary>
/// Where decisions go (MODE-03): the daily decision log plus a recent-decisions memory for the API.
/// <see cref="Record"/> is called on the guard's thread and must never block on I/O or throw for a
/// storage problem: an implementation queues the write (bounded) and reports trouble in
/// <see cref="Status"/>, so a slow or full disk can never delay protection.
/// </summary>
public interface IDecisionRecorder
{
    void Record(IReadOnlyList<Decision> decisions);

    /// <summary>Recent decisions, newest first; throws <see cref="ArgumentException"/> for a cursor this recorder did not issue.</summary>
    DecisionPage Query(DecisionQuery query);

    DecisionLogStatus Status { get; }
}

/// <summary>Recent decisions, newest first, optionally filtered.</summary>
/// <param name="Before">An opaque cursor from a previous page's <see cref="DecisionPage.Next"/>.</param>
public sealed record DecisionQuery(DeviceName? Device, Phase? Phase, ReasonCode? Reason, int Limit, string? Before)
{
    /// <summary>Only decisions of this kind (<see cref="DecisionKinds.Of"/>); null for every kind.</summary>
    public DecisionKind? Kind { get; init; }
}

/// <param name="Next">The cursor for the following (older) page; null when there is none.</param>
public sealed record DecisionPage(ImmutableArray<Decision> Items, string? Next, bool LogWritable);

/// <param name="Dropped">Decisions lost because the write queue was full while the log could not be written.</param>
public sealed record DecisionLogStatus(bool Writable, string? LastError, long Dropped);
