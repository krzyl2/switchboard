using Switchboard.Application.Goals;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;

namespace Switchboard.Application.Ev;

/// <summary>
/// The module's SoC estimate and plugged state for the manual goal of the EV device (<see cref="IModuleSocSource"/>): read from the published snapshot, so it does
/// no work and cannot throw inside the guard's step. A snapshot past its validity, a failed computation and another device give no reading, which the manual
/// goal answers with "inactive", never with the last value. The snapshot source is taken lazily: the keeper's engine reads the manual goals, so the two would
/// otherwise wait for each other when the container builds them.
/// </summary>
public sealed class EvModuleSocSource(Func<IEvSnapshotSource> snapshots, SignalClock clock) : IModuleSocSource
{
    public ModuleSocReading? Read(DeviceName device)
    {
        if (snapshots().Current.Snapshot is not { } snapshot || snapshot.Device != device || clock.Time.GetUtcNow() > snapshot.ValidUntil)
        {
            return null;
        }

        return new ModuleSocReading(snapshot.SocPercent, snapshot.Plugged);
    }
}
