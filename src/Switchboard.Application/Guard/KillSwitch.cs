using Microsoft.Extensions.Logging;
using Switchboard.Application.Signals;

namespace Switchboard.Application.Guard;

public interface IKillSwitchView
{
    KillSwitchStatus Status { get; }
}

/// <summary>What happened to a request to set the kill switch.</summary>
public enum KillSwitchSetResult
{
    Applied,

    /// <summary>The kill switch already was in that position.</summary>
    Unchanged,

    /// <summary>A release while state.json waits for the user's confirmation (it started corrupt): only the confirmation releases it.</summary>
    RefusedLocked,
}

/// <summary>Sets the global kill switch (SAFE-03): from Home Assistant over MQTT, or from the API.</summary>
public interface IKillSwitchControl : IKillSwitchView
{
    KillSwitchSetResult Set(bool engaged, KillSwitchSource source);
}

/// <summary>
/// The kill switch. Setting it changes memory and wakes the guard first, so it is respected within
/// one cycle whatever happens next; only then are listeners (state.json persistence) told, and a
/// listener that fails, a full disk for example, is logged and never undoes or delays the switch.
/// After a corrupt or unreadable state.json it starts engaged and locked: no release from MQTT or
/// the API until the user confirms (<see cref="Unlock"/>, through the control switch's confirmation).
/// </summary>
public sealed class KillSwitchState : IKillSwitchControl
{
    private readonly GuardInbox _inbox;
    private readonly TimeProvider _time;
    private readonly ILogger<KillSwitchState> _logger;
    private readonly object _gate = new();
    private KillSwitchStatus _status = KillSwitchStatus.Off;

    public KillSwitchState(GuardInbox inbox, SignalClock clock, ILogger<KillSwitchState> logger)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _inbox = inbox;
        _time = clock.Time;
        _logger = logger;
    }

    /// <summary>Raised after the guard has been woken; for persistence. Handlers must not block.</summary>
    public event EventHandler<KillSwitchStatus>? Changed;

    public KillSwitchStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>The value persisted in state.json, applied at start before the guard runs.</summary>
    public void Restore(bool engaged, DateTimeOffset? changedAt)
    {
        lock (_gate)
        {
            _status = engaged ? new KillSwitchStatus(true, changedAt, KillSwitchSource.Restored) : KillSwitchStatus.Off;
        }

        _inbox.Post(GuardTrigger.KillSwitch);
    }

    /// <summary>
    /// At start, when state.json was corrupt or unreadable: engaged and locked until the user
    /// confirms in the UI (maintainer survey 2026-10-25), before the guard's first evaluation.
    /// </summary>
    public void EngageLocked()
    {
        KillSwitchStatus status;
        lock (_gate)
        {
            status = new KillSwitchStatus(true, _time.GetUtcNow(), KillSwitchSource.CorruptState) { Locked = true };
            _status = status;
        }

        _inbox.Post(GuardTrigger.KillSwitch);
        _logger.LogWarning("Kill switch engaged because state.json could not be trusted; it stays engaged until the state is confirmed in the UI");
        Notify(status);
    }

    public KillSwitchSetResult Set(bool engaged, KillSwitchSource source)
    {
        KillSwitchStatus status;
        lock (_gate)
        {
            if (_status.Engaged == engaged)
            {
                return KillSwitchSetResult.Unchanged;
            }

            if (_status.Locked && !engaged)
            {
                return KillSwitchSetResult.RefusedLocked;
            }

            status = new KillSwitchStatus(engaged, _time.GetUtcNow(), source) { Locked = _status.Locked };
            _status = status;
        }

        _inbox.Post(GuardTrigger.KillSwitch);
        _logger.LogWarning("Kill switch {State} from {Source}", engaged ? "engaged" : "released", source);
        Notify(status);
        return KillSwitchSetResult.Applied;
    }

    /// <summary>
    /// The user confirmed the state after a corrupt start: the lock ends, and the kill switch is
    /// released only if it is still the one engaged by that start (one the user engaged since stays).
    /// </summary>
    /// <returns>False when nothing was locked.</returns>
    public bool Unlock()
    {
        KillSwitchStatus status;
        lock (_gate)
        {
            if (!_status.Locked)
            {
                return false;
            }

            status = _status.Source == KillSwitchSource.CorruptState
                ? new KillSwitchStatus(false, _time.GetUtcNow(), KillSwitchSource.Confirmation)
                : _status with { Locked = false };
            _status = status;
        }

        _inbox.Post(GuardTrigger.KillSwitch);
        _logger.LogWarning("State confirmed by the user; the kill switch is {State}", status.Engaged ? "still engaged" : "released");
        Notify(status);
        return true;
    }

    private void Notify(KillSwitchStatus status)
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler<KillSwitchStatus>)handler)(this, status);
            }
            catch (Exception ex)
            {
                // Persistence is best effort; the switch itself already holds.
                _logger.LogError(ex, "A kill switch listener failed; the kill switch stays {State}", status.Engaged ? "engaged" : "released");
            }
        }
    }
}
