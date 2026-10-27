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

    [Fact]
    public void Setpoint_commands_of_a_power_only_charger_are_verified_on_the_derived_current()
    {
        // Live 0.10.1, the execution check: every setpoint command read "cannot be verified (no draw)"
        // with Measured empty, because a setpoint device with no draw current is Unverifiable. With the
        // derived current a fresh power and voltage verify it, and the check can never turn into a
        // failure on the understatement: a lower draw is not a mismatch of a raise (inconclusive at
        // worst) and cannot exceed a lowered setpoint.
        var rig = new SelectStopRig(devices: PowerOnly, setpoint: 6, start: "headroom") { CarWants = 20 };
        rig.Begin(0);
        rig.Hold(0, TimeSpan.FromMinutes(5));
        rig.Hold(6, TimeSpan.FromMinutes(5));
        rig.Hold(0, TimeSpan.FromMinutes(5));

        var verdicts = rig.Ledger.History.Where(h => h.Kind == Switchboard.Application.Effector.HistoryKind.Command).Select(h => h.Verdict).ToList();

        Assert.True(verdicts.Count >= 3, "The scenario must command the charger several times.");
        Assert.DoesNotContain(Switchboard.Domain.Dispatch.Verdict.Unverifiable, verdicts);
        Assert.Contains(Switchboard.Domain.Dispatch.Verdict.Match, verdicts);
        Assert.DoesNotContain(Switchboard.Domain.Dispatch.Verdict.Mismatch, verdicts);
        Assert.Equal(0, rig.Ledger.Mismatches);
    }

    private static double Climb(string device, double l1)
    {
        var rig = new SelectStopRig(devices: device, setpoint: 6, start: "headroom") { CarWants = 20 };
        rig.Begin(l1);
        rig.Hold(l1, TimeSpan.FromMinutes(7));
        return rig.Setpoint;
    }
}
