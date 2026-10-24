using System.Collections.Immutable;
using Switchboard.Application.Decisions;
using Switchboard.Application.Modes;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Guard;

/// <summary>What woke the guard up; every trigger evaluates the latest protection inputs.</summary>
public enum GuardTrigger
{
    /// <summary>The signal store published a new cycle.</summary>
    Snapshot,

    /// <summary>A guard deadline came: a cycle boundary, a band timer, a budget threshold.</summary>
    Tick,

    KillSwitch,

    /// <summary>The add-on is stopping: every controlled device goes to safe state (SAFE-01).</summary>
    Shutdown,
}

/// <summary>One guard evaluation's input. <see cref="Protection"/> is null only before the first store cycle.</summary>
public sealed record GuardInput(GuardTrigger Trigger, ProtectionInputs? Protection, KillSwitchStatus KillSwitch, ModeStatus Mode);

/// <summary>Where protection holds a device stage.</summary>
public enum GuardTarget
{
    Off,

    /// <summary>A modulating device held at its minimum setpoint.</summary>
    AtMinimum,

    /// <summary>Protection lets go of a stage it held. It never starts anything.</summary>
    Released,
}

/// <summary>Protective intents (every guard intent) outrank lockouts and change limits; ordinary ones (dispatch, from Stage 3) do not.</summary>
public enum IntentUrgency
{
    Protective,
    Ordinary,
}

/// <summary>A change of what protection holds a device stage to, with its reason.</summary>
/// <param name="Stage">The stage number of a stepped device; null for a device with one stage.</param>
/// <param name="Phase">The phase whose condition produced it; null for kill switch and system-wide safe state.</param>
/// <param name="Overridden">A rule this intent outranks (lockout_min_on).</param>
public sealed record GuardIntent(
    DeviceName Device,
    int? Stage,
    GuardTarget Target,
    ReasonCode Reason,
    Phase? Phase,
    IntentUrgency Urgency,
    ReasonCode? Overridden,
    DecisionInputs Inputs);

/// <summary>A phase or system state change worth a decision record (safe state, kill switch, reserve request).</summary>
public sealed record GuardEvent(DecisionScope Scope, Phase? Phase, DecisionAction Action, ReasonCode Reason, DecisionInputs Inputs);

/// <summary>
/// The result of one evaluation. <see cref="Timing"/> and <see cref="CycleIndex"/> travel with it
/// so the effector gate applies the same cycle the guard saw.
/// </summary>
public sealed record GuardEvaluation(
    DateTimeOffset At,
    GuardStatus Status,
    ImmutableArray<GuardIntent> Intents,
    ImmutableArray<GuardEvent> Events,
    GuardTiming? Timing,
    long CycleIndex);
