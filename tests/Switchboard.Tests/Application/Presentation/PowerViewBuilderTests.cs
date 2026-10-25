using System.Collections.Immutable;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Guard;
using Switchboard.Application.Presentation;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Presentation;

/// <summary>
/// The dashboard's balance and phase bars come from one control step, through the real store,
/// guard and dispatch: house load from primary measurements only (SIG-08, 07-PITFALLS: never a
/// derived sensor), the house's own part as the only residual, unknown figures named as unknown
/// with their reason (never 0), and the guard's own comparison deciding a reserve banner.
/// </summary>
public sealed class PowerViewBuilderTests
{
    private static readonly Dictionary<Phase, DateTimeOffset> NoForeign = [];

    [Fact]
    public void House_load_is_net_import_plus_generation_from_primary_signals()
    {
        // 500 W imported on L1 and 1.2 kW of PV split evenly by the inverter's phase currents:
        // L1's load is 900 W, derived here from the meter and the inverter, nothing else.
        var loop = Loop(TestDevices.Document());
        PushMeter(loop, "-500", "-500", "-500", pv: "1200", pvCurrents: "1.74");

        var view = Build(loop.Step());

        var l1 = view.Phases.Single(p => p.Phase == Phase.L1).Balance;
        Assert.Equal(500, l1.NetImport.Value!.Value, 6);
        Assert.Equal(400, l1.Generation.Value!.Value, 6);
        Assert.Equal(900, l1.Load.Value!.Value, 6);
    }

    [Fact]
    public void House_load_is_the_residual_after_the_declared_devices()
    {
        // The heaters report on (1.5 kW each, rated model) and the charger at 16 A (no draw: unknown):
        // L2's house part is its load minus the heater, never the load itself, or the dashboard would
        // count the heater twice.
        var loop = Loop(TestDevices.Document());
        PushMeter(loop, "-2000", "-2000", "-2000", pv: "0", pvCurrents: "0");

        var view = Build(loop.Step());

        var l2 = view.Phases.Single(p => p.Phase == Phase.L2).Balance;
        Assert.Equal(1500, l2.Controlled.Value!.Value, 6);
        Assert.Equal(500, l2.Uncontrolled.Value!.Value, 6);
        Assert.Equal(l2.Load.Value, l2.Controlled.Value + l2.Uncontrolled.Value);
    }

    [Fact]
    public void Totals_are_sums_of_phases()
    {
        var loop = Loop(TestDevices.Document());
        PushMeter(loop, "-700", "-1900", "-2300", pv: "900", pvCurrents: "1.3");

        var view = Build(loop.Step());

        Assert.Equal(view.Phases.Sum(p => p.Balance.NetImport.Value!.Value), view.Total.NetImport.Value!.Value, 6);
        Assert.Equal(view.Phases.Sum(p => p.Balance.Generation.Value!.Value), view.Total.Generation.Value!.Value, 6);
        Assert.Equal(view.Phases.Sum(p => p.Balance.Load.Value!.Value), view.Total.Load.Value!.Value, 6);
        Assert.Equal(2, view.Phases.Count(p => p.Balance.Uncontrolled.Value is not null));
        Assert.Null(view.Total.Uncontrolled.Value);
    }

    [Fact]
    public void Unknown_part_has_reason_and_no_value()
    {
        // The charger has no power draw: L1's controlled part is unknown, and so are its house part
        // and the totals that depend on them. Each says why; none is 0.
        var loop = Loop(TestDevices.Document());
        PushMeter(loop, "-700", "-700", "-700", pv: "unavailable", pvCurrents: "0");

        var view = Build(loop.Step());

        var l1 = view.Phases.Single(p => p.Phase == Phase.L1).Balance;
        Assert.Equal((null, PowerUnknownReason.GenerationUnknown), (l1.Generation.Value, l1.Generation.Unknown));
        Assert.Equal((null, PowerUnknownReason.GenerationUnknown), (l1.Load.Value, l1.Load.Unknown));
        Assert.Equal((null, PowerUnknownReason.DevicePowerUnknown), (l1.Controlled.Value, l1.Controlled.Unknown));
        Assert.Contains(Switchboard.Domain.Devices.DeviceName.Parse("ev_charger"), l1.UnknownDevices);
        Assert.Null(l1.Uncontrolled.Value);
        Assert.NotNull(l1.Uncontrolled.Unknown);
        Assert.Equal(700, l1.NetImport.Value!.Value, 6);
        Assert.Contains(view.Phases.Single(p => p.Phase == Phase.L1).Bar, s => s.Kind == SegmentKind.Unattributed);
    }

