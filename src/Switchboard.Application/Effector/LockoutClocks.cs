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

/// <summary>
/// Minimum on and off times per device stage (03 EffectorGate). Every clock starts when the
/// process first sees the device, never from a persisted expiry (SAFE-07), and restarts on each
/// observed applied-state transition and on a backward clock step. While a stage's state is
/// unknown both lockouts are taken as running: the stage may be on, and may just have stopped.
/// Used only by the guard thread.
/// </summary>
public sealed class LockoutClocks : ILockoutView
{
    private readonly Dictionary<(DeviceName Device, int Stage), Clock> _clocks = [];
    private ImmutableDictionary<DeviceName, DeviceSpec> _specs = ImmutableDictionary<DeviceName, DeviceSpec>.Empty;

    /// <param name="setup">The setup in force; null (invalid configuration) keeps the clocks as they are.</param>
    public void Observe(ProtectionSetup? setup, ImmutableArray<DeviceStateSample> states, DateTimeOffset now)
    {
        if (setup is null)
        {
            return;
        }

        _specs = setup.Devices.ToImmutableDictionary(d => d.Id);
        foreach (var key in _clocks.Keys.Where(k => !_specs.ContainsKey(k.Device)).ToList())
        {
            _clocks.Remove(key);
        }

        foreach (var device in setup.Devices)
        {
            foreach (var stage in device.Stages)
            {
                var sample = states.IsDefault ? null : states.FirstOrDefault(s => s.Device == device.Id && s.Stage == stage.Index);
                var on = sample is { Fresh: true } ? IsOn(sample.Applied) : null;
                var key = (device.Id, stage.Index);
                if (!_clocks.TryGetValue(key, out var clock))
                {
                    _clocks[key] = new Clock(on, now);
                }
                else if (on is { } known && clock.On is { } before && known != before)
                {
                    _clocks[key] = new Clock(known, now);
                }
                else if (on is { } first && clock.On is null)
                {
                    // Learning the state is not a transition: the clock keeps running from when it started.
                    _clocks[key] = clock with { On = first };
                }
            }
        }
    }

    /// <summary>A backward clock step: every lockout restarts now.</summary>
    public void RestartAll(DateTimeOffset now)
    {
        foreach (var key in _clocks.Keys.ToList())
        {
            _clocks[key] = _clocks[key] with { Since = now };
        }
    }

    public bool UnderMinOn(DeviceName device, DateTimeOffset now) =>
        _specs.TryGetValue(device, out var spec) && spec.Stages.Any(s => MinOnRemaining(device, s.Index, now) > TimeSpan.Zero);

    public TimeSpan MinOnRemaining(DeviceName device, int stage, DateTimeOffset now) =>
        Remaining(device, stage, now, on: true);

    public TimeSpan MinOffRemaining(DeviceName device, int stage, DateTimeOffset now) =>
        Remaining(device, stage, now, on: false);

    private static bool? IsOn(AppliedState? applied) => applied switch
    {
        RelayApplied relay => relay.On,
        SetpointApplied setpoint => setpoint.Setpoint.Value > 0,
        _ => null,
    };

    private TimeSpan Remaining(DeviceName device, int stage, DateTimeOffset now, bool on)
    {
        if (!_specs.TryGetValue(device, out var spec) || !_clocks.TryGetValue((device, stage), out var clock) || clock.On == !on)
        {
            return TimeSpan.Zero;
        }

        var lockout = on ? spec.MinOn : spec.MinOff;
        var elapsed = now - clock.Since;
        return elapsed <= TimeSpan.Zero ? lockout : elapsed >= lockout ? TimeSpan.Zero : lockout - elapsed;
    }

    private sealed record Clock(bool? On, DateTimeOffset Since);
}
