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
}

/// <summary>Status of whatever feeds the store (the Home Assistant subscription, or a replay).</summary>
public enum SignalSourceStatus
{
    Disconnected,
    Connecting,
    Connected,
}
