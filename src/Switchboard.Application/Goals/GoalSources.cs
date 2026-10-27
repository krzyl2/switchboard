using System.Collections.Immutable;
using Switchboard.Application.Configuration;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Goals;

/// <summary>What a source needs to answer: the clock, the configuration in force, this cycle's signals and the ledger.</summary>
/// <param name="Zone">The local zone, injected: deadlines are local wall-clock times.</param>
public sealed record GoalContext(DateTimeOffset Now, SwitchboardConfiguration Configuration, GoalInputs Inputs, TimeZoneInfo Zone, IGoalProgress Progress);

/// <summary>What a source offers: the goals it can state now, and the goals it has but cannot state (with why).</summary>
public sealed record GoalOffer(ImmutableArray<GoalRequest> Requests, ImmutableArray<InactiveGoal> Inactive)
{
    public static GoalOffer Empty { get; } = new([], []);
}

/// <summary>
/// The seam of 15-PLANNER (Goal sources): anything that wants energy by a deadline states it as <see cref="GoalRequest"/>s. The
/// device's <c>goal:</c> block is one source (<see cref="ConfigGoalSource"/>); the manual store (slice 7) and the EV module (later) are
/// others, merged per device by <see cref="GoalMerge"/>. A source commands nothing and never guesses: an input it cannot read is an
/// <see cref="InactiveGoal"/>, not a default.
/// </summary>
public interface IGoalSource
{
    GoalOffer Goals(GoalContext context);
}

/// <summary>
/// The goals of the configuration: each device's <c>goal:</c> evaluated against the signals of this cycle. A missing, stale,
/// unavailable, unreadable or out-of-range input makes that goal inactive with the reason; it never keeps an old value.
/// </summary>
public sealed class ConfigGoalSource : IGoalSource
{
    public GoalOffer Goals(GoalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var requests = ImmutableArray.CreateBuilder<GoalRequest>();
        var inactive = ImmutableArray.CreateBuilder<InactiveGoal>();
        var readings = context.Inputs.Samples;

        foreach (var device in context.Configuration.Devices.Where(d => d.Goal is not null).OrderBy(d => d.Id))
        {
            var outcome = Evaluate(device.Goal!, context, readings);
            if (outcome.Request is { } request)
            {
                requests.Add(request);
            }
            else
            {
                inactive.Add(outcome.Inactive!);
            }
        }

        return new GoalOffer(requests.ToImmutable(), inactive.ToImmutable());
    }

