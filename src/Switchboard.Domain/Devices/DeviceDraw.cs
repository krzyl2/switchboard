using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Domain.Devices;

/// <summary>
/// A device's own measured current on one phase (its declared draw signal), as protection sees it.
/// <see cref="Current"/> is null unless the signal is <see cref="Fresh"/>: a stale draw is never relief.
/// Consumption is positive; a storage device's draw is signed (positive = charging).
/// </summary>
public sealed record DeviceDrawSample(DeviceName Device, Phase Phase, bool Fresh, Amperes? Current, DateTimeOffset? ReportedAt);

/// <summary>
/// A device's own measured power on one phase, as dispatch sees it (verification, foreign load,
/// SIG-09 device terms). <see cref="Power"/> is null unless the signal is <see cref="Fresh"/>.
/// </summary>
public sealed record DevicePowerSample(DeviceName Device, Phase Phase, bool Fresh, Watts? Power, DateTimeOffset? ReportedAt);
