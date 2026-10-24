using Switchboard.Domain.Devices;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Effector;

/// <summary>
/// The command side of the effector gate (03, DISP-01): logical device names and levels only,
/// never an entity ID. Only the effector gate calls it, only in active mode, and only for a device
/// of the last valid configuration (SAFE-05). This version has no production implementation (the
/// maintainer's rule: nothing may command a Home Assistant entity outside a test double); its only
/// implementation is a recording double in the tests, and the gate accepts one through an internal
/// constructor only.
/// </summary>
public interface IDeviceCommandPort
{
    /// <param name="stage">The relay stage number, starting at 1.</param>
    void SetRelay(DeviceName device, int stage, bool on);

    void SetSetpoint(DeviceName device, Amperes setpoint);

    /// <summary>A modulating device's stop actuator: false switches it off.</summary>
    void SetEnabled(DeviceName device, bool enabled);
}
