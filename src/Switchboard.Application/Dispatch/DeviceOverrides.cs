using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Guard;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;

namespace Switchboard.Application.Dispatch;

/// <summary>A manual override of one device (SAFE-04).</summary>
public enum OverrideMode
{
    /// <summary>No override: dispatch decides.</summary>
    Auto,

    /// <summary>Relays on, a setpoint at its maximum: outranks dispatch, never protection.</summary>
    ForceOn,

    /// <summary>Relays off, a setpoint at its floor: outranks dispatch.</summary>
    ForceOff,
}

public enum OverrideSource
{
    Api,
    Mqtt,

    /// <summary>Restored from state.json at start.</summary>
    Restored,
}

/// <summary>One device's override and who set it when.</summary>
public sealed record DeviceOverride(OverrideMode Mode, DateTimeOffset? ChangedAt, OverrideSource? Source)
{
    public static DeviceOverride Auto { get; } = new(OverrideMode.Auto, null, null);
}

/// <summary>Every override at one moment; <see cref="Version"/> grows with each change, so a change is seen once.</summary>
public sealed record OverrideSnapshot(long Version, ImmutableDictionary<DeviceName, DeviceOverride> Devices)
{
    public static OverrideSnapshot None { get; } = new(0, ImmutableDictionary<DeviceName, DeviceOverride>.Empty);

    public DeviceOverride For(DeviceName device) => Devices.GetValueOrDefault(device) ?? DeviceOverride.Auto;
}

public enum OverrideResult
{
    Applied,

    /// <summary>The device already had this override.</summary>
    Unchanged,

    /// <summary>No declared device has this id (SAFE-05: only declared devices are ever addressed).</summary>
    UnknownDevice,

    /// <summary>There is no valid configuration to check the device against.</summary>
    NoConfiguration,
}

public interface IDeviceOverrideView
{
    OverrideSnapshot Current { get; }
}

/// <summary>Sets a device's override from the API or Home Assistant (MQTT select).</summary>
public interface IDeviceOverrideControl : IDeviceOverrideView
{
    OverrideResult Set(DeviceName device, OverrideMode mode, OverrideSource source);
}

/// <summary>
/// The manual overrides (SAFE-04). Setting one changes memory and wakes the guard runner, whose
/// next step records it and lets dispatch act on it; only then are listeners (state.json
/// persistence) told. An override for a device that is not declared in the current valid
/// configuration is refused. Overrides survive a restart (restored before the first step);
/// protection always outranks them.
/// </summary>
public sealed class DeviceOverrides : IDeviceOverrideControl
{
    private readonly GuardInbox _inbox;
    private readonly IControlInputSource _inputs;
    private readonly TimeProvider _time;
    private readonly ILogger<DeviceOverrides> _logger;
    private readonly object _gate = new();
    private OverrideSnapshot _current = OverrideSnapshot.None;

    public DeviceOverrides(GuardInbox inbox, IControlInputSource inputs, SignalClock clock, ILogger<DeviceOverrides> logger)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _inbox = inbox;
        _inputs = inputs;
        _time = clock.Time;
        _logger = logger;
    }

    /// <summary>Raised after the runner has been woken; for persistence. Handlers must not block.</summary>
    public event EventHandler<OverrideSnapshot>? Changed;

    public OverrideSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>The overrides persisted in state.json, applied at start before the first step.</summary>
    public void Restore(ImmutableDictionary<DeviceName, DeviceOverride> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        lock (_gate)
        {
            _current = new OverrideSnapshot(
                _current.Version + 1,
                overrides.Where(o => o.Value.Mode != OverrideMode.Auto).ToImmutableDictionary(o => o.Key, o => o.Value with { Source = OverrideSource.Restored }));
        }

        _inbox.Post(GuardTrigger.Override);
    }

    public OverrideResult Set(DeviceName device, OverrideMode mode, OverrideSource source)
    {
        if (_inputs.Current.Protection.Setup is not { } setup)
        {
            return OverrideResult.NoConfiguration;
        }

        if (setup.Device(device) is null)
        {
            return OverrideResult.UnknownDevice;
        }

        OverrideSnapshot snapshot;
        lock (_gate)
        {
            if (_current.For(device).Mode == mode)
            {
                return OverrideResult.Unchanged;
            }

            var devices = mode == OverrideMode.Auto
                ? _current.Devices.Remove(device)
                : _current.Devices.SetItem(device, new DeviceOverride(mode, _time.GetUtcNow(), source));
            snapshot = new OverrideSnapshot(_current.Version + 1, devices);
            _current = snapshot;
        }

        _inbox.Post(GuardTrigger.Override);
        _logger.LogWarning("Manual override of {Device} set to {Mode} from {Source}", device, mode, source);
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler<OverrideSnapshot>)handler)(this, snapshot);
            }
            catch (Exception ex)
            {
                // Persistence is best effort; the override already holds in memory.
                _logger.LogError(ex, "An override listener failed; the override of {Device} stays {Mode}", device, mode);
            }
        }

        return OverrideResult.Applied;
    }
}
