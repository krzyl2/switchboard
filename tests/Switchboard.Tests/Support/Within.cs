namespace Switchboard.Tests.Support;

/// <summary>
/// Hang guard for awaits on deterministic test signals. A passing test never waits on this clock:
/// it only turns a broken expectation (a signal that never comes) into a failure instead of a hung run.
/// Control logic itself always runs on the virtual clock.
/// </summary>
internal static class Within
{
    private static readonly TimeSpan HangLimit = TimeSpan.FromSeconds(10);

    public static Task<T> Soon<T>(this Task<T> task) => task.WaitAsync(HangLimit, TimeProvider.System);

    public static Task Soon(this Task task) => task.WaitAsync(HangLimit, TimeProvider.System);

    public static Task<T> Soon<T>(this ValueTask<T> task) => task.AsTask().Soon();
}
