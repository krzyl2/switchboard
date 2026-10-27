using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Tests.Domain;

/// <summary>
/// The load model shared by guard and dispatch (DISP-06 and the live relief finding of 0.3.0):
/// a would-start adds its ceiling, a would-stop relieves at most what the device really draws, a
/// setpoint is a ceiling and never a draw, and an unknown or stale state relieves nothing.
/// </summary>
public sealed class LoadModelTests
{
    private static readonly DeviceSpec Charger = new(
        DeviceName.Parse("charger"),
        DeviceType.Modulating,
        [new StageSpec(1, [Phase.L1], new Amperes(20))],
        new ModulatingRange(new Amperes(6), new Amperes(20), new Amperes(1)),
        2.03,
        null,
        TimeSpan.FromSeconds(20),
        false,
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(60))
    {
        Start = StartPolicy.RestoreOnly,
    };

    private static readonly DeviceSpec MeasuredCharger = Charger with { CurrentDrawPhases = [Phase.L1] };

    private static readonly DeviceSpec Heater = new(
        DeviceName.Parse("heater"),
        DeviceType.OnOff,
        [new StageSpec(1, [Phase.L1], new Amperes(6.6))],
        null,
        0.35,
        null,
        TimeSpan.FromSeconds(1),
        false,
        TimeSpan.FromMinutes(6),
        TimeSpan.FromSeconds(90))
    {
        Start = StartPolicy.RestoreOnly,
    };

