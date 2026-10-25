using Switchboard.Domain.Devices;

namespace Switchboard.Application.Effector;

/// <summary>What the active phase self-test wants of its device now: one relay stage on or off.</summary>
public sealed record SelfTestTarget(Guid Test, DeviceName Device, int Stage, bool On);

/// <summary>
/// The one active phase self-test (CONN-05) as the effector gate and dispatch see it. The test
/// service writes it, the gate turns it into ordinary changes (origin self_test, reason
/// manual_override) below protection, and dispatch leaves the tested device alone and holds every
/// other increase back while it runs. Thread-safe: written from the API and the guard thread.
/// </summary>
public sealed class SelfTestSlot
{
    private SelfTestTarget? _current;

    public SelfTestTarget? Current => Volatile.Read(ref _current);

    public void Set(SelfTestTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Volatile.Write(ref _current, target);
    }

    /// <summary>Empties the slot if it still belongs to <paramref name="test"/>.</summary>
    public void Clear(Guid test)
    {
        var current = Volatile.Read(ref _current);
        if (current?.Test == test)
        {
            Interlocked.CompareExchange(ref _current, null, current);
        }
    }
}
