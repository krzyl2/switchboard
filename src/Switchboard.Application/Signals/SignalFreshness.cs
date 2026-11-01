using Switchboard.Domain.Signals;

namespace Switchboard.Application.Signals;

/// <summary>
/// What a held sample is worth at a given moment. Freshness is read from the report time Home
/// Assistant gave (<c>last_reported</c>, clamped to arrival and advanced only by a newer report),
/// never from <c>last_changed</c>; everything is a function of the runtime and <c>now</c>.
/// </summary>
internal static class SignalFreshness
{
    /// <summary>
    /// State precedence: Unavailable, then Stale, then Rejected (an unusable sample), then Fresh
    /// (SIG-05). A sample refused by the balance check leaves the held value Fresh until it ages out
    /// (SIG-09), with the refusal in the detail.
    /// </summary>
    public static (SignalState State, SignalDetail Detail) Classify(SignalRuntime signal, DateTimeOffset now)
    {
        switch (signal.Presence)
        {
            case Presence.Awaiting:
                return (SignalState.Unavailable, SignalDetail.AwaitingFirstSample);
            case Presence.Missing:
                return (SignalState.Unavailable, SignalDetail.EntityMissing);
            case Presence.Removed:
                return (SignalState.Unavailable, SignalDetail.EntityRemoved);
        }

        if (signal.RawStatus == RawStatus.Unavailable)
        {
            return (SignalState.Unavailable, signal.RawDetail);
        }

        if (signal.Accepted is not { } accepted)
        {
            return (SignalState.Rejected, signal.RawDetail);
        }

        // Aged from the last accepted report: a signal whose every new sample is rejected goes
        // stale like a frozen one instead of showing an old value forever. A device state declared
        // on_change has no age limit: it reports only when it changes, so its freshness is its
        // availability (Unavailable and Missing are handled above). A disconnected source is NOT handled here: Classify does not look at it. The device-state
        // interpreter and the EV module's own inputs view each treat a disconnected source as "nothing known", so an on_change state never stays Fresh forever.
        if (signal.Definition.Freshness == StateFreshness.Age && now - accepted.Effective >= signal.Definition.MaxAge)
        {
            return (SignalState.Stale, SignalDetail.MaxAgeExceeded);
        }

        // SIG-09 rejects the sample, never the decision: protection, dispatch and direction keep
        // using the last accepted value. The same-state path never advances it while the newest
        // state is the refused one, so it still goes stale at max_age.
        return signal.RawStatus switch
        {
            RawStatus.Invalid => (SignalState.Rejected, signal.RawDetail),
            RawStatus.BalanceRejected => (SignalState.Fresh, SignalDetail.BalanceResidual),
            _ => (SignalState.Fresh, SignalDetail.None),
        };
    }

    public static bool IsFresh(SignalRuntime signal, DateTimeOffset now) => Classify(signal, now).State == SignalState.Fresh;

    /// <summary>
    /// A draw is trusted while fresh, or while it is silent after reporting exactly 0: a device that
    /// reports only on change (the go-e) says 0 once at rest and then nothing. An idle device relieves
    /// nothing, so trusting its 0 can never hide load; a draw frozen at a load stays untrusted.
    /// </summary>
    public static bool DrawTrusted(SignalSourceStatus source, SignalRuntime signal, DateTimeOffset now)
    {
        if (source != SignalSourceStatus.Connected)
        {
            return false;
        }

        var state = Classify(signal, now).State;
        return state == SignalState.Fresh || (state == SignalState.Stale && signal.Accepted is not null && signal.Magnitude() == 0);
    }
}
