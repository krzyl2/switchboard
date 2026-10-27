using Switchboard.Application.Connectivity;
using Switchboard.Application.Guard;
using Switchboard.Application.Planning;

namespace Switchboard.Host.Composition;

/// <summary>
/// The planner's one question to the rest of the add-on: must it stand aside (the guard is in safe state, the kill switch is engaged, Home Assistant is
/// disconnected)? It lives at the host's edge so that the planner code names no guard type; it only reads, and only a change of the answer is passed on.
/// </summary>
internal sealed class PlannerSuspension : IPlannerSuspension
{
    private readonly IGuardStatusSource _guard;
    private readonly IKillSwitchView _killSwitch;
    private readonly IHomeAssistantConnectionState _home;
    private readonly object _gate = new();
    private bool _last;

    public PlannerSuspension(IGuardStatusSource guard, IKillSwitchView killSwitch, IHomeAssistantConnectionState home)
    {
        _guard = guard;
        _killSwitch = killSwitch;
        _home = home;
        _last = Suspended;
        _guard.Published += (_, _) => Raise();
        _home.Changed += (_, _) => Raise();
    }

    public bool Suspended => _guard.Current.SafeState.Active || _killSwitch.Status.Engaged || _home.Current != ConnectionState.Connected;

    public event EventHandler? Changed;

    // The guard publishes at every step: only a change of the answer wakes the planner.
    private void Raise()
    {
        var now = Suspended;
        lock (_gate)
        {
            if (now == _last)
            {
                return;
            }

            _last = now;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
