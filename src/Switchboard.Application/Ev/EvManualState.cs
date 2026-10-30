using Switchboard.Domain.Ev;

namespace Switchboard.Application.Ev;

/// <summary>The user's side of the module: the manual mode and a running "charge now" (EV-09). Pure data.</summary>
public sealed record EvManual(EvMode Mode, EvChargeNow? ChargeNow)
{
    public static EvManual Default { get; } = new(EvMode.Auto, null);
}

/// <summary>
/// The manual mode and charge-now held in memory, one value for the whole process: the web API, the MQTT entities and the engine all read and change it here,
/// each change atomic. The engine is the only writer of <c>ev.json</c>: it seeds this from the file once, takes every later change from here and persists it on
/// its next computation, so there are never two writers of the file. A field the user changed before the engine has seeded wins over the file, field by
/// field: a charge-now asked for first must not discard the mode the file held.
/// </summary>
public sealed class EvManualState
{
    private readonly object _gate = new();
    private EvManual _value = EvManual.Default;
    private bool _seeded;
    private bool _modeTouched;
    private bool _chargeNowTouched;

    /// <summary>Raised after every change, outside the lock; handlers must not block (MQTT notes it, the planner is woken).</summary>
    public event EventHandler? Changed;

    public EvManual Current
    {
        get
        {
            lock (_gate)
            {
                return _value;
            }
        }
    }

    /// <summary>Takes over what <c>ev.json</c> held, once; a field changed before this wins (the user acted first), the other comes from the file.</summary>
    public void Seed(EvMode mode, EvChargeNow? chargeNow)
    {
        lock (_gate)
        {
            if (_seeded)
            {
                return;
            }

            _seeded = true;
            _value = new EvManual(_modeTouched ? _value.Mode : mode, _chargeNowTouched ? _value.ChargeNow : chargeNow);
        }
    }

    /// <summary>Applies <paramref name="change"/> atomically; true when the value changed.</summary>
    public bool Update(Func<EvManual, EvManual> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var next = change(_value);
            if (next == _value)
            {
                return false;
            }

            if (!_seeded)
            {
                _modeTouched |= next.Mode != _value.Mode;
                _chargeNowTouched |= next.ChargeNow != _value.ChargeNow;
            }

            _value = next;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
