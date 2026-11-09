using System.Collections.Immutable;
using Switchboard.Domain.Devices;

namespace Switchboard.Application.Dispatch;

/// <summary>
/// The declared devices that say they cannot take energy now (2026-11-09: a charger with no car plugged in). Dispatch only narrows on it: such a
/// device does not start or rise in auto, a running one comes down, and the energy goes to the devices behind it. It is a set of names and nothing
/// else, so no signal, state or limit of its producer reaches dispatch, and the guard never reads it: protection decides from breaker data alone.
/// </summary>
public interface IDeviceReadinessSource
{
    /// <summary>The devices that cannot take energy now; empty while nothing is known (an unknown state is never "not ready").</summary>
    ImmutableHashSet<DeviceName> NotReady { get; }
}
