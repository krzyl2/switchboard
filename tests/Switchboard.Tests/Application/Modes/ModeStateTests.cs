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
    public void Active_option_stays_shadow_with_active_not_enabled(OperatingMode uiChoice)
    {
        // Going active takes two deliberate acts (option and UI) and a command path; this version
        // has none in production (the maintainer's rule), so the note says why it stays shadow.
        var mode = new ModeState(OperatingMode.Active, uiChoice).Current;

        Assert.Equal((OperatingMode.Active, OperatingMode.Shadow, ModeNote.ActiveNotEnabled), (mode.Ceiling, mode.Effective, mode.Note));
    }

    [Fact]
    public void Active_needs_option_ui_choice_and_a_command_path()
    {
        // Only tests can claim a command path; even then both deliberate acts are required.
        Assert.Equal(OperatingMode.Active, ModeState.Resolve(OperatingMode.Active, OperatingMode.Active, commandPathEnabled: true).Effective);
        Assert.Equal(OperatingMode.Shadow, ModeState.Resolve(OperatingMode.Active, OperatingMode.Shadow, commandPathEnabled: true).Effective);
        Assert.Equal(OperatingMode.Shadow, ModeState.Resolve(OperatingMode.Shadow, OperatingMode.Active, commandPathEnabled: true).Effective);
        Assert.Equal(OperatingMode.Shadow, ModeState.Resolve(OperatingMode.Active, OperatingMode.Active).Effective);
    }
}
