using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Dispatch;

/// <summary>
/// A setpoint device that declares only <c>draw.power</c> (0.10.2): its current is derived in the signal
/// layer as P / U, so dispatch knows what it already draws and may raise it to the headroom.
/// </summary>
public sealed class PowerOnlyDrawTests
{
    private const string PowerOnly = """
          - id: ev_charger
            type: modulating
            actuator: number.charger_current
            stop_actuator: select.charger_force
            stop_option: "1"
            resume_option: "0"
            phase: L1
            min: 6
            max: 20
            step: 1
            value_per_kwh: 2.03
            state_reports: on_change
            draw: { power: sensor.charger_draw_power, max_age: 60s }
        """;

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(3.0)]
    public void A_charger_that_declares_only_its_power_rises_to_the_headroom_like_one_that_declares_its_current(double l1)
    {
        // The live 0.10.1 finding: go-e's current sensor stands still at a load while its power
        // refreshes every 2 s. A setpoint device with a power draw only gave no contribution, so its
        // limit was the bare headroom and a 6 A charger never rose. The derived current (P / U) is
        // what a declared current would have said, so both configurations must climb the same way.
        var declared = Climb(SelectStopRig.Charger, l1);
        var derived = Climb(PowerOnly, l1);

        Assert.True(declared > 6, "The reference charger must climb, or this test proves nothing.");
        Assert.Equal(declared, derived);
    }

    [Fact]
    public void A_power_that_falls_silent_at_a_load_stops_the_increases()
    {
        // Intent: derived relief and derived headroom are as untrusted as a stale current. Once the
        // power is older than its max_age (and not silent at 0), the charger gets no further increase.
        var rig = new SelectStopRig(devices: PowerOnly, setpoint: 6, start: "headroom") { CarWants = 20 };
        rig.Begin(0);
        rig.Poll(0, 10);
        rig.Poll(0, 10);
        rig.ReportDraw = false;
        rig.Hold(0, TimeSpan.FromMinutes(2));
        var sent = rig.Path.Calls.Count;

        rig.Hold(0, TimeSpan.FromMinutes(3));

        Assert.Equal(sent, rig.Path.Calls.Count);
        Assert.True(rig.Setpoint < 12, "The charger must not have been taken to its limit on a stale power.");
    }

    private static double Climb(string device, double l1)
    {
        var rig = new SelectStopRig(devices: device, setpoint: 6, start: "headroom") { CarWants = 20 };
        rig.Begin(l1);
        rig.Hold(l1, TimeSpan.FromMinutes(7));
        return rig.Setpoint;
    }
}
