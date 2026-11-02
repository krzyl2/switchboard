using Switchboard.Application.Setup;
using Switchboard.Application.Setup.SelfTest;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Setup;

/// <summary>
/// The watch a self-test start describes is built from the draft's meter rows. A refusal names the field the wizard must fix, because the wizard
/// shows the message at that field: a wrong field sends the user to the wrong input, a missing refusal watches something that cannot be judged.
/// </summary>
public sealed class SelfTestWatchTests
{
    private static (SelfTestRequest? Watch, System.Collections.Immutable.ImmutableArray<SetupIssue> Issues) Of(SetupDraft draft, SelfTestSubject subject) =>
        SelfTestWatch.Of(draft, subject, SetupFixtures.Control, SetupFixtures.Defaults.SelfTest.Classifier);

    [Fact]
    public void A_draft_without_a_meter_cannot_be_watched()
    {
        var draft = SetupFixtures.MaintainerDraft();
        var (watch, issues) = Of(draft with { Meter = new MeterDraft() }, new SelfTestSubject(0, null, null, null));

        Assert.Null(watch);
        Assert.Equal("meter", Assert.Single(issues).Field);
    }

    [Fact]
    public void A_device_index_outside_the_draft_is_refused_on_devices()
    {
        var (watch, issues) = Of(SetupFixtures.MaintainerDraft(), new SelfTestSubject(99, null, null, null));

        Assert.Null(watch);
        Assert.Equal("devices", Assert.Single(issues).Field);
    }

    [Fact]
    public void A_charger_is_not_an_on_off_device_on_one_phase_and_is_refused_on_its_phase_field()
    {
        var (watch, issues) = Of(SetupFixtures.MaintainerDraft(), new SelfTestSubject(3, null, null, null));

        Assert.Null(watch);
        Assert.Equal("devices[3].phase", Assert.Single(issues).Field);
    }

    [Fact]
    public void A_heater_of_the_draft_is_watched_on_its_switched_entity_and_phase_with_the_others_isolated()
    {
        var (watch, issues) = Of(SetupFixtures.MaintainerDraft(), new SelfTestSubject(1, null, null, null));

        Assert.Empty(issues);
        Assert.NotNull(watch);
        Assert.Equal("switch.grzalka_1", watch.StateEntity.Value);
        Assert.DoesNotContain(watch.OtherSwitches, e => e.Value == "switch.grzalka_1");
        Assert.NotEmpty(watch.OtherSwitches);
    }

    [Fact]
    public void A_test_load_needs_its_entity_phase_and_rating_and_names_the_missing_one()
    {
        var draft = SetupFixtures.MaintainerDraft();

        Assert.Equal("testLoad.stateEntity", Assert.Single(Of(draft, new SelfTestSubject(null, null, "L1", 2000)).Issues).Field);
        Assert.Equal("testLoad.phase", Assert.Single(Of(draft, new SelfTestSubject(null, "switch.kettle", "L9", 2000)).Issues).Field);
        Assert.Single(Of(draft, new SelfTestSubject(null, "switch.kettle", "L1", null)).Issues);

        var (watch, issues) = Of(draft, new SelfTestSubject(null, "switch.kettle", "L1", 2000));
        Assert.Empty(issues);
        Assert.Equal(Phase.L1, watch!.ExpectedPhase);
        Assert.Equal((2000.0, "W"), (watch.ExpectedStep, watch.Unit)); // the maintainer draft maps a power meter: the rating is judged in watts
    }
}
