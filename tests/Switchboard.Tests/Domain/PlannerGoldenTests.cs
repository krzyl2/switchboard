using Switchboard.Domain.Devices;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Planning;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.PlannerFixtures;

namespace Switchboard.Tests.Domain;

/// <summary>
/// The pure planner against the worked example of 15-PLANNER (the maintainer's installation, G13 placeholder prices) and its variants. Why it matters: these
/// are the numbers the design promised, to the kWh. A plan that put the car's energy on the grid in the afternoon peak, ignored the cheaper surplus at
/// 13:00, or lost the latest safe start would cost real money or a missed trip, and no later slice (dispatch, UI) can repair a plan that is wrong at the source.
/// <para>
/// Tolerance: the design's tables are rounded by hand to two decimals (0.57, 1.59, 2.36, 2.61, 2.27, 0.50; cost 3.47; references 5.94 and 14.91), and the
/// latest grid start 03:11 is 9.9 / 2.99 h = 3 h 18 min 40 s before 06:30, i.e. 03:11:20. The tests therefore compare energies and costs to 0.0055 (half a printed unit, 2.355 is printed 2.36 and 3.465 is printed 3.47) and times to a
/// minute: finer than the document states, coarser than the arithmetic.
/// </para>
/// </summary>
public sealed class PlannerGoldenTests(Xunit.ITestOutputHelper output)
{
    // Half a unit of the last printed digit (0.005) plus room for float noise: 2.355 is printed as 2.36 and 3.465 as 3.47, so 0.005 itself would sit on the edge.
    private const double DocTolerance = 0.0055;

    private static PlanEntry Entry(PlanSnapshot plan, DeviceName device, DateTimeOffset start) =>
        plan.Device(device)!.Entries.Single(e => e.Start == start);

    [Fact]
    public void The_worked_example_places_the_car_on_six_surplus_cells_on_tuesday_and_defers_everything_else()
    {
        // Why: PV surplus at 0.35 is cheaper than the off-peak grid at 0.60, so a hard goal with two days to go waits for the sun; the 13:00 cell is in the
        // off-peak zone but still class pv. Energy follows the surplus S_t = forecast x 0.85 - 0.45 kWh base load, earliest cells first.
        var input = WorkedExample();

        var plan = Solve(input);

        var expected = new (int Hour, double Kwh)[] { (9, 0.57), (10, 1.59), (11, 2.36), (12, 2.61), (13, 2.27), (14, 0.50) };
        foreach (var (hour, kwh) in expected)
        {
            var entry = Entry(plan, Car, Local(2026, 10, 27, hour));
            Assert.Equal(PlanWant.Run, entry.Want);
            Assert.Equal(PlanClasses.Pv, entry.Class);
            Assert.Equal(kwh, entry.EnergyKwh, DocTolerance);
            Assert.Equal(2990, entry.MaxPowerW, 6);
            Assert.NotNull(entry.Goal);
        }

        var run = plan.Device(Car)!.Entries.Where(e => e.Want == PlanWant.Run).ToList();
        Assert.Equal(6, run.Count);
        Assert.Equal(9.9, run.Sum(e => e.EnergyKwh), 9);
        Assert.Equal(PlanWant.Defer, Entry(plan, Car, Local(2026, 10, 26, 21)).Want);
        Assert.Equal(PlanWant.Defer, Entry(plan, Car, Local(2026, 10, 27, 8)).Want);
        Assert.Equal(PlanWant.None, Entry(plan, Car, Local(2026, 10, 27, 15)).Want);
        Assert.DoesNotContain(PlanNote.PvForecastMissing, plan.Notes);
    }

    [Fact]
    public void The_worked_example_costs_3_47_against_5_94_for_v2_and_14_91_as_soon_as_possible()
    {
        // Why: the savings the UI will show are these references. v2 on its own starts at 21:00 off-peak (9.9 x 0.60); as soon as possible runs from 18:00 in
        // the afternoon peak (8.97 x 1.60 + 0.93 x 0.60). The plan's 9.9 x 0.35 is the surplus value.
        var plan = Solve(WorkedExample());

        Assert.Equal(3.47, plan.Cost.Planned, DocTolerance);
        Assert.Equal(5.94, plan.Cost.ReferenceV2, DocTolerance);
        Assert.Equal(14.91, plan.Cost.ReferenceAsap, DocTolerance);
    }

