using System.Collections.Immutable;
using Switchboard.Domain.Ev;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Ev;

/// <summary>
/// EV-04 with <c>ev.consumption</c> (2026-11-17): the car's own consumption figure is what the consumption is learned from. Why it matters: in winter the
/// live car reported 0.186 kWh/km while SoC and range gave 0.145, so every trip target was about a fifth short; and the electric-only figure collapses on a
/// leg driven on fuel, which is why the configuration may point at a figure in litres that counts the fuel too.
/// </summary>
public sealed class EvConsumptionSensorTests
{
    [Fact]
    public void The_cars_figure_converted_in_the_signal_layer_is_the_consumption_the_plan_uses()
    {
        // Why: 5.3 L/100km at 3.5 kWh per litre is 0.1855 kWh/km; a trip target computed with anything else plans the wrong energy.
        var rig = new EvRig(ev: EvRig.EvBlockWithConsumption);
        rig.PlugInCar(60);
        rig.PushConsumption(5.3);

        var snapshot = rig.Tick();

        Assert.Equal(0.1855, snapshot.KwhPerKm, 9);
        Assert.Equal(KwhPerKmSource.Sensor, snapshot.KwhPerKmSource);
        Assert.Equal(KwhPerKmSource.Sensor, rig.Saved!.Learned.Source);
    }

    [Fact]
    public void What_soc_and_range_taught_is_dropped_and_the_range_no_longer_matters()
    {
        // Why: the live ev.json holds 0.145 learned from SoC and range; averaged with the car's figure it would keep trips short for many reports. With the
        // figure declared the range is not read for consumption, so a range of 0 (a PHEV at its threshold) is no note and no fallback.
        var learned = new KwhPerKmState(0.145, [new KwhPerKmSample(EvRig.Local(2026, 11, 17, 8), 0.145)], 25);
        var rig = new EvRig(ev: EvRig.EvBlockWithConsumption, initial: EvState.Default with { Learned = learned });
        rig.PlugInCar(25);
        rig.PushRange(0);
        rig.PushConsumption(18.6 / 3.5);

        var snapshot = rig.Tick();

        Assert.Equal(0.186, snapshot.KwhPerKm, 9);
        Assert.DoesNotContain(EvAssessmentCode.RangeUnusable, snapshot.Composition.Notes);
        Assert.Single(rig.Saved!.Learned.Samples);
    }

    [Fact]
    public void A_figure_gone_stale_keeps_what_was_learned_across_a_restart()
    {
        // Why: the car reports only while it is awake; parked over a weekend its figure is older than any max_age. The consumption of the last drive still holds,
        // so it is kept in ev.json and planned with, never replaced by the default.
        var rig = new EvRig(ev: EvRig.EvBlockWithConsumption);
        rig.PlugInCar(60);
        rig.PushConsumption(5.3);
        rig.Tick();

        rig.Advance(TimeSpan.FromHours(30));
        rig.PlugInCar(60);
        rig.Restart();
        var snapshot = rig.Tick();

        Assert.Equal(0.1855, snapshot.KwhPerKm, 9);
        Assert.Equal(KwhPerKmSource.Sensor, snapshot.KwhPerKmSource);
    }

    [Fact]
    public void Without_the_block_consumption_is_still_learned_from_soc_and_range()
    {
        // Why: the block is optional; a file without it must plan exactly as before.
        var rig = new EvRig();
        rig.PlugInCar(60);
        rig.PushRange(45);

        var snapshot = rig.Tick();

        Assert.Equal(60.0 / 100 * 18 / 45, snapshot.KwhPerKm, 9);
        Assert.Equal(KwhPerKmSource.SocAndRange, snapshot.KwhPerKmSource);
    }
}
