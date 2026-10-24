using Switchboard.Application.Modes;

namespace Switchboard.Tests.Application.Modes;

/// <summary>
/// MODE-01 and the mode rule of DECISIONS: the add-on option is a ceiling, option shadow forces
/// shadow whatever the UI chose, and this version has no active behaviour at all.
/// </summary>
public sealed class ModeStateTests
{
    [Theory]
    [InlineData(OperatingMode.Shadow)]
    [InlineData(OperatingMode.Active)]
    public void Shadow_option_forces_shadow(OperatingMode uiChoice)
    {
        var mode = ModeState.Resolve(OperatingMode.Shadow, uiChoice);

        Assert.Equal((OperatingMode.Shadow, ModeNote.None, uiChoice), (mode.Effective, mode.Note, mode.UiChoice));
    }

    [Theory]
    [InlineData(OperatingMode.Shadow)]
    [InlineData(OperatingMode.Active)]
    public void Active_option_is_still_shadow_in_stage_2(OperatingMode uiChoice)
    {
        // Going active takes two deliberate acts (option and UI) and a version with a command
        // path; until Stage 3 the note says why it stays shadow.
        var mode = new ModeState(OperatingMode.Active, uiChoice).Current;

        Assert.Equal((OperatingMode.Active, OperatingMode.Shadow, ModeNote.ActiveUnavailableBeforeStage3), (mode.Ceiling, mode.Effective, mode.Note));
    }
}
