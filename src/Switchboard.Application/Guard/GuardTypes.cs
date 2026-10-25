using System.Collections.Immutable;
using Switchboard.Application.Decisions;
using Switchboard.Application.Modes;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
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

    /// <summary>A manual override changed (SAFE-04).</summary>
    Override,

    /// <summary>The control switch changed or was confirmed (MODE-02): the step records the mode; the guard itself does not react to it.</summary>
    Mode,

    /// <summary>A command's outcome arrived from the command path: the gate records the decision it belongs to.</summary>
    Command,

    /// <summary>The active phase self-test moved on (CONN-05): the gate applies what it wants of its device.</summary>
    SelfTest,

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
    DecisionInputs Inputs)
{
    /// <summary>The circuit group whose condition produced it (CONN-03); null for the main breaker.</summary>
    public GroupName? Group { get; init; }
}

/// <summary>A phase, group or system state change worth a decision record (safe state, kill switch, reserve request).</summary>
public sealed record GuardEvent(DecisionScope Scope, Phase? Phase, DecisionAction Action, ReasonCode Reason, DecisionInputs Inputs)
{
    public GroupName? Group { get; init; }
}

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
    long CycleIndex)
{
    /// <summary>What dispatch may use: E per main and group phase, and the load model per device.</summary>
    public DispatchEnvelope Envelope { get; init; } = DispatchEnvelope.Empty;

    /// <summary>The last valid setup of this process (the declared devices), also during an invalid configuration.</summary>
    public ProtectionSetup? Setup { get; init; }

    /// <summary>The configuration of this evaluation is valid (not only remembered).</summary>
    public bool ConfigurationValid { get; init; }

    /// <summary>A cycle boundary was crossed at this evaluation.</summary>
    public bool AtBoundary { get; init; }

    /// <summary>What protection holds each device stage to after this evaluation.</summary>
    public ImmutableDictionary<DeviceName, ImmutableDictionary<int, StageLimit>> Limits { get; init; } =
        ImmutableDictionary<DeviceName, ImmutableDictionary<int, StageLimit>>.Empty;
}