    [Fact]
    public void Relief_never_exceeds_measured_draw_so_an_idle_charger_hides_no_overload()
    {
        // The live case: L1 measures 19 A, the go-e is at a 20 A setpoint, the car draws 0 A. Held
        // at its 6 A minimum it relieves nothing, so the estimate stays at the measured 19 A (0.3.0
        // read 5 A and would have missed a later overload).
        var reported = DeviceLevel.At(new Amperes(20));
        var draw = new DeviceDrawSample(Charger.Id, Phase.L1, true, Amperes.Zero, null);
        var basis = LoadModel.Contribution(MeasuredCharger, Phase.L1, reported, draw);

        var delta = LoadModel.InFlightDelta(MeasuredCharger, Phase.L1, DeviceLevel.At(new Amperes(6)), reported, basis);

        Assert.Equal(19, 19 + delta.Value, 9);
        var drawing = LoadModel.Contribution(MeasuredCharger, Phase.L1, reported, draw with { Current = new Amperes(16) });
        Assert.Equal(-10, LoadModel.InFlightDelta(MeasuredCharger, Phase.L1, DeviceLevel.At(new Amperes(6)), reported, drawing).Value, 9);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.95)]
    [InlineData(0.8)]
    public void A_current_derived_from_power_never_widens_a_limit_or_a_relief_whatever_the_power_factor(double powerFactor)
    {
        // The signal layer derives I = P / U. The real current is P / (U cos φ), never smaller, so the
        // derived draw understates. Intent: every use of the understatement errs towards less: a smaller
        // own share (limit = headroom + own share), a smaller relief on a decrease, a larger in-flight
        // increase. A rated 14 A charger at 230 V drawing 3.22 kW at the given power factor.
        const double Volts = 230;
        const double Watts = 3220;
        var real = Watts / (Volts * powerFactor);
        var derived = Watts / Volts;
        var reported = DeviceLevel.At(new Amperes(14));
        var fromReal = LoadModel.Contribution(MeasuredCharger, Phase.L1, reported, new DeviceDrawSample(Charger.Id, Phase.L1, true, new Amperes(real), null));
        var fromDerived = LoadModel.Contribution(MeasuredCharger, Phase.L1, reported, new DeviceDrawSample(Charger.Id, Phase.L1, true, new Amperes(derived), null));
        var lower = DeviceLevel.At(new Amperes(6));
        var higher = DeviceLevel.At(new Amperes(20));

        Assert.True(fromDerived <= fromReal);
        Assert.True(-LoadModel.InFlightDelta(MeasuredCharger, Phase.L1, lower, reported, fromDerived) <= -LoadModel.InFlightDelta(MeasuredCharger, Phase.L1, lower, reported, fromReal));
        Assert.True(LoadModel.InFlightDelta(MeasuredCharger, Phase.L1, higher, reported, fromDerived) >= LoadModel.InFlightDelta(MeasuredCharger, Phase.L1, higher, reported, fromReal));
    }

    [Fact]
    public void Ending_a_storage_discharge_counts_the_whole_step()
    {
        // A battery discharging 10 A lowers the phase current by 10 A. Taking it to zero charge raises
        // the current by 10 A, to 5 A charge by 15 A: the estimate must count the step from the signed
        // discharge, or shadow would invent the discharge's 10 A as headroom. A charge setpoint is
        // still a ceiling, never a draw.
        var battery = Charger with { Id = DeviceName.Parse("battery"), Type = DeviceType.Storage, Range = new ModulatingRange(new Amperes(-10), new Amperes(16), new Amperes(1)) };
        var discharging = DeviceLevel.At(new Amperes(-10));
        var basis = LoadModel.Contribution(battery, Phase.L1, discharging, null);

        Assert.Equal(-10, basis.Value, 9);
        Assert.Equal(10, LoadModel.InFlightDelta(battery, Phase.L1, DeviceLevel.At(Amperes.Zero), discharging, basis).Value, 9);
        Assert.Equal(15, LoadModel.InFlightDelta(battery, Phase.L1, DeviceLevel.At(new Amperes(5)), discharging, basis).Value, 9);
        Assert.Equal(0, LoadModel.Contribution(battery, Phase.L1, DeviceLevel.At(new Amperes(8)), null).Value, 9);
    }

    [Fact]
    public void Modulating_without_draw_relieves_nothing()
    {
        // No draw signal, no knowledge of the draw: the setpoint is only a ceiling.
        var reported = DeviceLevel.At(new Amperes(20));
        var basis = LoadModel.Contribution(Charger, Phase.L1, reported, null);

        Assert.Equal(0, basis.Value);
        Assert.Equal(0, LoadModel.InFlightDelta(Charger, Phase.L1, DeviceLevel.At(new Amperes(6)), reported, basis).Value);
    }

    [Fact]
    public void Relay_without_draw_relieves_rated_only_while_reported_on()
    {
        // The stated assumption without a draw signal: a relay reported on draws its rating.
        var on = DeviceLevel.Relay(true);
        var off = DeviceLevel.Relay(false);

        Assert.Equal(-6.6, LoadModel.InFlightDelta(Heater, Phase.L1, off, on, LoadModel.Contribution(Heater, Phase.L1, on, null)).Value, 9);
        Assert.Equal(0, LoadModel.InFlightDelta(Heater, Phase.L1, off, off, LoadModel.Contribution(Heater, Phase.L1, off, null)).Value);
        Assert.Equal(0, LoadModel.InFlightDelta(Heater, Phase.L1, off, null, LoadModel.Contribution(Heater, Phase.L1, null, null)).Value);
    }

    [Fact]
    public void Stale_draw_relieves_nothing_and_blocks_that_devices_increases()
    {
        // A stale draw is no knowledge: its contribution is zero, so holding the device relieves
        // nothing. (Its increases are blocked by dispatch: DispatchEngineTests.)
        var reported = DeviceLevel.At(new Amperes(16));
        var stale = new DeviceDrawSample(Charger.Id, Phase.L1, false, null, null);

        var basis = LoadModel.Contribution(MeasuredCharger, Phase.L1, reported, stale);

        Assert.Equal(0, basis.Value);
        Assert.Equal(0, LoadModel.InFlightDelta(MeasuredCharger, Phase.L1, DeviceLevel.At(new Amperes(6)), reported, basis).Value);
    }

    [Fact]
    public void Shadow_start_adds_target_ceiling()
    {
        // A would-start is added to the measured current: shadow mode cannot invent headroom.
        var off = DeviceLevel.Relay(false);
        Assert.Equal(6.6, LoadModel.InFlightDelta(Heater, Phase.L1, DeviceLevel.Relay(true), off, LoadModel.Contribution(Heater, Phase.L1, off, null)).Value, 9);

        // A charger raised from 10 to 16 A while drawing 10 A may take 6 A more.
        var ten = DeviceLevel.At(new Amperes(10));
        var draw = new DeviceDrawSample(Charger.Id, Phase.L1, true, new Amperes(10), null);
        Assert.Equal(6, LoadModel.InFlightDelta(MeasuredCharger, Phase.L1, DeviceLevel.At(new Amperes(16)), ten, LoadModel.Contribution(MeasuredCharger, Phase.L1, ten, draw)).Value, 9);
    }

    [Fact]
    public void Group_term_is_the_worst_case_without_a_sub_meter()
    {
        // CONN-03 estimated group current: a relay counts its rating when on or unknown, a setpoint
        // its value (its maximum when unknown), a fresh draw its measurement.
        Assert.Equal(6.6, LoadModel.GroupTerm(Heater, Phase.L1, null, null).Value, 9);
        Assert.Equal(0, LoadModel.GroupTerm(Heater, Phase.L1, DeviceLevel.Relay(false), null).Value);
        Assert.Equal(20, LoadModel.GroupTerm(Charger, Phase.L1, null, null).Value);
        Assert.Equal(12, LoadModel.GroupTerm(Charger, Phase.L1, DeviceLevel.At(new Amperes(12)), null).Value);
        Assert.Equal(3, LoadModel.GroupTerm(MeasuredCharger, Phase.L1, DeviceLevel.At(new Amperes(12)), new DeviceDrawSample(Charger.Id, Phase.L1, true, new Amperes(3), null)).Value);
    }

    [Fact]
    public void Levels_step_from_floor_and_storage_never_targets_discharge()
    {
        // DISP-01: a storage range of −10…16 A is offered from zero charge up, never below.
        var battery = Charger with
        {
            Id = DeviceName.Parse("battery"),
            Type = DeviceType.Storage,
            Range = new ModulatingRange(new Amperes(-10), new Amperes(16), new Amperes(2)),
            Stages = [new StageSpec(1, [Phase.L1], new Amperes(16))],
        };

        var levels = DeviceLevels.All(battery);

        Assert.Equal([0.0, 2, 4, 6, 8, 10, 12, 14, 16], levels.Select(l => l.Setpoint!.Value.Value));
        Assert.All(levels, l => Assert.True(l.Setpoint!.Value.Value >= 0));
        Assert.Equal(0, DeviceLevels.Off(battery).Setpoint!.Value.Value);
        Assert.Equal(15, DeviceLevels.All(Charger).Length);
        Assert.False(DeviceLevels.All(Charger with { HasStopActuator = true })[0].Enabled);
    }
}