    [Fact]
    public void Inverter_asleep_is_noted_not_hidden()
    {
        // Live 0.4.0: SolarEdge sleeps at night; with unavailable_as_zero it counts as 0 W, and the
        // dashboard says so instead of presenting a measured 0.
        var yaml = TestDevices.Document().Replace("    role: inverter_power\n", "    role: inverter_power\n    unavailable_as_zero: true\n", StringComparison.Ordinal);
        Assert.NotEqual(TestDevices.Document(), yaml);
        var loop = Loop(yaml);
        PushMeter(loop, "-700", "-700", "-700", pv: "unavailable", pvCurrents: "0");

        var view = Build(loop.Step());

        Assert.Equal(0, view.Total.Generation.Value);
        Assert.Equal(GenerationNote.AsleepCountedAsZero, view.Total.GenerationNote);
        Assert.Equal(2100, view.Total.Load.Value!.Value, 6);
    }

    [Fact]
    public void Without_an_inverter_generation_is_not_declared_and_counts_as_zero()
    {
        // Rendered "not declared", never "0 W"; the load is the net import.
        var loop = Loop(TestConfigurations.ThreePhase(inverter: false).TrimEnd('\n') + "\ndevices:\n" + TestDevices.Reference.ReplaceLineEndings("\n"));
        PushMeter(loop, "-700", "-700", "-700", pv: null, pvCurrents: null);

        var view = Build(loop.Step());

        Assert.Equal((null, PowerUnknownReason.NotDeclared), (view.Total.Generation.Value, view.Total.Generation.Unknown));
        Assert.Equal(0, view.Total.Generation.InBalance);
        Assert.Equal(2100, view.Total.Load.Value!.Value, 6);
    }

    [Fact]
    public void Controlled_and_foreign_are_separate_segments_and_scaled_when_pv_covers_the_load()
    {
        // L2: 1 A of net import while 2 kW of PV covers the rest; the heater's 1.5 kW alone is more
        // current than the meter sees, so the parts are scaled into the bar and the bar says so.
        var loop = Loop(TestDevices.Document());
        PushMeter(loop, "-230", "-230", "-230", pv: "6000", pvCurrents: "8.7", currents: "1");

        var view = Build(loop.Step());

        var l2 = view.Phases.Single(p => p.Phase == Phase.L2);
        Assert.True(l2.BarScaled);
        Assert.Equal(1, l2.Bar.Sum(s => s.Current.Value), 6);
        Assert.Equal([SegmentKind.Base, SegmentKind.Controlled, SegmentKind.Foreign], l2.Bar.Select(s => s.Kind));
    }

    [Fact]
    public void A_phase_over_its_reserve_threshold_gets_a_banner_with_the_guards_timing()
    {
        // 20 A on L3 is above In − reserve (14.13 A) and inside the may-add band: the guard asks for
        // a reduction only after persistence_down_cycles boundaries, so the banner says "by" that
        // boundary (an upper bound), never "next cycle".
        var loop = Loop(TestDevices.Document());
        PushMeter(loop, "-700", "-700", "-4600", pv: "0", pvCurrents: "0", currents: "3", currentL3: "20");

        var step = loop.Step();
        var view = Build(step);

        var banner = Assert.Single(view.Banners);
        Assert.Equal(Phase.L3, banner.Phase);
        Assert.Equal(ReactionTiming.ByBoundary, banner.Timing);
        var setup = loop.Harness.Holder.Current.Snapshot.DispatchSetup!;
        Assert.Equal(PowerViewBuilder.NextBoundary(step.Evaluation.At, setup.Timing.Cycle) + (setup.Timing.Cycle * (setup.Timing.PersistenceDownCycles - 1)), banner.DueAt);
    }

