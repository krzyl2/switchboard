namespace Switchboard.Application.Modes;

/// <summary>Why the effective mode differs from what was asked for.</summary>
public enum ModeNote
{
    None,

    /// <summary>The add-on option allows active mode, but this version has no command path: it stays shadow.</summary>
    ActiveUnavailableBeforeStage3,
}

/// <summary>
/// The mode rule (DECISIONS, MODE-01): the add-on option is a ceiling, and option shadow forces
/// shadow. The UI choice persisted in state.json matters only under an active ceiling.
/// </summary>
/// <param name="Ceiling">The add-on option (or SWITCHBOARD_MODE).</param>
/// <param name="UiChoice">The choice persisted in state.json.</param>
/// <param name="Effective">What the effector gate runs in.</param>
public sealed record ModeStatus(OperatingMode Ceiling, OperatingMode UiChoice, OperatingMode Effective, ModeNote Note);

public interface IModeSource
{
    ModeStatus Current { get; }
}

/// <summary>
/// Holds the mode for the process. In this version the effective mode is always shadow: there is
/// no command path at all, so even an active ceiling with an active UI choice only produces the
/// note that active mode is not available yet.
/// </summary>
public sealed class ModeState : IModeSource
{
    public ModeState(OperatingMode ceiling, OperatingMode uiChoice)
    {
        Current = Resolve(ceiling, uiChoice);
    }

    public ModeStatus Current { get; }

    public static ModeStatus Resolve(OperatingMode ceiling, OperatingMode uiChoice) =>
        new(
            ceiling,
            uiChoice,
            OperatingMode.Shadow,
            ceiling == OperatingMode.Active ? ModeNote.ActiveUnavailableBeforeStage3 : ModeNote.None);
}
