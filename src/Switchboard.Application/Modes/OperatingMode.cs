namespace Switchboard.Application.Modes;

public enum OperatingMode
{
    /// <summary>Computes and logs, never commands. The default everywhere (MODE-01, D7).</summary>
    Shadow,

    /// <summary>Allowed to command declared actuators. Stage 0 contains no command path at all.</summary>
    Active,
}
