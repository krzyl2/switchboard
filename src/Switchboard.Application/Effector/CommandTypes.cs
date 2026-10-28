using Switchboard.Domain.Devices;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Effector;

/// <summary>
/// Protective commands (protection's holds, the safe state, the kill switch) leave before ordinary
/// ones (dispatch, overrides, the self-test) when several actuators wait; one actuator's commands
/// always leave in the order they were decided.
/// </summary>
public enum CommandPriority
{
    Protective,
    Ordinary,
}

/// <summary>Which actuator of a device a command addresses.</summary>
public enum DeviceCommandKind
{
    /// <summary>One relay stage (onoff, stepped): on or off.</summary>
    Relay,

    /// <summary>The setpoint of a modulating or storage device, in amperes.</summary>
    Setpoint,

    /// <summary>A modulating device's stop actuator: on enables it, off switches it off.</summary>
    Enable,
}

/// <summary>
/// One command of the effector gate, by logical name and level only: the command port resolves the
/// entity through the configuration loaded when it sends (SAFE-05), never from anything here.
/// </summary>
/// <param name="Stage">The relay stage number (from 1); 1 for a setpoint or stop actuator.</param>
/// <param name="On">Relay or stop actuator: on.</param>
/// <param name="Setpoint">A setpoint command's value (A); rounded to the device's step and clamped by the port.</param>
public sealed record DeviceCommand(DeviceName Device, DeviceCommandKind Kind, int Stage, bool On, Amperes? Setpoint, CommandPriority Priority)
{
    /// <summary>The device's actuator revision when the command was decided: a reload that re-pointed the device makes it stale.</summary>
    public long Revision { get; init; }

    /// <summary>The command lowers what the device may draw (an off, a lower setpoint): the only kind sent during an invalid configuration.</summary>
    public bool Decrease { get; init; }

    /// <summary>A final off to a device a reload no longer declares (it was on because Switchboard started it), sent once through its previous declaration.</summary>
    public bool Retiring { get; init; }

    /// <summary>The active phase self-test's own off when control is switched off during the test: allowed through once, a decrease only.</summary>
    public bool Final { get; init; }
}

/// <summary>A submitted command's handle; its result comes back with the same sequence number.</summary>
public readonly record struct CommandTicket(long Sequence);

public enum CommandOutcome
{
    /// <summary>Home Assistant answered the service call with success.</summary>
    Acknowledged,

    /// <summary>Home Assistant answered with an error.</summary>
    Failed,

    /// <summary>Home Assistant refused the value itself (service_validation_error, e.g. a setpoint outside the entity's range): the device never saw it, so it says nothing about the device reacting.</summary>
    Rejected,

    /// <summary>No answer within HomeAssistant:CommandTimeout, or the link closed before the answer: the command may or may not have been carried out.</summary>
    TimedOut,

    /// <summary>The command never left (see <see cref="CommandNotSentReason"/>).</summary>
    NotSent,
}

public enum CommandNotSentReason
{
    /// <summary>Home Assistant is not connected.</summary>
    LinkDown,

    /// <summary>The entity is not a declared actuator of the configuration loaded at send time (or the service is not allowed for it).</summary>
    NotDeclared,

    /// <summary>A newer command for the same actuator replaced it before it was sent, or the device was re-pointed at another entity.</summary>
    Superseded,

    /// <summary>Control was switched off (or locked) before it was sent.</summary>
    ControlOff,

    /// <summary>The entity is one of Switchboard's own MQTT entities: commanding it would loop back into Switchboard, so it is refused at send time whatever the configuration declares.</summary>
    OwnEntity,
}

/// <param name="At">When the outcome was known (host clock of the command path).</param>
/// <param name="Detail">Home Assistant's error message, or why it was not sent.</param>
public sealed record CommandResult(CommandTicket Ticket, DeviceName Device, CommandOutcome Outcome, CommandNotSentReason? NotSent, DateTimeOffset At, string? Detail);

/// <param name="LinkReady">Commands can be sent now (Home Assistant connected, or the development sink).</param>
/// <param name="Pending">Commands queued or awaiting their answer.</param>
/// <param name="LastFailure">The last command that failed, timed out or could not be sent; null when none has.</param>
public sealed record CommandPathStatus(bool LinkReady, int Pending, DateTimeOffset? LastAcknowledgedAt, CommandResult? LastFailure)
{
    public static CommandPathStatus Idle { get; } = new(false, 0, null, null);
}
