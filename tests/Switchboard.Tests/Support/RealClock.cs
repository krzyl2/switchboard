namespace Switchboard.Tests.Support;

/// <summary>
/// The one place tests may reach the wall clock: hang guards and real waits on other threads (see <see cref="Within"/>), and
/// production objects whose clock is not under test. Control logic itself runs on a virtual clock.
/// </summary>
internal static class RealClock
{
#pragma warning disable RS0030 // The sole sanctioned TimeProvider.System in tests; everything else goes through RealClock.Provider.
    public static TimeProvider Provider { get; } = TimeProvider.System;
#pragma warning restore RS0030
}
