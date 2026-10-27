using Switchboard.Domain.Decisions;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.EconomicsScenarios;

namespace Switchboard.Tests.Application.Dispatch;

/// <summary>
/// Displacement (0.10.2, 14-ECONOMICS): a running device worth less is stopped when a device worth more is refused only by the
/// hourly projection and would start with the cheaper one gone. Found live: the car (2.03) was off with 2.4 kW free and a heater
/// (0.35) running; the car could not start, nothing stopped the heater. Numbers: 600 Wh with 60 min to go, the grid exporting 900 W
/// beside a 1.5 kW heater, so 2.4 kW free; a 1.5 kW car after the heater is projected to 0 Wh (below the 150 Wh floor), without
/// the heater to 1500 Wh. Quiet phases, so only the allowance decides.
/// </summary>
public sealed class DisplacementTests
{
    private static readonly DateTimeOffset Noon = Workday(11);

    private static string Car(double value = 2.03) => Device("ev_charger", "L2", 6.6, 1500, value, "pv_only");

    private static string Heater(string id = "heater_l1", string phase = "L1", double value = 0.35, string extra = "") => Device(id, phase, 6.6, 1500, value, "pv_only", extra);

    private static int Displacements(GuardScenario s) => Stops(s).Count(d => d.Reason == ReasonCode.ValueRankBelowCutoff);

    private static GuardScenario Run(string devices, string[] off, double wh = 600, double exportW = 900, string? economics = null, bool settledLockouts = true)
    {
        var s = DispatchScenario.Create(devices, off, OneBoundary, start: Noon, settledLockouts: settledLockouts, extra: "\n" + (economics ?? Economics()));
        s.Time.Zone = Warsaw;
        Export(s, exportW);
        EconomicsScenarios.Run(s, Feed(s, wh), 3);
        return s;
    }

    [Fact]
    public void The_cheaper_running_device_is_stopped_when_the_dearer_one_then_fits()
    {
        // The live case. The car does not start beside the heater; the heater is stopped for it, as value_rank_below_cutoff (a higher-value
        // device took the headroom), and the car is not started in the same step.
        var s = Run(Heater() + Car(), off: ["ev_charger"]);

        var stop = Assert.Single(Stops(s), d => d.Device!.Value.Value == "heater_l1");
        Assert.Equal(ReasonCode.ValueRankBelowCutoff, stop.Reason);
        var start = Assert.Single(Starts(s));
        Assert.Equal(("ev_charger", ReasonCode.BalanceSurplus), (start.Device!.Value.Value, start.Reason));
        Assert.True(stop.At < start.At, "the car starts on a later step than the stop, once the heater is the target off");
    }

    [Fact]
    public void Nothing_is_displaced_when_the_dearer_device_would_still_not_fit()
    {
        // Intent: a stop that buys nothing is a loss. With 300 Wh the car's start leaves 150 - 1500 + ... below the floor even without the heater.
        var s = Run(Heater() + Car(), off: ["ev_charger"], wh: 100, exportW: -300);

        Assert.Equal(0, Displacements(s));
    }

    [Theory]
    [InlineData(2.03, 2.03)]   // equal values: no gain
    [InlineData(0.35, 0.36)]   // a gap below order_min_gap_fraction (5 %): a price tick is not a reason to swap
    public void A_device_that_is_not_worth_more_by_the_gap_displaces_nothing(double running, double wanted)
    {
        var s = Run(Heater(value: running) + Car(wanted), off: ["ev_charger"]);

        Assert.Equal(0, Displacements(s));
    }

    [Fact]
    public void A_cheaper_device_never_displaces_a_dearer_one()
    {
        // Intent: the order is one way. The running car stays when the heater is refused.
        var s = Run(Heater() + Car(), off: ["heater_l1"], wh: 600, exportW: 900);

        Assert.Equal(0, Displacements(s));
    }

    [Fact]
    public void A_heater_under_its_min_on_is_not_displaced_and_the_wait_is_not_a_stop()
    {
        // Intent (the default of the design): a displacement never stops a device before its min_on. The lockouts start at the first step
        // here, so the heater is locked for the first 6 minutes of the run; nothing is requested and nothing is deferred.
        var s = Run(Heater() + Car(), off: ["ev_charger"], settledLockouts: false);

        Assert.Empty(Stops(s));
    }

    [Fact]
    public void Only_one_device_is_displaced_in_a_step_even_when_two_cheaper_ones_run()
    {
        // Rotation group of identical heaters: one stage per step. With 3.9 kW free the car needs one heater gone, not two.
        var devices = Heater("heater_l1", extra: "    rotation_group: cwu_heaters\n") + Heater("heater_l3", "L3", extra: "    rotation_group: cwu_heaters\n") + Car();
        var s = Run(devices, off: ["ev_charger"], exportW: 600, wh: 0);

        Assert.Equal(1, Displacements(s));
    }

    [Fact]
    public void Without_an_economics_section_nothing_is_displaced()
    {
        // Intent: ECO-15. A file without economics: decides as 0.9, byte for byte; displacement is a new behaviour and needs the section.
        var s = DispatchScenario.Create(Heater() + Car(), ["ev_charger"], OneBoundary, start: Noon);
        s.Time.Zone = Warsaw;
        Export(s, 900);

        EconomicsScenarios.Run(s, Feed(s, 600), 3);

        Assert.Equal(0, Displacements(s));
    }
}
