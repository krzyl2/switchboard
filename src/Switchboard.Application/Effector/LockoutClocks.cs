using System.Collections.Immutable;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Guard;

namespace Switchboard.Application.Effector;

/// <summary>What the guard may know about lockouts: only to sort devices under min_on last, never to spare them.</summary>
public interface ILockoutView
{
    bool UnderMinOn(DeviceName device, DateTimeOffset now);

    TimeSpan MinOnRemaining(DeviceName device, int stage, DateTimeOffset now);

    TimeSpan MinOffRemaining(DeviceName device, int stage, DateTimeOffset now);
}

/// <summary>Which clock a lockout is read from.</summary>
public enum LockoutTimeline
{
    /// <summary>Restarted by every observed applied-state transition (Stage 2): what protection sorts by.</summary>
    Physical,

    /// <summary>
    /// Restarted by every accepted target change (in shadow mode at its virtual command instant), by
    /// a reported transition that does not confirm the target (the contacts really switched), at
    /// process start and on a backward clock step: what dispatch and overrides are held to (DISP-09).
    /// </summary>
    Target,
}

/// <summary>Dispatch's read view of the lockouts.</summary>
public interface IReadOnlyLockouts
{
    TimeSpan MinOnRemaining(DeviceName device, int stage, DateTimeOffset now, LockoutTimeline timeline);

    TimeSpan MinOffRemaining(DeviceName device, int stage, DateTimeOffset now, LockoutTimeline timeline);
}

/// <summary>
/// Minimum on and off times per device stage (03 EffectorGate), on two timelines (see
/// <see cref="LockoutTimeline"/>). Every clock starts when the process first sees the device,
/// never from a persisted expiry (SAFE-07), and restarts on a backward clock step. While a stage's
/// state is unknown both lockouts are taken as running: the stage may be on, and may just have
/// stopped. A setpoint device's stage is on while it is enabled with a setpoint above zero. Used
/// only by the guard runner's thread.
/// </summary>
public sealed class LockoutClocks : ILockoutView, IReadOnlyLockouts
{
    private readonly Dictionary<(DeviceName Device, int Stage), Clock> _physical = [];
    private readonly Dictionary<(DeviceName Device, int Stage), Clock> _target = [];
    private ImmutableDictionary<DeviceName, DeviceSpec> _specs = ImmutableDictionary<DeviceName, DeviceSpec>.Empty;

    /// <param name="setup">The setup in force; null (invalid configuration) keeps the clocks as they are.</param>
    public void Observe(ProtectionSetup? setup, ImmutableArray<DeviceStateSample> states, DateTimeOffset now)
    {
        if (setup is null)
        {
            return;
        }

        _specs = setup.Devices.ToImmutableDictionary(d => d.Id);
        foreach (var clocks in new[] { _physical, _target })
        {
            foreach (var key in clocks.Keys.Where(k => !_specs.ContainsKey(k.Device)).ToList())
            {
                clocks.Remove(key);
            }
        }

        foreach (var device in setup.Devices)
        {
            foreach (var stage in device.Stages)
            {
                var on = IsOn(device, stage.Index, states);
                var key = (device.Id, stage.Index);
                if (!_physical.TryGetValue(key, out var clock))
                {
                    _physical[key] = new Clock(on, now);
                    _target[key] = new Clock(on, now);
                }
                else if (on is { } known && clock.On is { } before && known != before)
                {
                    _physical[key] = new Clock(known, now);
                }
                else if (on is { } first && clock.On is null)
                {
                    // Learning the state is not a transition: the clock keeps running from when it started.
                    _physical[key] = clock with { On = first };
                    if (_target.TryGetValue(key, out var target) && target.On is null)
                    {
                        _target[key] = target with { On = first };
                    }
                }
            }
        }
    }

    /// <summary>A target change was accepted for a stage (commanded, or virtually commanded in shadow mode): its target lockout restarts now.</summary>
    public void Commanded(DeviceName device, int stage, bool on, DateTimeOffset now) => _target[(device, stage)] = new Clock(on, now);

    /// <summary>The stage switched without confirming the target (another controller, a manual switch): the contacts moved, the target lockout restarts now.</summary>
    public void Switched(DeviceName device, int stage, bool on, DateTimeOffset now) => _target[(device, stage)] = new Clock(on, now);

    /// <summary>A backward clock step: every lockout restarts now.</summary>
    public void RestartAll(DateTimeOffset now)
    {
        foreach (var clocks in new[] { _physical, _target })
        {
            foreach (var key in clocks.Keys.ToList())
            {
                clocks[key] = clocks[key] with { Since = now };
            }
        }
    }

    public bool UnderMinOn(DeviceName device, DateTimeOffset now) =>
        _specs.TryGetValue(device, out var spec) && spec.Stages.Any(s => MinOnRemaining(device, s.Index, now) > TimeSpan.Zero);

    public TimeSpan MinOnRemaining(DeviceName device, int stage, DateTimeOffset now) =>
        Remaining(_physical, device, stage, now, on: true);

    public TimeSpan MinOffRemaining(DeviceName device, int stage, DateTimeOffset now) =>
        Remaining(_physical, device, stage, now, on: false);

    public TimeSpan MinOnRemaining(DeviceName device, int stage, DateTimeOffset now, LockoutTimeline timeline) =>
        Remaining(timeline == LockoutTimeline.Physical ? _physical : _target, device, stage, now, on: true);

    public TimeSpan MinOffRemaining(DeviceName device, int stage, DateTimeOffset now, LockoutTimeline timeline) =>
        Remaining(timeline == LockoutTimeline.Physical ? _physical : _target, device, stage, now, on: false);

    private static bool? IsOn(DeviceSpec device, int stage, ImmutableArray<DeviceStateSample> states)
    {
        DeviceStateSample? Sample(int index) => states.IsDefault ? null : states.FirstOrDefault(s => s.Device == device.Id && s.Stage == index);
        if (Sample(stage) is not { Fresh: true } sample)
        {
            return null;
        }

        var on = sample.Applied switch
        {
            RelayApplied relay => relay.On,
            SetpointApplied setpoint => setpoint.Setpoint.Value > 0,
            _ => (bool?)null,
        };
        if (on == true && device.HasStopActuator)
        {
            return Sample(DeviceStateSample.StopStage) is { Fresh: true, Applied: RelayApplied stop } ? stop.On : null;
        }

        return on;
    }

    private TimeSpan Remaining(Dictionary<(DeviceName, int), Clock> clocks, DeviceName device, int stage, DateTimeOffset now, bool on)
    {
        if (!_specs.TryGetValue(device, out var spec) || !clocks.TryGetValue((device, stage), out var clock) || clock.On == !on)
        {
            return TimeSpan.Zero;
        }

        var lockout = on ? spec.MinOn : spec.MinOff;
        var elapsed = now - clock.Since;
        return elapsed <= TimeSpan.Zero ? lockout : elapsed >= lockout ? TimeSpan.Zero : lockout - elapsed;
    }

    private sealed record Clock(bool? On, DateTimeOffset Since);
}
