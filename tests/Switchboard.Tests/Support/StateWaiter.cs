using Switchboard.Application.Connectivity;

namespace Switchboard.Tests.Support;

internal static class StateWaiter
{
    /// <summary>Completes when the state is (or becomes) <paramref name="expected"/>.</summary>
    public static Task WaitForAsync(this IConnectionState state, ConnectionState expected)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, ConnectionState next)
        {
            if (next == expected)
            {
                reached.TrySetResult();
            }
        }

        state.Changed += OnChanged;
        if (state.Current == expected)
        {
            reached.TrySetResult();
        }

        return reached.Task.ContinueWith(
            _ => state.Changed -= OnChanged,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default).Soon();
    }
}
