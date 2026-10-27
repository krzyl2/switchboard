using System.Collections.Immutable;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Planning;
using Switchboard.Domain.Signals;

namespace Switchboard.Tests.Support;

/// <summary>
/// Inputs for the pure planner and its service (15-PLANNER, slice 4): the maintainer's installation of the worked example (3 x 25 A, 230 V, 2.5 kW reserve per
/// phase, a go-e on L1, G13 prices) in the Warsaw zone, and small builders for the synthetic instances of the property tests.
/// </summary>
internal static class PlannerFixtures
{
    /// <summary>Plans with the test's cancellation token (the analyzer asks every call that can take one to pass it).</summary>
    public static PlanSnapshot Solve(PlanInput input) => Planner.Plan(input, TestContext.Current.CancellationToken);

    public static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    public static readonly DeviceName Car = DeviceName.Parse("ev_charger");

    public static readonly ConstraintKey L1 = ConstraintKey.Main(Phase.L1);
    public static readonly ConstraintKey L2 = ConstraintKey.Main(Phase.L2);
    public static readonly ConstraintKey L3 = ConstraintKey.Main(Phase.L3);

    public static PlannerParameters Parameters(int refinePasses = 3, TimeSpan? horizon = null, TimeSpan? slot = null, double derate = 0.85) =>
        new(
            TimeSpan.FromMinutes(15),
            TimeSpan.FromSeconds(60),
            TimeSpan.FromMinutes(45),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(60),
            horizon ?? TimeSpan.FromHours(48),
            slot ?? TimeSpan.FromHours(1),
            derate,
            PvShape.HalfSine,
            refinePasses,
            2,
            0.05,
            0.10,
            new HardGoalParameters(TimeSpan.FromMinutes(30)),
            new LoadForecastParameters(14, 0.8, 450),
            new PlannerStorageParameters(0.1, 0.10, 0, 0),
            new GoalParameters(TimeSpan.FromHours(1), 0));

    public static ForecastParameters Forecast(PlannerParameters? parameters = null) => (parameters ?? Parameters()).Forecast();

    /// <summary>The shipped G13 template (placeholder prices: off-peak 0.60, morning peak 1.10, afternoon peak 1.60 in October to March).</summary>
    public static Tariff G13
    {
        get
        {
            var snippet = File.ReadAllText(RepoPaths.Combine("deploy", "examples", "economics-g13.yaml"));
            var economics = TestConfigurations.Valid(TestConfigurations.ThreePhase() + "\n" + snippet).Configuration!.Economics!;
            return new Tariff(economics.Tariff!, economics.Holidays, Warsaw);
        }
    }

    public static readonly Tariff Export = new(new FlatPrice(0.35), HolidayCalendar.Empty, Warsaw);

