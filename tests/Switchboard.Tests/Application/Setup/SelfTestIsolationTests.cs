using Switchboard.Application.Setup;
using Switchboard.Application.Setup.SelfTest;
using Switchboard.Application.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Setup;

/// <summary>
/// A guided watch is isolated by the same rule as the recorder: another declared device switching,
/// or another device's draw changing by more than T_quiet of the watched device, inside the window
/// makes the step not this device's. The watched device's own entity and draw are never "other".
/// </summary>
public sealed class SelfTestIsolationTests
{
    [Fact]
    public void Other_devices_and_their_draws_are_watched_never_the_device_itself()
    {
        var draft = SetupFixtures.MaintainerDraft();

        var (switches, draws) = SelfTestIsolation.Of(draft, 1, EntityId.Parse("switch.grzalka_1"), 7, 1700, SetupFixtures.Control, SetupFixtures.Defaults.SelfTest.Classifier);

        Assert.Equal(["switch.grzalka_2", "switch.grzalka_3", "number.goe_422752_amp"], switches.Select(e => e.Value));
        Assert.Equal(["sensor.goe_422752_nrg_4", "sensor.goe_422752_nrg_7"], draws.Select(d => d.Entity.Value));

        // T_quiet of the 7 A / 1.7 kW heater: 0.25 x rating, or 3 sigma when larger.
        Assert.Equal(("A", 1.75), (draws[0].Unit, draws[0].Threshold));
        Assert.Equal(("W", 425.0), (draws[1].Unit, draws[1].Threshold));
    }

    [Fact]
    public void A_draw_in_kilowatts_is_read_in_kilowatts()
    {
        var draft = SetupFixtures.MaintainerDraft();
        var charger = draft.Devices[3];
        var kilowatts = draft with { Devices = [.. draft.Devices.Take(3), charger with { Draw = charger.Draw! with { PowerUnit = "kW" } }] };

        var (_, draws) = SelfTestIsolation.Of(kilowatts, 0, EntityId.Parse("switch.grzalka_2"), 7, 1700, SetupFixtures.Control, SetupFixtures.Defaults.SelfTest.Classifier);

        Assert.Equal("kW", draws.Single(d => d.Entity.Value == "sensor.goe_422752_nrg_7").Unit);
    }
}