    [Fact]
    public void The_latest_safe_start_of_the_worked_example_is_wednesday_03_11_and_the_goal_is_not_urgent_on_monday()
    {
        // Why: grid-only delivery needs 9.9 / 2.99 = 3.31 h before 06:30, so 03:11 is the last start that still makes the trip; on Monday evening the
        // plan is far from it and the goal is merely on track (cheap off-peak hours exist on Tuesday night to repair a bad PV day).
        var goal = Solve(WorkedExample()).Device(Car)!.Goals.Single();

        var latest = goal.LatestGridStart!.Value;
        Assert.True(Math.Abs((latest - LocalS(2026, 10, 28, 3, 11, 20)).TotalMinutes) < 1, $"latest grid start was {latest:O}");
        Assert.Equal(GoalStatus.OnTrack, goal.Status);
        Assert.False(goal.Urgent);
        Assert.Equal(0, goal.ShortfallKwh, 9);
    }

    [Theory]
    [InlineData(2, 40, false)]
    [InlineData(2, 41, false)]
    [InlineData(2, 42, true)]
    [InlineData(2, 45, true)]
    public void A_hard_goal_turns_urgent_thirty_minutes_before_its_latest_grid_start(int hour, int minute, bool urgent)
    {
        // Why: escalation margin 30 min before 03:11:20 is 02:41:20. Past it the plan must run now at full power whatever it counted on (the forecast may have
        // failed); before it the ordinary replan can still repair a short day without paying the dear zone.
        var now = Local(2026, 10, 28, hour, minute);
        var input = WorkedExample(now: now);

        var plan = Solve(input);

        var goal = plan.Device(Car)!.Goals.Single();
        Assert.Equal(urgent, goal.Urgent);
        var first = plan.Device(Car)!.Entries.First();
        Assert.Equal(urgent ? PlanWant.Urgent : PlanWant.Run, first.Want);
        Assert.Equal(0, goal.ShortfallKwh, 9);
        Assert.Empty(PlanVerifier.Verify(input, plan));
    }

    [Fact]
    public void Without_a_pv_forecast_the_same_goal_fills_the_off_peak_grid_from_monday_21_00()
    {
        // Why: 12-EV-MODULE requires that a missing forecast still gets the car charged: zero PV, the cheapest allowed energy is the off-peak grid at 0.60, earliest
        // slot first (robust against an under-delivery): 21:00, 22:00, 23:00 at 2.99 kWh and the 0.93 kWh remainder at 00:00; cost 9.9 x 0.60 = 5.94.
        var plan = Solve(WorkedExample(withPv: false));

        var entries = plan.Device(Car)!.Entries.Where(e => e.Want == PlanWant.Run).ToList();
        Assert.Equal([Local(2026, 10, 26, 21), Local(2026, 10, 26, 22), Local(2026, 10, 26, 23), Local(2026, 10, 27, 0)], entries.Select(e => e.Start).ToArray());
        Assert.Equal([2.99, 2.99, 2.99, 0.93], entries.Select(e => Math.Round(e.EnergyKwh, 2)).ToArray());
        Assert.All(entries, e => Assert.Equal("zone:offpeak", e.Class));
        Assert.Equal(5.94, plan.Cost.Planned, DocTolerance);
        Assert.Contains(PlanNote.PvForecastMissing, plan.Notes);
    }

    [Fact]
    public void The_heaters_without_a_goal_get_none_in_every_slot_and_use_no_capacity()
    {
        // Why: the plan never invents a want; a pv-only heater stays a v2 opportunist, so every slot is None and the car keeps the whole 3.25 kW on L1.
        var plan = Solve(WorkedExample());

        foreach (var heater in new[] { "heater_l1", "heater_l2", "heater_l3" })
        {
            var entries = plan.Device(DeviceName.Parse(heater))!.Entries;
            Assert.Equal(plan.Slots.Length, entries.Length);
            Assert.All(entries, e => Assert.Equal(PlanWant.None, e.Want));
        }
    }

    [Fact]
    public void The_worked_example_passes_the_verifier()
    {
        // Why: the verifier is the gate before a plan is published; the reference example must never be refused by it.
        var input = WorkedExample();

        Assert.Empty(PlanVerifier.Verify(input, Solve(input)));
    }

    [Fact]
    public void Solving_the_worked_example_takes_milliseconds()
    {
        // Why: max_solve_time is 2 s by default; the design says the problem is tens of slots and a handful of devices. A solve measured in seconds would
        // mean the greedy and its refinement are not what the design promised. The bound is a generous guard, the measured figure goes in the report.
        var input = WorkedExample();
        Solve(input);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            Solve(input);
        }

        output.WriteLine($"one solve of the worked example took {watch.Elapsed.TotalMilliseconds / 20:0.00} ms");
        Assert.True(watch.Elapsed.TotalSeconds / 20 < 0.2, $"one solve took {watch.Elapsed.TotalMilliseconds / 20:0.0} ms");
    }

    private static DateTimeOffset LocalS(int year, int month, int day, int hour, int minute, int second) => PlannerFixtures.Local(year, month, day, hour, minute).AddSeconds(second);
}