    public static DateTimeOffset Local(int year, int month, int day, int hour, int minute = 0)
    {
        var wall = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTime(new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(wall, Warsaw), TimeSpan.Zero), Warsaw);
    }

    /// <summary>The go-e of the example: 6 to 13 A on L1 (1.38 to 2.99 kW at 230 V), minimum run one minute, may start.</summary>
    public static PlanDevice Charger(SourceSet? sources = null, double value = 2.03, bool canStart = true, TimeSpan? minRun = null) =>
        new(Car, PlanDeviceKind.Modulating, 1380, 2990, minRun ?? TimeSpan.FromMinutes(1), [new PlanKeyShare(L1, 1)], canStart, new ValueSpec(value, ValueFollows.Static, 1), sources, 7);

    /// <summary>A 1.7 kW heater on one phase.</summary>
    public static PlanDevice Heater(string name, ConstraintKey key, SourceSet? sources = null, double value = 0.35) =>
        new(DeviceName.Parse(name), PlanDeviceKind.Relay, 1700, 1700, TimeSpan.FromMinutes(1), [new PlanKeyShare(key, 1)], true, new ValueSpec(value, ValueFollows.Static, 1), sources ?? SourceSet.PvOnly, 11);

    /// <summary>The static allocation limit of the example: 25 A less a 2.5 kW reserve at 230 V, 3.25 kW per phase.</summary>
    public static ImmutableDictionary<ConstraintKey, double> Capacity(double w = 3250) =>
        ImmutableDictionary<ConstraintKey, double>.Empty.Add(L1, w).Add(L2, w).Add(L3, w);

    public static SourceSet Offpeak => SourceSet.TryParse("pv_and_zone:offpeak", out var set, out _) ? set! : throw new InvalidOperationException();

    public static GoalRequest Goal(
        DeviceName device,
        double remaining,
        DateTimeOffset? deadline,
        bool hard = true,
        TimeSpan? margin = null,
        SourceSet? sources = null,
        bool escalate = true,
        GoalOnMet onMet = GoalOnMet.Opportunistic,
        long revision = 1,
        GoalKind kind = GoalKind.Soc) =>
        new(device, kind, remaining, null, 90, 40, null, deadline, margin ?? TimeSpan.Zero, hard, sources, escalate, onMet, null, GoalOrigin.Config, revision);

    /// <summary>The Tuesday forecast of the worked example, kWh per local hour from 08:00 to 16:00 (before the derate).</summary>
    public static PvForecast TuesdayPv(double scale = 1)
    {
        double[] kwh = [0.3, 1.2, 2.4, 3.3, 3.6, 3.2, 2.3, 1.2, 0.3];
        return PvForecast.FromPeriodEnergies(kwh.Select((e, i) => new PvPeriod(Local(2026, 10, 27, 8 + i), Local(2026, 10, 27, 9 + i), e * 1000 * scale)));
    }

    public static PlanInputs Inputs(DateTimeOffset now, PvForecast pv, IEnumerable<DateTimeOffset>? cuts = null, PlannerParameters? parameters = null, Tariff? import = null, Tariff? export = null)
    {
        var p = parameters ?? Parameters();
        var sources = new PlanInputSources(Warsaw, HolidayCalendar.Empty, import ?? G13, export ?? Export, null, null, pv, LoadProfile.Empty);
        return PlanInputs.Build(now, now + p.Horizon, cuts, p.Forecast(), sources);
    }

    public static PlanInput Input(
        DateTimeOffset now,
        PlanInputs inputs,
        IEnumerable<PlanDevice> devices,
        IEnumerable<GoalRequest> goals,
        ImmutableDictionary<ConstraintKey, double>? capacity = null,
        PlannerParameters? parameters = null,
        double minMargin = 0.05,
        long revision = 1) =>
        new(now, inputs, [.. devices], [.. goals], capacity ?? Capacity(), parameters ?? Parameters(), minMargin, revision);

    /// <summary>
    /// Hand-made per-slot inputs (no tariff, no forecast): <paramref name="slot"/> gives, for each slot index, its zone, import and export price and the surplus
    /// (derated PV less house load, written as the PV energy with no base load). The grid is hourly from <paramref name="now"/> in the Warsaw zone.
    /// </summary>
    public static PlanInputs Custom(DateTimeOffset now, int slots, Func<int, (string? Zone, double? Import, double? Export, double Surplus)> slot, IEnumerable<DateTimeOffset>? cuts = null)
    {
        var grid = SlotGrid.Build(now, now + TimeSpan.FromHours(slots), TimeSpan.FromHours(1), Warsaw, cuts);
        var list = ImmutableArray.CreateBuilder<PlanInputSlot>();
        for (var i = 0; i < grid.Count; i++)
        {
            var (zone, import, export, surplus) = slot(i);
            list.Add(new PlanInputSlot(
                grid.Slots[i],
                zone,
                import is { } p ? new SlotPrice(p, SlotPriceBasis.Tariff, zone) : null,
                export is { } e ? new SlotPrice(e, SlotPriceBasis.Tariff, null) : null,
                surplus,
                0));
        }

        return new PlanInputs(grid, list.ToImmutable(), PvBasis.Series, PvCoverage.Full, grid.To, LoadBasis.Profile, 7, PlanPriceBasis.Tariff, null, []);
    }

    /// <summary>
    /// The worked example: Monday 2026-10-26 18:00 local, the car at 40 percent of 18 kWh with a hard goal of 90 percent by Wednesday 07:00 (margin 30 minutes),
    /// sources <c>pv_and_zone:offpeak</c>, three heaters on pv only, and Tuesday's PV forecast (or none).
    /// </summary>
    public static PlanInput WorkedExample(bool withPv = true, DateTimeOffset? now = null, double remaining = 9.9, bool withHeaters = true)
    {
        var at = now ?? Local(2026, 10, 26, 18);
        var deadline = Local(2026, 10, 28, 7);
        var inputs = Inputs(at, withPv ? TuesdayPv() : PvForecast.None, [deadline - TimeSpan.FromMinutes(30)]);
        List<PlanDevice> devices = [Charger(Offpeak)];
        if (withHeaters)
        {
            devices.AddRange([Heater("heater_l1", L1), Heater("heater_l2", L2), Heater("heater_l3", L3)]);
        }

        return Input(at, inputs, devices, [Goal(Car, remaining, deadline, margin: TimeSpan.FromMinutes(30), sources: Offpeak)]);
    }
}
