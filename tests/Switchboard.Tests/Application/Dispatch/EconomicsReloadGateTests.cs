using Switchboard.Application.Dispatch;
using Switchboard.Domain.Decisions;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.EconomicsScenarios;

namespace Switchboard.Tests.Application.Dispatch;

/// <summary>
/// The allowance gate on the step a configuration reload first makes devices judgeable (live 2026-10-26: start restore_only became
/// headroom with sources and an economics section; one heater was started from the afternoon peak with the hour importing, while its
/// siblings were correctly refused).
/// </summary>
public sealed class EconomicsReloadGateTests
{
    private static readonly string[] AllOff = ["heater_l1", "heater_l2", "heater_l3", "ev_charger"];

    private static string Devices(string start, bool sources, string carPhase) =>
        string.Concat(new[] { "L1", "L2", "L3" }.Select(p =>
            Device($"heater_{p.ToLowerInvariant()}", p, 7.4, 1700, 0.35, sources ? "pv_only" : null, $"    start: {start}\n    rotation_group: cwu_heaters\n")))
        + Device("ev_charger", carPhase, 8, 1840, 2.03, sources ? "pv_and_zone:offpeak" : null, $"    start: {start}\n");

    [Theory]
    [InlineData("L1")]
    [InlineData("L2")]
    public void After_a_reload_that_makes_devices_judgeable_nothing_starts_from_a_peak_with_no_surplus(string carPhase)
    {
        // Why: protection of the user's money and the gate's whole point (ECO-06/ECO-07): sources decide what energy a device may take.
        // A device that was never judged before (restore_only) must not slip through on the step the gate first applies to it, because a
        // sibling's refusal frees headroom for it after the allocation was already read.
        var s = DispatchScenario.Create(Devices("restore_only", false, carPhase), AllOff, OneBoundary, start: Workday(20, 40));
        s.Time.Zone = Warsaw;
        Export(s, -942);
        Run(s, Feed(s, -582), 1);
        Assert.Empty(Starts(s));

        s.Reconfigure(Document(Devices("headroom", true, carPhase)));
        Run(s, Feed(s, -582), 4);

        Assert.Empty(Starts(s));
        Assert.All(AllOff, device => Assert.Equal(ReasonCode.SourceNotAllowed, Pending(s, device)));
    }
}
