using Switchboard.Application.Modes;

namespace Switchboard.Tests.Support;

/// <summary>Modes for tests: shadow (the default) and active semantics, and a mode state over a fixed control switch.</summary>
internal static class TestModes
{
    /// <summary>Option shadow, control off, configuration valid: nothing leaves.</summary>
    public static ModeStatus Shadow => ModeState.Resolve(OperatingMode.Shadow, controlAllowed: false, configurationValid: true, stateConfirmed: true);

    /// <summary>A mode state whose control switch is fixed (allowed or not) and whose configuration counts as valid.</summary>
    public static ModeState State(OperatingMode ceiling, bool controlAllowed) => new(ceiling, new FixedControl(controlAllowed));

    /// <summary>A control switch nobody changes.</summary>
    public sealed class FixedControl(bool allowed, bool locked = false) : IControlSwitchView
    {
        public ControlSwitchStatus Status { get; } = new(allowed && !locked, null, ControlSwitchSource.Default, locked, locked ? ControlLockReason.StateFileCorrupt : null);
    }
}
