using System.Collections.Immutable;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;

namespace Switchboard.Application.Ev;

/// <summary>
/// The EV module's one answer to dispatch (2026-11-09, docs/EV.md section 3 "Not plugged"): the charger is not ready while the car is known to be
/// unplugged, so dispatch does not start it and its surplus goes to the devices behind it; once the car is plugged in it is ready again and the
/// value order hands it the energy. Only a fresh plugged state outside the configured plugged states counts: an unavailable or stale one (a lost
/// go-e link) is not "not plugged", and dispatch stays as it was. Dispatch gets the device's name only, never the state or any other EV input.
/// </summary>
public sealed class EvDeviceReadiness(IEvInputSource inputs) : IDeviceReadinessSource
{
    public ImmutableHashSet<DeviceName> NotReady =>
        inputs.Current is { Configuration.Configuration.Ev: { } ev, Inputs: { Plugged.Configured: true, IsPlugged: false } }
            ? [ev.Device]
            : [];
}