    [Fact]
    public void An_exporting_phase_above_the_threshold_magnitude_raises_no_reserve_banner()
    {
        // 20 A flowing out on L3 (PV) is no reserve crossing in the guard's own comparison.
        var loop = Loop(TestDevices.Document());
        PushMeter(loop, "-700", "-700", "4600", pv: "15000", pvCurrents: "21.7", currents: "3", currentL3: "20");

        var view = Build(loop.Step());

        Assert.Empty(view.Banners);
        var l3 = view.Phases.Single(p => p.Phase == Phase.L3);
        Assert.False(l3.OverReserve);
        Assert.Equal([SegmentKind.Export], l3.Bar.Select(s => s.Kind));
    }

    [Fact]
    public void Next_boundary_is_strictly_after_now_on_the_epoch_grid()
    {
        var cycle = TimeSpan.FromSeconds(30);
        var onBoundary = new DateTimeOffset(2026, 10, 24, 12, 0, 30, TimeSpan.Zero);

        Assert.Equal(onBoundary + cycle, PowerViewBuilder.NextBoundary(onBoundary, cycle));
        Assert.Equal(onBoundary, PowerViewBuilder.NextBoundary(onBoundary - TimeSpan.FromSeconds(1), cycle));
    }

    [Fact]
    public void In_shadow_the_banner_stays_while_the_meter_is_over_although_a_reduction_was_decided()
    {
        // 05-UI-SPEC: the banner shows whenever a phase is over its reserve threshold. 20 A on L3
        // (threshold 14.13 A) with heater_l3 on: the guard asks for a reduction, dispatch decides the
        // would-stop (once heater_l3's min_on, started at the first step, has run out), and from then
        // on the estimate counts the heater's relief (20 − 6.5 < 14.13 A).
        // Nothing is switched in shadow mode, the meter still reads 20 A, so the banner must stay and
        // keep naming the decided reduction.
        var loop = Loop(TestDevices.Document());
        var steps = new List<(PhaseGuardStatus Guard, PowerView View)>();
        for (var i = 0; i < 48; i++)
        {
            PushMeter(loop, "-700", "-700", "-4600", pv: "0", pvCurrents: "0", currents: "3", currentL3: "20");
            var step = loop.Step();
            steps.Add((step.Evaluation.Status.Phases.Single(p => p.Phase == Phase.L3), Build(step)));
            loop.Harness.Time.Advance(TimeSpan.FromSeconds(15));
        }

        var relieved = steps.Where(s => s.Guard.EffectiveCurrent!.Value.Value <= s.Guard.ReserveThreshold.Value).ToList();
        Assert.True(relieved.Count > 0, "the decided reduction never relieved the estimate: the check would pass vacuously");
        Assert.All(steps, s => Assert.Contains(s.View.Banners, b => b.Phase == Phase.L3));
        Assert.All(relieved, s =>
        {
            var banner = s.View.Banners.Single(b => b.Phase == Phase.L3);
            Assert.Equal(20, banner.Current.Value, 6);
            Assert.Equal(s.Guard.EffectiveCurrent, banner.EffectiveCurrent);
            Assert.Contains(banner.Planned, c => c.Device.Value == "heater_l3");
            Assert.True(s.View.Phases.Single(p => p.Phase == Phase.L3).OverReserve);
        });
    }

    [Fact]
    public void A_decided_start_that_lifts_only_the_estimate_over_the_threshold_raises_the_banner_with_the_estimate()
    {
        // Live 0.4.1 night: a would-started heater stays in flight in shadow mode, so the guard's
        // estimate can cross the threshold while the meter reads 7.5 A. The banner follows the guard's
        // comparison and carries the estimate, so its title can say what is over.
        var loop = Loop(TestDevices.Document());
        PushMeter(loop, "-700", "-700", "-1725", pv: "0", pvCurrents: "0", currents: "3", currentL3: "7.5");
        var step = WithPhase(loop.Step(), Phase.L3, g => g with { EffectiveCurrent = new Amperes(14.5) });

        var view = Build(step);

        var banner = Assert.Single(view.Banners);
        Assert.Equal(7.5, banner.Current.Value, 6);
        Assert.Equal(14.5, banner.EffectiveCurrent!.Value.Value, 6);
        Assert.True(view.Phases.Single(p => p.Phase == Phase.L3).OverReserve);
    }