    private static (GoalRequest? Request, InactiveGoal? Inactive) Evaluate(GoalSpec spec, GoalContext context, ImmutableDictionary<SignalName, GoalInputSample> readings)
    {
        InactiveGoal Inactive(GoalInactiveReason reason, GoalInputKind input) => new(spec.Device, reason, input, spec.Origin, spec.Kind, spec.Hard);

        if (spec.Want is { } wantSignal)
        {
            var (reading, failure) = Fresh(readings, wantSignal);
            if (failure is { } wantFailure)
            {
                return (null, Inactive(wantFailure, GoalInputKind.Want));
            }

            switch ((reading!.Value as StateValue)?.State.Trim().ToLowerInvariant())
            {
                case "on" or "true":
                    break;
                case "off" or "false":
                    return (null, Inactive(GoalInactiveReason.WantOff, GoalInputKind.Want));
                default:
                    return (null, Inactive(GoalInactiveReason.InputInvalid, GoalInputKind.Want));
            }
        }

        double? soc = null;
        if (spec.Kind == GoalKind.Soc)
        {
            var read = Number(spec.Soc!, readings);
            if (read.Failure is { } socFailure)
            {
                return (null, Inactive(socFailure, GoalInputKind.Soc));
            }

            if (read.Value is not (>= 0 and <= 100))
            {
                return (null, Inactive(GoalInactiveReason.InputOutOfRange, GoalInputKind.Soc));
            }

            soc = read.Value;
        }

        var target = Number(spec.Target, readings);
        if (target.Failure is { } targetFailure)
        {
            return (null, Inactive(targetFailure, GoalInputKind.Target));
        }

        var inRange = spec.Kind == GoalKind.Soc ? target.Value is >= 0 and <= 100 : target.Value >= 0;
        if (!inRange)
        {
            return (null, Inactive(GoalInactiveReason.InputOutOfRange, GoalInputKind.Target));
        }

        DateTimeOffset? deadline = null;
        if (spec.Deadline is { } deadlineSpec)
        {
            if (deadlineSpec.At is { } at)
            {
                deadline = GoalDeadlines.NextOccurrence(at, context.Now, context.Zone);
            }
            else
            {
                var (reading, failure) = Fresh(readings, deadlineSpec.Signal!.Value);
                if (failure is { } deadlineFailure)
                {
                    return (null, Inactive(deadlineFailure, GoalInputKind.Deadline));
                }

                deadline = (reading!.Value as StateValue) is { } state ? GoalDeadlines.Parse(state.State, context.Now, context.Zone) : null;
                if (deadline is null)
                {
                    return (null, Inactive(GoalInactiveReason.InputInvalid, GoalInputKind.Deadline));
                }
            }
        }

        var targetValue = target.Value!.Value;
        if (spec.Kind == GoalKind.Soc)
        {
            var remaining = GoalMath.RemainingSocKwh(targetValue, soc!.Value, spec.CapacityKwh!.Value, spec.LossFraction);
            return (new GoalRequest(spec.Device, spec.Kind, remaining, null, targetValue, soc, null, deadline, spec.Deadline?.Margin ?? TimeSpan.Zero, spec.Hard, spec.Sources, spec.Escalate, spec.OnMet, null, spec.Origin, spec.Revision), null);
        }

        var entry = context.Progress.Entry(spec.Device) is { } known && known.Continues(spec.Kind, deadline, context.Now) ? known : null;
        var delivered = entry?.DeliveredKwh ?? 0;
        var left = GoalMath.RemainingEnergyKwh(targetValue, delivered, spec.LossFraction);
        return (new GoalRequest(spec.Device, spec.Kind, left, targetValue, null, null, delivered, deadline, spec.Deadline?.Margin ?? TimeSpan.Zero, spec.Hard, spec.Sources, spec.Escalate, spec.OnMet, entry?.Since ?? context.Now, spec.Origin, spec.Revision), null);
    }

    internal static (double? Value, GoalInactiveReason? Failure) Number(GoalNumber number, ImmutableDictionary<SignalName, GoalInputSample> readings)
    {
        if (number.Literal is { } literal)
        {
            return (literal, null);
        }

        var (reading, failure) = Fresh(readings, number.Signal!.Value);
        if (failure is not null)
        {
            return (null, failure);
        }

        return SignalNormalizer.Magnitude(reading!.Value) is { } value && double.IsFinite(value) ? (value, null) : (null, GoalInactiveReason.InputInvalid);
    }

    /// <summary>The reading of a goal input while it is fresh and has a value; otherwise why not. Never the last known value.</summary>
    private static (GoalInputSample? Reading, GoalInactiveReason? Failure) Fresh(ImmutableDictionary<SignalName, GoalInputSample> readings, SignalName name)
    {
        if (!readings.TryGetValue(name, out var reading))
        {
            return (null, GoalInactiveReason.InputMissing);
        }

        return reading.State switch
        {
            SignalState.Fresh when reading.Value is not null => (reading, null),
            SignalState.Fresh => (null, GoalInactiveReason.InputUnavailable),
            SignalState.Stale => (null, GoalInactiveReason.InputStale),
            SignalState.Rejected => (null, GoalInactiveReason.InputInvalid),
            SignalState.Unavailable when reading.Detail is SignalDetail.StateUnavailable or SignalDetail.StateUnknown => (null, GoalInactiveReason.InputUnavailable),
            _ => (null, GoalInactiveReason.InputMissing),
        };
    }
}
