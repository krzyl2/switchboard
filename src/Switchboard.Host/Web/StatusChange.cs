using Switchboard.Application.Dispatch;
using Switchboard.Application.Guard;

namespace Switchboard.Host.Web;

/// <summary>A status holder's publication, seen from the API: how to listen, and what it holds now.</summary>
internal readonly record struct StatusFeed<TStatus>(Action<EventHandler<TStatus>> Subscribe, Action<EventHandler<TStatus>> Unsubscribe, Func<TStatus> Current);

/// <param name="Result">What the change answered.</param>
/// <param name="Status">The published status that showed the change; null when the change was not waited for or the wait ran out.</param>
/// <param name="Late">The wait ran out: the change is in force in memory, only its evaluation is late.</param>
internal readonly record struct AppliedChange<TResult, TStatus>(TResult Result, TStatus? Status, bool Late);

internal static class StatusChange
{
    public static StatusFeed<GuardStatus> Feed(GuardStatusHolder holder) => new(h => holder.Published += h, h => holder.Published -= h, () => holder.Current);

    public static StatusFeed<DispatchStatus> Feed(DispatchStatusHolder holder) => new(h => holder.Published += h, h => holder.Published -= h, () => holder.Current);

    /// <summary>
    /// Applies a change and waits until the guard evaluated it, that is, until a published status
    /// <paramref name="shows"/> it. The subscription is made before the change, so its evaluation cannot be missed.
    /// A change <paramref name="waitFor"/> says nothing was changed by (refused, unknown) is answered at once.
    /// </summary>
    public static async Task<AppliedChange<TResult, TStatus>> ApplyAsync<TResult, TStatus>(
        StatusFeed<TStatus> feed,
        Func<TStatus, bool> shows,
        Func<TResult> change,
        Func<TResult, bool> waitFor,
        TimeSpan timeout,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var evaluated = new TaskCompletionSource<TStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPublished(object? sender, TStatus published)
        {
            if (shows(published))
            {
                evaluated.TrySetResult(published);
            }
        }

        feed.Subscribe(OnPublished);
        try
        {
            var result = change();
            if (!waitFor(result))
            {
                return new AppliedChange<TResult, TStatus>(result, default, false);
            }

            var current = feed.Current();
            if (shows(current))
            {
                evaluated.TrySetResult(current);
            }

            try
            {
                return new AppliedChange<TResult, TStatus>(result, await evaluated.Task.WaitAsync(timeout, time, cancellationToken).ConfigureAwait(false), false);
            }
            catch (TimeoutException)
            {
                return new AppliedChange<TResult, TStatus>(result, default, true);
            }
        }
        finally
        {
            feed.Unsubscribe(OnPublished);
        }
    }
}
