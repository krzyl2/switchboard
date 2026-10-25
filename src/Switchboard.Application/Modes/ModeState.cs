using System.Collections.Immutable;
using Switchboard.Application.Signals;

namespace Switchboard.Application.Modes;

/// <summary>Why the effective mode differs from what was asked for.</summary>
public enum ModeNote
{
    None,

    /// <summary>
    /// Written by 0.3.x when the option allowed active mode. Kept so decision lines of that version
    /// (30 days of retention) still read; never written any more.
    /// </summary>
    ActiveUnavailableBeforeStage3,

    /// <summary>
    /// Written by 0.4.x … 0.6.x when the option allowed active mode while no command path existed.
    /// Kept so their decision lines still read; never written any more.
    /// </summary>
    ActiveNotEnabled,
}

/// <summary>A condition that keeps the effective mode in shadow (MODE-02); the UI names each one.</summary>
public enum ModeCondition
{
    /// <summary>The add-on option (the ceiling) is shadow.</summary>
    OptionShadow,

    /// <summary>The global "control allowed" switch is off.</summary>
    ControlOff,

    /// <summary>There is no valid configuration.</summary>
    ConfigurationInvalid,

    /// <summary>state.json was corrupt or unreadable at start and the user has not confirmed yet.</summary>
    StateUnconfirmed,
}

/// <summary>
/// The mode rule (DECISIONS, maintainer survey 2026-10-25): commands need the add-on option active
/// (the ceiling), the global control switch on, a valid configuration and a confirmed state;
/// anything missing keeps the effective mode in shadow and is named in <see cref="Missing"/>.
/// </summary>
/// <param name="Ceiling">The add-on option (or SWITCHBOARD_MODE).</param>
/// <param name="ControlAllowed">The global "control allowed" switch, persisted in state.json.</param>
/// <param name="Effective">What the effector gate runs in.</param>
/// <param name="Missing">Every condition that keeps the effective mode in shadow, in the order of <see cref="ModeCondition"/>.</param>
public sealed record ModeStatus(
    OperatingMode Ceiling,
    bool ControlAllowed,
    bool ConfigurationValid,
    bool StateConfirmed,
    OperatingMode Effective,
    ImmutableArray<ModeCondition> Missing,
    ModeNote Note)
{
    /// <summary>The control switch in the vocabulary of the Stage 5 UI choice: active while control is allowed.</summary>
    public OperatingMode UiChoice => ControlAllowed ? OperatingMode.Active : OperatingMode.Shadow;

    /// <summary>
    /// The only thing missing is a valid configuration: the gate may still send the safe state's
    /// offs and minimums to the devices of the last valid document (04 §8), never an increase.
    /// </summary>
    public bool DecreasesOnly => Effective == OperatingMode.Shadow && Missing.Length == 1 && Missing[0] == ModeCondition.ConfigurationInvalid;

    /// <summary>Commands may leave at all (every one in active mode, decreases only during an invalid configuration).</summary>
    public bool Commands => Effective == OperatingMode.Active || DecreasesOnly;

    public bool Equals(ModeStatus? other) =>
        other is not null
        && Ceiling == other.Ceiling
        && ControlAllowed == other.ControlAllowed
        && ConfigurationValid == other.ConfigurationValid
        && StateConfirmed == other.StateConfirmed
        && Effective == other.Effective
        && Note == other.Note
        && Missing.SequenceEqual(other.Missing);

    public override int GetHashCode() => HashCode.Combine(Ceiling, ControlAllowed, ConfigurationValid, StateConfirmed, Effective, Note);
}

public interface IModeSource
{
    /// <summary>The mode now, with the configuration validity of the latest published inputs.</summary>
    ModeStatus Current { get; }

    /// <summary>The mode for a given configuration validity: what the guard runner resolves from the inputs of its own step.</summary>
    ModeStatus For(bool configurationValid);
}

/// <summary>
/// Holds the mode for the process: the add-on option (fixed for the process) and the control switch
/// (changed at run time, MODE-02). Resolved fresh on every read, so a change of the switch is in
/// force at once; the switch wakes the guard itself, which records the change as a mode decision.
/// </summary>
public sealed class ModeState : IModeSource
{
    private readonly IControlSwitchView _control;
    private readonly IControlInputSource? _inputs;

    /// <param name="inputs">The control inputs whose configuration validity <see cref="Current"/> reads; null treats the configuration as valid (tests of the rule alone).</param>
    public ModeState(OperatingMode ceiling, IControlSwitchView control, IControlInputSource? inputs = null)
    {
        ArgumentNullException.ThrowIfNull(control);
        Ceiling = ceiling;
        _control = control;
        _inputs = inputs;
    }

    public OperatingMode Ceiling { get; }

    public ModeStatus Current => For(_inputs is null || _inputs.Current.Protection.Setup is not null);

    public ModeStatus For(bool configurationValid)
    {
        var control = _control.Status;
        return Resolve(Ceiling, control.Allowed, configurationValid, !control.Locked);
    }

    /// <summary>Active only when nothing is missing: option active, control allowed, configuration valid, state confirmed.</summary>
    public static ModeStatus Resolve(OperatingMode ceiling, bool controlAllowed, bool configurationValid, bool stateConfirmed)
    {
        var missing = ImmutableArray.CreateBuilder<ModeCondition>();
        if (ceiling != OperatingMode.Active)
        {
            missing.Add(ModeCondition.OptionShadow);
        }

        if (!controlAllowed)
        {
            missing.Add(ModeCondition.ControlOff);
        }

        if (!configurationValid)
        {
            missing.Add(ModeCondition.ConfigurationInvalid);
        }

        if (!stateConfirmed)
        {
            missing.Add(ModeCondition.StateUnconfirmed);
        }

        return new ModeStatus(
            ceiling,
            controlAllowed,
            configurationValid,
            stateConfirmed,
            missing.Count == 0 ? OperatingMode.Active : OperatingMode.Shadow,
            missing.ToImmutable(),
            ModeNote.None);
    }
}