    [Fact]
    public void Banner_timing_follows_the_band_of_the_estimate_as_the_guard_does()
    {
        // GuardEngine.React times a single shed and the cycle reductions from the band of I_est. 26 A
        // measured (1.04 × In, ordinary cycle) with a decided 7 A start in flight is 33 A (1.32 × In):
        // the guard sheds one device at the next phase sample, and the banner must say so.
        var loop = Loop(TestDevices.Document());
        PushMeter(loop, "-700", "-700", "-5980", pv: "0", pvCurrents: "0", currents: "3", currentL3: "26");
        var raised = WithPhase(loop.Step(), Phase.L3, g => g with { EffectiveCurrent = new Amperes(33) });

        var banner = Assert.Single(Build(raised).Banners);

        Assert.Equal(TierReaction.ShedOneImmediately, banner.Reaction);
        Assert.Equal(ReactionTiming.NextSample, banner.Timing);
    }

    [Fact]
    public void After_a_decided_shed_the_banner_promises_no_immediate_step_the_guard_will_not_take()
    {
        // The reverse: 32.5 A measured (1.30 × In) with the shed already counted is 25.5 A (1.02 × In,
        // ordinary cycle). The guard takes no further step at the next sample; without a reserve
        // request the reduction is due by the persistence boundary, with one at the next boundary.
        var loop = Loop(TestDevices.Document());
        PushMeter(loop, "-700", "-700", "-7475", pv: "0", pvCurrents: "0", currents: "3", currentL3: "32.5");
        var step = loop.Step();
        var relieved = WithPhase(step, Phase.L3, g => g with { EffectiveCurrent = new Amperes(25.5), ReductionRequested = false });
        var requested = WithPhase(step, Phase.L3, g => g with { EffectiveCurrent = new Amperes(25.5), ReductionRequested = true });

        var byBoundary = Assert.Single(Build(relieved).Banners);
        var nextBoundary = Assert.Single(Build(requested).Banners);

        Assert.Equal(TierReaction.ReduceOrdinaryCycle, byBoundary.Reaction);
        Assert.Equal(ReactionTiming.ByBoundary, byBoundary.Timing);
        Assert.Equal(ReactionTiming.NextBoundary, nextBoundary.Timing);
        Assert.Equal(PowerViewBuilder.NextBoundary(step.Evaluation.At, loop.Harness.Holder.Current.Snapshot.DispatchSetup!.Timing.Cycle), nextBoundary.DueAt);
    }

    [Fact]
    public void Shedding_everything_stays_on_the_measured_band_whatever_the_estimate()
    {
        // 40 A measured (1.6 × In) opens the top band; the guard sheds everything from the measured
        // current, never the estimate, so relief that does not materialize cannot hide it.
        var loop = Loop(TestDevices.Document());
        PushMeter(loop, "-700", "-700", "-9200", pv: "0", pvCurrents: "0", currents: "3", currentL3: "40");
        var relieved = WithPhase(loop.Step(), Phase.L3, g => g with { EffectiveCurrent = new Amperes(20) });

        var banner = Assert.Single(Build(relieved).Banners);

        Assert.Equal(TierReaction.ShedAllOnPhase, banner.Reaction);
        Assert.Equal(ReactionTiming.Now, banner.Timing);
    }

    [Fact]
    public void A_sleeping_inverter_does_not_make_a_fresh_house_load_look_hours_old()
    {
        // Live 0.4.1: SolarEdge reports 0 W at dusk and then nothing; with unavailable_as_zero that
        // silence counts as 0 W. The 0 W is a rule value: the generation row keeps its dusk age, but the
        // house load is as old as the meter, or a calm system would look frozen every night.
        var yaml = TestDevices.Document().Replace("    role: inverter_power\n", "    role: inverter_power\n    unavailable_as_zero: true\n", StringComparison.Ordinal);
        Assert.NotEqual(TestDevices.Document(), yaml);
        var loop = Loop(yaml);
        PushMeter(loop, "-700", "-700", "-700", pv: "0", pvCurrents: "0");
        var dusk = loop.Harness.Now;
        loop.Step();
        loop.Harness.Time.Advance(TimeSpan.FromHours(3));
        PushMeter(loop, "-700", "-700", "-700", pv: null, pvCurrents: null);

        var view = Build(loop.Step());

        var now = loop.Harness.Now;
        Assert.Equal(GenerationNote.AsleepCountedAsZero, view.Total.GenerationNote);
        Assert.Equal(dusk, view.Total.Generation.ReportedAt);
        Assert.Equal(now, view.Total.Load.ReportedAt);
        var l2 = view.Phases.Single(p => p.Phase == Phase.L2).Balance;
        Assert.Equal(now, l2.Load.ReportedAt);
        Assert.Equal(now, l2.Uncontrolled.ReportedAt);
    }

