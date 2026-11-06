using Switchboard.Domain.Economics;
using Switchboard.Domain.Ev;
using Switchboard.Domain.Planning;
using static Switchboard.Tests.Support.PlannerFixtures;

namespace Switchboard.Tests.Domain.Ev;

/// <summary>
/// The reserve model of 2026-11-06 through the real planner: the floor is due at the next <c>floor_by</c> (15:00), after the next day's PV, so the planner
/// charges the night only for what the forecast will not bring. Why it matters: the maintainer wants the car charged as cheaply as possible, and a night
/// charge from the cheap zone (0.60) must not take the place of PV the next day (worth the lost export, 0.35). On 2026-11-05 the reserve was placed against a
/// far deadline and the charger's own sources bought the grid whenever the module had no goal; here the night is empty under a sunny forecast, carries only the
/// shortfall under a weak one, and all of it under a dull one.
/// </summary>
public sealed class EvReservePlanTests
{
    // Thursday 2026-11-05 17:00 local: the maintainer's evening. The car is at 40 %, the reserve is 50 %: 10 points of 18 kWh with 10 % loss = 1.98 kWh.
    private static readonly DateTimeOffset Evening = Local(2026, 11, 5, 17);
    private static readonly DateTimeOffset NightEnd = Local(2026, 11, 6, 7);
    private const double ReserveKwh = 1.98;

    private static PlanSnapshot PlanWith(double surplusPerSunHourKwh, TimeOnly? floorBy = null)
    {
        var p = EvTestParameters.Defaults with { FloorPercent = 50, FloorBy = floorBy ?? EvTestParameters.Defaults.FloorBy };
        var composition = EvComposer.Compose(new EvComposerInput
        {
            Now = Evening,
            Zone = Warsaw,
            Device = Car,
            CapacityKwh = EvTestParameters.CapacityKwh,
            Parameters = p,
            Mode = EvMode.Auto,
            Plugged = true,
            SocPercent = 40,
            KwhPerKm = 0.20,
            IsHoliday = _ => false,
            CheapZone = "offpeak",
            DeviceSources = SourceSet.PvOnly,
        });

        // The maintainer's weekday G13: afternoon peak to 21:00, off-peak through the night, morning peak 07:00-13:00 with the sun from 08:00 to 12:00, off-peak
        // 13:00-15:00. The surplus is per hour, derated, less the house.
        var inputs = Custom(Evening, 24, i =>
        {
            var hour = (17 + i) % 24;
            return hour switch
            {
                >= 21 or < 7 => ("offpeak", 0.60, 0.35, 0),
                >= 7 and < 13 => ("morning_peak", 0.88, 0.35, hour is >= 8 and < 12 ? surplusPerSunHourKwh : 0),
                >= 13 and < 15 => ("offpeak", 0.60, 0.35, 0),
                _ => ("afternoon_peak", 1.45, 0.35, 0),
            };
        });

        return Solve(Input(Evening, inputs, [Charger(SourceSet.PvOnly)], composition.Goals.Select(g => g.Request), Capacity(5000)));
    }

    private static double NightGridKwh(PlanSnapshot plan) =>
        plan.Device(Car)!.Entries.Where(e => e.Start < NightEnd && e.Want is PlanWant.Run or PlanWant.Urgent && e.Class is EnergySource.Grid).Sum(e => e.EnergyKwh);

    private static PlanGoal Reserve(PlanSnapshot plan) => plan.Device(Car)!.Goals.Single(g => g.Deadline == Local(2026, 11, 6, 15));

    [Fact]
    public void A_sunny_forecast_leaves_the_night_empty_and_the_reserve_waits_for_the_sun()
    {
        var plan = PlanWith(surplusPerSunHourKwh: 1.0);

        Assert.Equal(0, NightGridKwh(plan), 9);
        Assert.Equal(ReserveKwh, Reserve(plan).PlannedKwh, 6);
        Assert.Equal(0, Reserve(plan).ShortfallKwh, 6);
    }

    [Fact]
    public void A_weak_forecast_charges_the_night_only_for_the_part_the_sun_will_not_bring()
    {
        // 4 hours x 0.25 kWh = 1.0 kWh from the sun; the remaining 0.98 kWh in the earliest cheap hours of the night.
        var plan = PlanWith(surplusPerSunHourKwh: 0.25);

        Assert.Equal(ReserveKwh - 1.0, NightGridKwh(plan), 6);
        Assert.Equal(Local(2026, 11, 5, 21), plan.Device(Car)!.Entries.First(e => e.Class is EnergySource.Grid).Start);
    }

    [Fact]
    public void A_reserve_due_in_the_morning_would_buy_the_night_even_under_a_sunny_forecast()
    {
        // The rejected alternative (a reserve due 07:00 has no PV in its window): pinned so that moving floor_by before the sun shows what it costs.
        var plan = PlanWith(surplusPerSunHourKwh: 1.0, floorBy: new TimeOnly(7, 0));

        Assert.Equal(ReserveKwh, NightGridKwh(plan), 6);
    }

    [Fact]
    public void A_dull_forecast_charges_the_whole_reserve_in_the_night_and_never_in_a_peak()
    {
        var plan = PlanWith(surplusPerSunHourKwh: 0);

        Assert.Equal(ReserveKwh, NightGridKwh(plan), 6);
        var gridSlots = plan.Device(Car)!.Entries.Where(e => e.Class is EnergySource.Grid).Select(e => plan.Slots.Single(s => s.Slot.Start == e.Start).Zone);
        Assert.All(gridSlots, zone => Assert.Equal("offpeak", zone));
    }
}
