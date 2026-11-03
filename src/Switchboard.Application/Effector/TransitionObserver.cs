using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Effector;

/// <summary>
/// What a reported change of a device means (03 EffectorGate): part of Switchboard's own command
/// showing first is progress; anything else is another controller's act, which restarts the target
/// lockouts, lets a set target follow in active mode, ends a restore baseline and is observed in the
/// history either way. Owns no state: the last reported level and the target are the ledger's.
/// Single-threaded, on the guard runner's thread.
/// </summary>
internal sealed class TransitionObserver
{
    private readonly LockoutClocks _lockouts;

    public TransitionObserver(LockoutClocks lockouts) => _lockouts = lockouts;

    public void Observe(StepContext context, DeviceSpec device, DeviceTargets.State state, DeviceLevel? reported)
    {
        if (reported is null)
        {
            return;
        }

        var previous = state.LastReported;
        state.LastReported = reported;

        // A start of Switchboard's is over once the device reports off, except while its command still has time to show (reaction time plus the
        // confirm timeout after the acknowledgement): an off then is the device not having moved yet, as a select reports its new option a moment late.
        var commandShowing = state.Commands.ConfirmBy is { } confirmBy && context.Now < confirmBy;
        if (state.Baselines.SelfStarted is not null && DeviceLevels.IsOff(device, reported) && !commandShowing)
        {
            state.Baselines.SelfStarted = null;
        }

        if (previous is null || previous.Equals(reported) || reported.Equals(state.Resolved.Target))
        {
            return;
        }

        if (state.Resolved.TargetCommanded && state.Resolved.Target is { } commanded && MovesToward(device, previous, reported, commanded))
        {
            // Part of our own command shows first (one relay of several, the setpoint before the
            // stop actuator): progress toward the target, not someone else's act.
            return;
        }

        if (device is SetpointSpec { HasStopActuator: true } && state.Resolved.TargetCommanded && state.Resolved.Target is SetpointLevel { Enabled: false } stopped
            && previous is SetpointLevel { Enabled: false } && reported is SetpointLevel { Enabled: false } reportedStop)
        {
            // Switchboard holds the device stopped through its stop actuator and only the setpoint
            // moved (the charger's app, an automation): the user chose a new level, not to stay
            // stopped, so it is the level to give back. The baseline and the target take the new
            // setpoint and the stop stands; only a move of the stop itself ends the baseline (below).
            if (state.Baselines.Baseline is { } baseline)
            {
                state.Baselines.Baseline = baseline with { Level = DeviceLevel.At(reportedStop.Setpoint, baseline.Level.Enabled) };
            }

            state.Resolved.Target = DeviceLevel.At(reportedStop.Setpoint, stopped.Enabled);
            if (state.Settling.Window is null or { Kind: WindowKind.Observation })
            {
                state.Settling.Window = SettlingVerifier.OpenWindow(context, device, WindowKind.Observation, previous, reported, applied: false, probe: false);
            }

            return;
        }

        // Someone else switched the device, and the step is observed for the history either way.
        // Where the intended state follows the real one (no target set, or active mode, which does
        // not fight another controller) its contacts moved: the target lockouts restart and a set
        // target follows. In shadow mode a set target is the would-be world and stays as it is: the
        // same intent is never recorded again because another controller undid it in reality. The
        // level the user chose is the new prior level: a restore baseline ends, and a device
        // dispatch had started is no longer Switchboard's to switch off.
        state.Baselines.ClearBaseline();
        state.Baselines.SelfStarted = null;
        var followsReality = state.Resolved.Target is null || context.Mode.Commands;
        if (followsReality)
        {
            foreach (var stage in device.Stages)
            {
                var was = DeviceLevels.StageOn(device, previous, stage.Index);
                var now = DeviceLevels.StageOn(device, reported, stage.Index);
                if (was != now)
                {
                    _lockouts.Switched(device.Id, stage.Index, now, context.Now);
                }
            }

            if (state.Resolved.Target is not null)
            {
                state.Resolved.Target = reported;
                state.Resolved.Origin = null;
                state.Resolved.Reason = null;
                state.Resolved.TargetCommanded = false;
                state.Commands.ConfirmBy = null;
                state.Commands.UnconfirmedSince = null;
                state.Commands.ProtectiveFailed = false;
            }
        }

        if (state.Settling.Window is null or { Kind: WindowKind.Observation })
        {
            state.Settling.Window = SettlingVerifier.OpenWindow(context, device, WindowKind.Observation, previous, reported, applied: false, probe: false);
        }
        else if (followsReality)
        {
            state.Settling.Window = null;
        }
    }

    /// <summary>Every component that changed between two reports now equals the target's: the device is on its way to the commanded level.</summary>
    private static bool MovesToward(DeviceSpec device, DeviceLevel previous, DeviceLevel reported, DeviceLevel target)
    {
        if (device is SetpointSpec setpointDevice)
        {
            if (previous is not SetpointLevel before || reported is not SetpointLevel now || target is not SetpointLevel wanted)
            {
                return false;
            }

            var setpointMoved = !before.Setpoint.Equals(now.Setpoint);
            var enabledMoved = before.Enabled != now.Enabled;
            return (!setpointMoved || SameSetpoint(now.Setpoint, wanted.Setpoint, setpointDevice))
                && (!enabledMoved || now.Enabled == wanted.Enabled)
                && (setpointMoved || enabledMoved);
        }

        var changed = device.Stages.Where(s => DeviceLevels.StageOn(device, previous, s.Index) != DeviceLevels.StageOn(device, reported, s.Index)).ToList();
        return changed.Count > 0 && changed.All(s => DeviceLevels.StageOn(device, reported, s.Index) == DeviceLevels.StageOn(device, target, s.Index));
    }

    internal static bool SameSetpoint(Amperes a, Amperes b, SetpointSpec device) =>
        Math.Abs(a.Value - b.Value) <= device.Range.Step.Value / 2;
}
