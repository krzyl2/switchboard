using Microsoft.Extensions.Logging;
using Switchboard.Application.Signals;

namespace Switchboard.Application.Guard;

public interface IKillSwitchView
{
    KillSwitchStatus Status { get; }
}

/// <summary>Sets the global kill switch (SAFE-03): from Home Assistant over MQTT, or from the API.</summary>
public interface IKillSwitchControl : IKillSwitchView
{
    void Set(bool engaged, KillSwitchSource source);
}

/// <summary>
/// The kill switch. Setting it changes memory and wakes the guard first, so it is respected within
/// one cycle whatever happens next; only then are listeners (state.json persistence) told, and a
/// listener that fails, a full disk for example, is logged and never undoes or delays the switch.
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

    public void Set(bool engaged, KillSwitchSource source)
    {
        KillSwitchStatus status;
        lock (_gate)
        {
            if (_status.Engaged == engaged)
            {
                return;
            }

            status = new KillSwitchStatus(engaged, _time.GetUtcNow(), source);
            _status = status;
        }

        _inbox.Post(GuardTrigger.KillSwitch);
        _logger.LogWarning("Kill switch {State} from {Source}", engaged ? "engaged" : "released", source);

        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler<KillSwitchStatus>)handler)(this, status);
            }
            catch (Exception ex)
            {
                // Persistence is best effort; the switch itself already holds.
                _logger.LogError(ex, "A kill switch listener failed; the kill switch stays {State}", engaged ? "engaged" : "released");
            }
        }
    }
}
