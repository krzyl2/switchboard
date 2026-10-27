using System.Collections.Immutable;
using Switchboard.Application.Configuration;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Signals;

/// <summary>One goal input signal as the goal model may read it: its state and, only while it is usable, its normalized value.</summary>
public sealed record GoalInputSample(SignalState State, SignalDetail Detail, SignalValue? Value);

/// <summary>
/// The signals of the device goals (roles device_soc, goal_target, goal_deadline and goal_want) of one store cycle, by logical name:
/// all the goal model sees of the signal layer. The full snapshot stays with the signal layer and diagnostics (SIG-04 by structure);
/// no entity is known here.
/// </summary>
public sealed record GoalInputs(ImmutableDictionary<SignalName, GoalInputSample> Samples)
{
    public static GoalInputs Empty { get; } = new(ImmutableDictionary<SignalName, GoalInputSample>.Empty);

    private static readonly SignalRole[] Roles = [SignalRole.DeviceSoc, SignalRole.GoalTarget, SignalRole.GoalDeadline, SignalRole.GoalWant];

    /// <summary>Takes the goal signals out of a snapshot; a value is carried whatever the state, and the reader judges the state first.</summary>
    public static GoalInputs From(SignalSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var samples = ImmutableDictionary.CreateBuilder<SignalName, GoalInputSample>();
        foreach (var reading in snapshot.Signals.Where(s => s.Name is not null && Roles.Contains(s.Role)))
        {
            samples[reading.Name!.Value] = new GoalInputSample(reading.State, reading.Detail, reading.Value);
        }

        return new GoalInputs(samples.ToImmutable());
    }
}

/// <summary>The configuration in force and the goal signals of the latest store cycle; the configuration is null while none is valid.</summary>
public sealed record GoalInputView(ConfigurationResult? Configuration, GoalInputs Inputs);

/// <summary>Where the goal model reads its inputs from; the diagnostics-backed implementation is the only one.</summary>
public interface IGoalInputSource
{
    GoalInputView Current { get; }
}

/// <summary>The latest published store cycle, reduced to what the goal model may see.</summary>
public sealed class DiagnosticsGoalInputSource(ISignalDiagnosticsSource diagnostics) : IGoalInputSource
{
    public GoalInputView Current
    {
        get
        {
            var current = diagnostics.Current;
            return new GoalInputView(current.Configuration, GoalInputs.From(current.Snapshot));
        }
    }
}
