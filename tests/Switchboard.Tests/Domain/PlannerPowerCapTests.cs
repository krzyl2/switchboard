using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Planning;
using static Switchboard.Tests.Support.PlannerFixtures;

namespace Switchboard.Tests.Domain;

/// <summary>
/// Power caps of a goal (<c>control.ev.power_bands</c>): with the whole night ahead the charger draws at most 2 kW until midnight and 3 kW after it. Why it
/// matters: the caps are the maintainer's wish only while there is time for them. A goal that cannot be met under them must run as without them, and a cap
/// can only lower a slot's ceiling: protection and the device's own limit stay where they are.
/// </summary>
public sealed class PlannerPowerCapTests
{
    private static readonly DeviceName DeviceA = DeviceName.Parse("device_a");
    private static readonly DateTimeOffset Evening = Local(2026, 10, 26, 21);
    private static readonly DateTimeOffset Midnight = Local(2026, 10, 27, 0);
    private static readonly DateTimeOffset Morning = Local(2026, 10, 27, 7);

    /// <summary>6 to 20 A on L1 at 230 V (1380 to 4600 W), one ampere is 230 W, no minimum run.</summary>
    private static PlanDevice Amps() =>
        new(DeviceA, PlanDeviceKind.Modulating, 1380, 4600, TimeSpan.Zero, [new PlanKeyShare(L1, 1)], true, new ValueSpec(10, ValueFollows.Static, 1), null, 1, 230);

    private static readonly PowerCap[] Night = [new(Evening, Midnight, 2000), new(Midnight, Morning, 3000)];

    private static PlanInput Make(double kwh, DateTimeOffset deadline, bool spread = false, PowerCap[]? caps = null)
    {
        var goal = Goal(DeviceA, kwh, deadline) with { Spread = spread, PowerCaps = [.. caps ?? Night] };
        return Input(Evening, Custom(Evening, 10, _ => ("z", 1.0, 0.1, 0)), [Amps()], [goal], Capacity(5000));
    }

    private static List<PlanEntry> Runs(PlanSnapshot plan) => [.. plan.Device(DeviceA)!.Entries.Where(e => e.Want is PlanWant.Run or PlanWant.Urgent)];

    [Fact]
    public void With_the_night_ahead_the_charge_keeps_to_the_bands_rounded_down_to_whole_amperes()
    {
        // Why: the maintainer's night. 9.3 kWh from 21:00: 2 kW is 8.7 A, so 8 A (1840 W) until midnight, then 3 kW is 13.04 A, so 13 A (2990 W). The plan
        // budgets what dispatch will really set, so the energy adds up.
        var input = Make(9.3, Morning);

        var plan = Solve(input);

        var runs = Runs(plan);
        Assert.Equal(9.3, runs.Sum(e => e.EnergyKwh), 9);
        Assert.All(runs.Where(e => e.Start < Midnight), e => Assert.Equal(1840, e.MaxPowerW, 6));
        Assert.All(runs.Where(e => e.Start >= Midnight), e => Assert.Equal(2990, e.MaxPowerW, 6));
        Assert.All(runs, e => Assert.True(e.EnergyKwh <= (e.MaxPowerW / 1000) + 1e-9));
        Assert.Equal(Evening, runs[0].Start);
        Assert.Empty(PlanVerifier.Verify(input, plan));
    }

    [Fact]
    public void Without_time_for_the_bands_the_goal_runs_as_if_it_had_none()
    {
        // Why: the bands are a preference, never a reason to miss a goal. 8 kWh by 23:00 cannot be drawn at 1840 W (3.68 kWh), so the full ceiling stays.
        var input = Make(8, Local(2026, 10, 26, 23));

        var runs = Runs(Solve(input));

        Assert.Equal(8, runs.Sum(e => e.EnergyKwh), 9);
        Assert.All(runs, e => Assert.Equal(4600, e.MaxPowerW, 6));
    }

    [Fact]
    public void A_cap_above_the_ceiling_never_raises_it()
    {
        // Why: protection outranks everything. A band of 10 kW on a 4.6 kW charger leaves the device's ceiling; the guard's limits stay above any cap.
        var input = Make(9.3, Morning, caps: [new(Evening, Morning, 10_000)]);

        var plan = Solve(input);

        Assert.All(Runs(plan), e => Assert.Equal(4600, e.MaxPowerW, 6));
        Assert.Empty(PlanVerifier.Verify(input, plan));
    }

    [Fact]
    public void A_spread_charge_stays_under_the_evening_band_and_spreads_the_rest_after_midnight()
    {
        // Why: spread alone would run 2070 W all night (20 kWh over 10 h); the evening band holds 1840 W until midnight and the hours after it take the rest.
        var input = Make(20, Morning, spread: true);

        var plan = Solve(input);

        var runs = Runs(plan);
        Assert.Equal(20, runs.Sum(e => e.EnergyKwh), 9);
        Assert.All(runs.Where(e => e.Start < Midnight), e => Assert.Equal(1840, e.MaxPowerW, 6));
        Assert.All(runs.Where(e => e.Start >= Midnight), e => Assert.Equal(2070, e.MaxPowerW, 6));
        Assert.Empty(PlanVerifier.Verify(input, plan));
    }

    [Fact]
    public void A_goal_without_caps_plans_exactly_as_before()
    {
        // Why: the digests of every installation without bands depend on it.
        var goal = Goal(DeviceA, 9.3, Morning);
        var input = Input(Evening, Custom(Evening, 10, _ => ("z", 1.0, 0.1, 0)), [Amps()], [goal], Capacity(5000));
        var empty = Input(Evening, Custom(Evening, 10, _ => ("z", 1.0, 0.1, 0)), [Amps()], [goal with { PowerCaps = [] }], Capacity(5000));

        Assert.Equal(Solve(input).Canonical(), Solve(empty).Canonical());
        Assert.All(Runs(Solve(input)), e => Assert.Equal(4600, e.MaxPowerW, 6));
    }
}
