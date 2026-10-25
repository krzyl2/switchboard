using Switchboard.Application.Guard;

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
    /// The add-on option allows active mode, but commanding is not enabled in this version: dispatch
    /// decisions are computed and logged, nothing is switched, and the effective mode stays shadow.
    /// </summary>
    ActiveNotEnabled,
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
/// Holds the mode for the process. In this version the effective mode is always shadow: no
/// production command path exists (the maintainer's rule: nothing may command a Home Assistant
/// entity outside a test double), so an active ceiling only produces the note that commanding is
/// not enabled. The UI choice can change at run time (MODE-02): the change wakes the guard, which
/// records it as a mode decision in its next evaluation, and listeners persist it in state.json.
/// </summary>
public sealed class ModeState : IModeSource
{
    private readonly object _gate = new();
    private readonly GuardInbox? _inbox;
    private ModeStatus _current;

    /// <param name="inbox">The guard's inbox, woken on a change of the UI choice; null where nothing evaluates (tests of the rule alone).</param>
    public ModeState(OperatingMode ceiling, OperatingMode uiChoice, GuardInbox? inbox = null)
    {
        _current = Resolve(ceiling, uiChoice);
        _inbox = inbox;
    }

    /// <summary>Raised after the guard has been woken; for persistence. Handlers must not block.</summary>
    public event EventHandler<ModeStatus>? Changed;

    public ModeStatus Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public static ModeStatus Resolve(OperatingMode ceiling, OperatingMode uiChoice) => Resolve(ceiling, uiChoice, commandPathEnabled: false);

    /// <summary>
    /// Active only with both deliberate acts (option and UI choice) and a command path; production
    /// always passes false. Tests pass true to drive active semantics through a test double.
    /// </summary>
    internal static ModeStatus Resolve(OperatingMode ceiling, OperatingMode uiChoice, bool commandPathEnabled)
    {
        var active = commandPathEnabled && ceiling == OperatingMode.Active && uiChoice == OperatingMode.Active;
        return new(
            ceiling,
            uiChoice,
            active ? OperatingMode.Active : OperatingMode.Shadow,
            ceiling == OperatingMode.Active && !commandPathEnabled ? ModeNote.ActiveNotEnabled : ModeNote.None);
    }

    /// <summary>
    /// The UI choice (MODE-02). While commanding is not enabled only shadow can be chosen: active is
    /// refused (null) and nothing changes, so no version that later enables a command path can start
    /// active on an upgrade without a new deliberate act.
    /// </summary>
    public ModeStatus? Choose(OperatingMode uiChoice) => uiChoice == OperatingMode.Active ? null : Apply(uiChoice);

    /// <summary>The mechanism without the refusal of active; for tests of persistence and evaluation only.</summary>
    internal ModeStatus ChooseUnchecked(OperatingMode uiChoice) => Apply(uiChoice);

    private ModeStatus Apply(OperatingMode uiChoice)
    {
        ModeStatus next;
        lock (_gate)
        {
            next = Resolve(_current.Ceiling, uiChoice);
            if (next == _current)
            {
                return next;
            }

            _current = next;
        }

        _inbox?.Post(GuardTrigger.Mode);
        Changed?.Invoke(this, next);
        return next;
    }
}
