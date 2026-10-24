using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Signals;

/// <summary>
/// Reads a device stage's applied state from its state signal (SIG-05). A relay state (and a stop
/// actuator's) must be exactly <c>on</c> or <c>off</c>; a setpoint arrives as a current the signal
/// layer has already unit-checked and normalized to A, and must not be negative, except for a
/// storage device, whose negative setpoint is a discharge. Anything else, and any signal that is
/// not fresh, leaves the state unknown, which protection counts as on.
/// </summary>
public static class DeviceStateInterpreter
{
    public const string OnState = "on";
    public const string OffState = "off";

    public static DeviceStateSample Interpret(
        DeviceName device,
        int stage,
        SignalSourceStatus source,
        SignalState state,
        SignalDetail detail,
        SignalValue? value,
        DateTimeOffset? reportedAt,
        bool allowNegativeSetpoint = false)
    {
        if (source != SignalSourceStatus.Connected)
        {
            return Unknown(DeviceStateInvalidReason.SourceDisconnected);
        }

        if (state != SignalState.Fresh)
        {
            return Unknown(state switch
            {
                SignalState.Stale => DeviceStateInvalidReason.Stale,
                SignalState.Rejected => DeviceStateInvalidReason.Unrecognized,
                _ => detail switch
                {
                    SignalDetail.AwaitingFirstSample => DeviceStateInvalidReason.Awaiting,
                    SignalDetail.EntityMissing or SignalDetail.EntityRemoved => DeviceStateInvalidReason.Missing,
                    _ => DeviceStateInvalidReason.Unavailable,
                },
            });
        }

        AppliedState? applied = value switch
        {
            StateValue { State: OnState } => new RelayApplied(true),
            StateValue { State: OffState } => new RelayApplied(false),
            CurrentValue current when current.Current.Value >= 0 || allowNegativeSetpoint => new SetpointApplied(current.Current),
            _ => null,
        };
        return applied is null
            ? Unknown(DeviceStateInvalidReason.Unrecognized)
            : new DeviceStateSample(device, stage, true, null, applied, reportedAt);

        DeviceStateSample Unknown(DeviceStateInvalidReason reason) => new(device, stage, false, reason, null, reportedAt);
    }
}
