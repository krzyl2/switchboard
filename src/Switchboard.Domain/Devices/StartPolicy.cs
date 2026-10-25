namespace Switchboard.Domain.Devices;

/// <summary>
/// What dispatch may do with a device beyond protection (the device key <c>start:</c>, maintainer
/// survey 2026-10-25). The first member is 1, so <c>default</c> is not a policy.
/// </summary>
public enum StartPolicy
{
    /// <summary>
    /// Dispatch never starts or raises the device beyond the level it had before Switchboard
    /// intervened: it only restores what protection shed or reduced (the default).
    /// </summary>
    RestoreOnly = 1,

    /// <summary>Dispatch may start and raise the device within headroom and rank (the behaviour before 0.7.0).</summary>
    Headroom,
}
