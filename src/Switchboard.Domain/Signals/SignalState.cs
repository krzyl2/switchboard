namespace Switchboard.Domain.Signals;

/// <summary>
/// Whether a signal may be used. Precedence when several apply: Unavailable, then Stale, then
/// Rejected. Only Fresh values ever reach protection or dispatch.
/// </summary>
public enum SignalState
{
    Fresh,

    /// <summary>No report within max_age (SIG-05).</summary>
    Stale,

    /// <summary>The latest sample was refused (invalid, or failed the balance check, SIG-09).</summary>
    Rejected,

    /// <summary>Home Assistant reports no value, the entity is missing, or nothing has arrived yet.</summary>
    Unavailable,

    /// <summary>An optional role slot of the topology that no declared signal fills.</summary>
    Unmapped,
}

/// <summary>Why a signal is in its state; shown in diagnostics.</summary>
public enum SignalDetail
{
    None,
    AwaitingFirstSample,
    EntityMissing,
    EntityRemoved,
    StateUnavailable,
    StateUnknown,
    NotNumeric,
    UnitMismatch,
    SignViolation,
    BalanceResidual,
    MaxAgeExceeded,

    /// <summary>A series signal's attribute is absent, empty or not a list or mapping (the whole series is refused).</summary>
    SeriesMissing,

    /// <summary>A series signal's attribute has a malformed item (a bad time, a non-finite or impossible value): the whole series is refused.</summary>
    SeriesMalformed,

    /// <summary>A series signal's attribute is beyond the bounds (points or bytes): dropped, never truncated.</summary>
    SeriesTooLarge,

    /// <summary>A timestamp signal's state is not an instant with a UTC offset.</summary>
    NotTimestamp,

    /// <summary>A calendar signal's attributes are malformed (a time that is not a time, an attribute of the wrong type): the whole event is refused.</summary>
    CalendarMalformed,

    /// <summary>A calendar event's description is beyond the bound: the whole event is refused, never truncated.</summary>
    CalendarDescriptionTooLong,

    /// <summary>The feed from Home Assistant is not connected, so nothing about this input is known now (the EV inputs view reports it so; the store does not).</summary>
    SourceDisconnected,
}

/// <summary>Status of whatever feeds the store (the Home Assistant subscription, or a replay).</summary>
public enum SignalSourceStatus
{
    Disconnected,
    Connecting,
    Connected,
}