    [Fact]
    public void A_lost_phase_voltage_converts_at_the_nominal_voltage_and_every_segment_is_finite()
    {
        // A tripped L3 reports 0 V and 0 A: the moment the breaker-trip metric exists for. Converting
        // the heater's 1.5 kW at 0 V would be 1500/0, a NaN the API cannot even serialise.
        var loop = Loop(TestDevices.Document());
        PushMeter(loop, "-700", "-700", "0", pv: "0", pvCurrents: "0", currents: "3", currentL3: "0", voltageL3: "0");

        var view = Build(loop.Step());

        var l3 = view.Phases.Single(p => p.Phase == Phase.L3);
        Assert.False(l3.VoltageMeasured);
        Assert.Equal(230, l3.Voltage.Value, 6);
        Assert.NotEmpty(l3.Bar);
        Assert.All(view.Phases.SelectMany(p => p.Bar), s => Assert.True(double.IsFinite(s.Current.Value), $"{s.Kind}: {s.Current.Value}"));
        Assert.True(view.Phases.Single(p => p.Phase == Phase.L1).VoltageMeasured);
    }

    internal static PowerView Build(GuardStep step) => PowerViewBuilder.Build(step, NoForeign, TestStatistics.Options().VoltageLossFraction);

    /// <summary>The same step with one phase of the guard's status changed: the builder is pure, so the guard's numbers can be set directly.</summary>
    private static GuardStep WithPhase(GuardStep step, Phase phase, Func<PhaseGuardStatus, PhaseGuardStatus> change)
    {
        var status = step.Evaluation.Status;
        var phases = status.Phases.Select(p => p.Phase == phase ? change(p) : p).ToImmutableArray();
        return step with { Evaluation = step.Evaluation with { Status = status with { Phases = phases } } };
    }

    private static PresentationLoop Loop(string yaml) => new(yaml);

    /// <summary>The three phase powers (positive = export), currents and voltages, the inverter, and the reference devices on.</summary>
    internal static void PushMeter(PresentationLoop loop, string a, string b, string c, string? pv, string? pvCurrents, string currents = "3", string? currentL3 = null, string voltageL3 = "230")
    {
        var states = new List<(string, string, string?)>
        {
            ("switch.heater_1", "on", null),
            ("switch.heater_2", "on", null),
            ("switch.heater_3", "on", null),
            ("number.charger_current", "16", "A"),
            ("sensor.meter_power_a", a, "W"),
            ("sensor.meter_power_b", b, "W"),
            ("sensor.meter_power_c", c, "W"),
            ("sensor.meter_current_a", currents, "A"),
            ("sensor.meter_current_b", currents, "A"),
            ("sensor.meter_current_c", currentL3 ?? currents, "A"),
            ("sensor.meter_voltage_a", "230", "V"),
            ("sensor.meter_voltage_b", "230", "V"),
            ("sensor.meter_voltage_c", voltageL3, "V"),
        };
        if (pv is not null)
        {
            states.Add(("sensor.inverter_power", pv, "W"));
        }

        if (pvCurrents is not null)
        {
            states.AddRange([("sensor.inverter_current_a", pvCurrents, "A"), ("sensor.inverter_current_b", pvCurrents, "A"), ("sensor.inverter_current_c", pvCurrents, "A")]);
        }

        loop.Harness.Push([.. states]);
    }
}

/// <summary>The production control loop on a steppable clock, keeping the steps it produced.</summary>
internal sealed class PresentationLoop
{
    public PresentationLoop(string yaml)
    {
        Harness = new GuardRunnerHarness(yaml);
        Harness.Runner.Stepped += (_, step) => Steps.Add(step);
    }

    public GuardRunnerHarness Harness { get; }

    public List<GuardStep> Steps { get; } = [];

    public GuardStep Step()
    {
        Harness.Runner.Step();
        return Steps[^1];
    }
}
