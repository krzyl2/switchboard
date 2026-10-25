namespace Switchboard.Application.Effector;

/// <summary>
/// The command side of the effector gate (03, DISP-01): logical device names and levels only, never
/// an entity ID. Only the effector gate holds it, and it submits only after protection resolved the
/// step, only while commands may leave (option active, control allowed; during an invalid
/// configuration decreases only), and only for a device of the last valid configuration (SAFE-05).
/// The one production implementation sends Home Assistant service calls through the outbound
/// allow-list and checks the entity against the configuration loaded at send time once more.
/// </summary>
public interface IDeviceCommandPort
{
    /// <summary>Queues one command; called on the guard thread, never blocks and never throws for transport trouble (that comes back as a result).</summary>
    CommandTicket Submit(DeviceCommand command);

    /// <summary>The end of a step's submissions: what was queued may leave, protective commands first.</summary>
    void Flush();

    /// <summary>The next outcome of a submitted command, if any; drained by the gate at the start of every step.</summary>
    bool TryTakeResult(out CommandResult result);

    /// <summary>Grows by one each time a new link to Home Assistant has delivered its initial states: the gate then re-asserts protective targets once.</summary>
    long LinkGeneration { get; }
}

/// <summary>What the API and the UI read of the command path; separate from the port, which only the gate holds.</summary>
public interface ICommandPathStatus
{
    CommandPathStatus Status { get; }
}
