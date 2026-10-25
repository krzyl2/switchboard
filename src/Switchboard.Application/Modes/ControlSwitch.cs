using Microsoft.Extensions.Logging;
using Switchboard.Application.Guard;
using Switchboard.Application.Signals;

namespace Switchboard.Application.Modes;

/// <summary>Who set the control switch last.</summary>
public enum ControlSwitchSource
{
    /// <summary>Never set: a fresh install, or migrated from a state.json written before 0.7.0 (off).</summary>
    Default,
    Api,
    Mqtt,

    /// <summary>Restored from state.json at start.</summary>
    Restored,
}

/// <summary>Why control is locked off at start.</summary>
public enum ControlLockReason
{
    /// <summary>state.json was corrupt or unreadable: control off and the kill switch on until the user confirms.</summary>
    StateFileCorrupt,
}

/// <summary>The global "control allowed" switch (MODE-02) and whether it waits for a confirmation.</summary>
public sealed record ControlSwitchStatus(bool Allowed, DateTimeOffset? ChangedAt, ControlSwitchSource Source, bool Locked, ControlLockReason? LockReason)
{
    /// <summary>A fresh install: control off, nothing locked (shadow stays the default, D7).</summary>
    public static ControlSwitchStatus Default { get; } = new(false, null, ControlSwitchSource.Default, false, null);
}

public enum ControlSetResult
{
    Applied,

    /// <summary>The switch already was in that position.</summary>
    Unchanged,

    /// <summary>Turning control on while state.json waits for the user's confirmation.</summary>
    RefusedLocked,
}

public interface IControlSwitchView
{
    ControlSwitchStatus Status { get; }
}

/// <summary>Sets the control switch from the API or Home Assistant (MQTT), and confirms a corrupt start.</summary>
public interface IControlSwitch : IControlSwitchView
{
    ControlSetResult Set(bool allowed, ControlSwitchSource source);

    /// <summary>The user confirmed the state after a corrupt start; false when nothing was locked.</summary>
    bool Confirm();
}

/// <summary>
/// The global "control allowed" switch (maintainer survey 2026-10-25), default off. Commands need
/// it on together with the add-on option active (the ceiling) and a valid configuration. Turning it
/// off first tells the command path to stop (<see cref="Closing"/>, synchronously, so nothing queued
/// leaves after the call returns), then wakes the guard, which records the mode change and keeps
/// computing decisions without sending them; devices are left exactly as they are. Listeners
/// (state.json, written at once) are told last; a failing listener never undoes the switch.
/// </summary>
public sealed class ControlSwitchState : IControlSwitch
{
    private readonly GuardInbox _inbox;
    private readonly KillSwitchState _killSwitch;
    private readonly TimeProvider _time;
    private readonly ILogger<ControlSwitchState> _logger;
    private readonly object _gate = new();
    private ControlSwitchStatus _status = ControlSwitchStatus.Default;

    public ControlSwitchState(GuardInbox inbox, KillSwitchState killSwitch, SignalClock clock, ILogger<ControlSwitchState> logger)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _inbox = inbox;
        _killSwitch = killSwitch;
        _time = clock.Time;
        _logger = logger;
    }

    /// <summary>Raised after the guard has been woken; for persistence. Handlers must not block.</summary>
    public event EventHandler<ControlSwitchStatus>? Changed;

    /// <summary>
    /// Raised synchronously, before the guard is woken, whenever commanding stops (switched off, or
    /// locked): the command path drops what it has not sent yet. Handlers must not block.
    /// </summary>
    public event EventHandler? Closing;

    public ControlSwitchStatus Status
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
    public void Restore(ControlSwitchStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_gate)
        {
            _status = status.Locked
                ? status with { Allowed = false }
                : status with { Source = status.Source == ControlSwitchSource.Default ? ControlSwitchSource.Default : ControlSwitchSource.Restored };
        }

        if (_status.Locked)
        {
            _killSwitch.EngageLocked();
        }

        _inbox.Post(GuardTrigger.Mode);
    }

    /// <summary>
    /// At start, when state.json was corrupt or unreadable: control off and locked, the kill switch
    /// engaged and locked, until the user confirms (<see cref="Confirm"/>).
    /// </summary>
    public void Lock(ControlLockReason reason)
    {
        ControlSwitchStatus status;
        lock (_gate)
        {
            status = new ControlSwitchStatus(false, _time.GetUtcNow(), ControlSwitchSource.Default, true, reason);
            _status = status;
        }

        RaiseClosing();
        _killSwitch.EngageLocked();
        _inbox.Post(GuardTrigger.Mode);
        _logger.LogWarning("Control is locked off ({Reason}) until the state is confirmed in the UI", reason);
        Notify(status);
    }

    public ControlSetResult Set(bool allowed, ControlSwitchSource source)
    {
        ControlSwitchStatus status;
        lock (_gate)
        {
            if (_status.Allowed == allowed)
            {
                return ControlSetResult.Unchanged;
            }

            if (allowed && _status.Locked)
            {
                return ControlSetResult.RefusedLocked;
            }

            status = _status with { Allowed = allowed, ChangedAt = _time.GetUtcNow(), Source = source };
            _status = status;
        }

        if (!allowed)
        {
            RaiseClosing();
        }

        _inbox.Post(GuardTrigger.Mode);
        _logger.LogWarning("Control {State} from {Source}", allowed ? "allowed" : "not allowed", source);
        Notify(status);
        return ControlSetResult.Applied;
    }

    public bool Confirm()
    {
        ControlSwitchStatus status;
        lock (_gate)
        {
            if (!_status.Locked)
            {
                return false;
            }

            status = _status with { Locked = false, LockReason = null, ChangedAt = _time.GetUtcNow() };
            _status = status;
        }

        // Control stays off: confirming the state is not allowing control, which needs its own act.
        _killSwitch.Unlock();
        _inbox.Post(GuardTrigger.Mode);
        _logger.LogWarning("The state after a corrupt state.json was confirmed by the user; control stays off until it is allowed");
        Notify(status);
        return true;
    }

    private void RaiseClosing()
    {
        foreach (var handler in Closing?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler)handler)(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A control-closing listener failed");
            }
        }
    }

    private void Notify(ControlSwitchStatus status)
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler<ControlSwitchStatus>)handler)(this, status);
            }
            catch (Exception ex)
            {
                // Persistence is best effort; the switch itself already holds.
                _logger.LogError(ex, "A control switch listener failed; control stays {State}", status.Allowed ? "allowed" : "not allowed");
            }
        }
    }
}
