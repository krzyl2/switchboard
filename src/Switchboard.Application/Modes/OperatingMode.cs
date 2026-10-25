namespace Switchboard.Application.Modes;

public enum OperatingMode
{
    /// <summary>Computes and logs, never commands. The default everywhere (MODE-01, D7).</summary>
    Shadow,

    /// <summary>Commands declared actuators through the effector gate (option active, control allowed, valid configuration).</summary>
    Active,
}
