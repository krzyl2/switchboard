using System.Collections.Immutable;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Tests.Domain;

/// <summary>
/// The shedding order of 04 §2 and DISP-02: within the overloaded phase only, cheapest kWh first,
/// faster devices first on a tie; a modulating device is reduced to its minimum before it is
/// stopped; a device under a min_on lockout is taken last but never spared; no_shed is never taken.
/// </summary>
public sealed class ShedOrderTests
{
    [Fact]
    public void Cheapest_kwh_sheds_first()
    {
        var steps = ShedOrder.For(Phase.L1, [Candidate(Relay("car_heater", 1.20)), Candidate(Relay("boiler", 0.35)), Candidate(Relay("pool", 0.80))]);

        Assert.Equal(["boiler", "pool", "car_heater"], steps.Select(s => s.Device.Value));
    }

    [Fact]
    public void Shed_priority_overrides_value_per_kwh()
    {
        // DISP-02: "overridable per device": the boiler is worth little per kWh but the user wants it kept.
        var steps = ShedOrder.For(Phase.L1, [Candidate(Relay("boiler", 0.35) with { ShedPriority = 5 }), Candidate(Relay("pool", 0.80))]);

        Assert.Equal(["pool", "boiler"], steps.Select(s => s.Device.Value));
    }

    [Fact]
    public void Faster_reaction_time_breaks_ties()
    {
        var steps = ShedOrder.For(Phase.L1, [Candidate(Relay("slow", 0.5) with { ReactionTime = TimeSpan.FromSeconds(20) }), Candidate(Relay("fast", 0.5))]);

        Assert.Equal(["fast", "slow"], steps.Select(s => s.Device.Value));
    }

    [Fact]
    public void Only_devices_on_the_overloaded_phase()
    {
        // Energy cannot be moved between phases: shedding L2 does nothing for L1.
        var steps = ShedOrder.For(Phase.L1, [Candidate(Relay("on_l2", 0.1, Phase.L2)), Candidate(Relay("on_l1", 0.9))]);

        Assert.Equal(["on_l1"], steps.Select(s => s.Device.Value));
    }

    [Fact]
    public void Three_phase_device_on_each_phase()
    {
        var heater = Relay("three", 0.3, Phase.L1, Phase.L2, Phase.L3);

        foreach (var phase in new[] { Phase.L1, Phase.L2, Phase.L3 })
        {
            Assert.Equal(["three"], ShedOrder.For(phase, [Candidate(heater)]).Select(s => s.Device.Value));
        }
    }

    [Fact]
    public void No_shed_never_a_candidate()
    {
        // 04 §2: a device that physically cannot stop is declared no_shed and excluded.
        var steps = ShedOrder.For(Phase.L1, [Candidate(Relay("compressor", 0.01) with { NoShed = true }), Candidate(Relay("boiler", 0.35))]);

        Assert.Equal(["boiler"], steps.Select(s => s.Device.Value));
    }

    [Fact]
    public void Under_min_on_taken_last_not_skipped()
    {
        // Lockouts never outrank protection: the locked device is still in the list, at the end.
        var steps = ShedOrder.For(Phase.L1, [Candidate(Relay("boiler", 0.35), underMinOn: true), Candidate(Relay("pool", 0.80))]);

        Assert.Equal(["pool", "boiler"], steps.Select(s => s.Device.Value));
        Assert.True(steps[1].UnderMinOn);
    }

    [Fact]
    public void Modulating_reduced_to_minimum_before_stopped()
    {
        // 04 §2: "first reduced to their minimum, and only stopped if that is not enough" - stopped
        // only through a stop actuator (DISP-01).
        var steps = ShedOrder.For(Phase.L1, [Candidate(Charger() with { HasStopActuator = true }, new SetpointApplied(new Amperes(16)))]);

        Assert.Equal([(ShedAction.ReduceToMinimum, 10.0), (ShedAction.SwitchOff, 6.0)], steps.Select(s => (s.Action, s.Relief.Value)));
    }

    [Fact]
    public void Shed_order_never_spends_a_step_on_a_modulating_device_that_cannot_switch_off()
    {
        // Stage 2 open issue: a charger without a stop actuator has a setpoint range that excludes
        // zero. Once at its minimum, a "switch off" step would relieve nothing, so the next real
        // candidate is taken instead (here the boiler, worth more per kWh).
        var limited = ImmutableDictionary<int, StageLimit>.Empty.Add(1, StageLimit.AtMinimum);

        var steps = ShedOrder.For(Phase.L1, [new ShedCandidate(Charger() with { ValuePerKwh = 0.1 }, [new SetpointApplied(new Amperes(16))], false, limited), Candidate(Relay("boiler", 0.35))]);

        Assert.Equal([("boiler", ShedAction.SwitchOff)], steps.Select(s => (s.Device.Value, s.Action)));
        Assert.Equal([(ShedAction.ReduceToMinimum, 10.0)], ShedOrder.For(Phase.L1, [Candidate(Charger(), new SetpointApplied(new Amperes(16)))]).Select(s => (s.Action, s.Relief.Value)));
    }

    [Fact]
    public void An_idle_charger_whose_draw_is_at_its_floor_never_takes_a_shed_step_before_a_real_candidate()
    {
        // Relief never exceeds a measured draw: the cheapest device on L1 is a charger at a 16 A
        // setpoint whose car draws nothing. Reducing it relieves 0 A, so the one step of a shed-one
        // reaction goes to the boiler; the charger's step comes last (still taken when everything
        // on the phase is shed). With a real draw of 10 A it is first again, relieving 4 A.
        var charger = Charger() with { ValuePerKwh = 0.1, CurrentDrawPhases = [Phase.L1] };
        var idle = Candidate(charger, new SetpointApplied(new Amperes(16))) with { Draw = Amperes.Zero };

        var steps = ShedOrder.For(Phase.L1, [idle, Candidate(Relay("boiler", 0.35))]);

        Assert.Equal([("boiler", false), ("charger", true)], steps.Select(s => (s.Device.Value, s.Idle)));
        Assert.Equal(0, steps[1].Relief.Value);
        var drawing = ShedOrder.For(Phase.L1, [idle with { Draw = new Amperes(10) }, Candidate(Relay("boiler", 0.35))]);
        Assert.Equal([("charger", 4.0), ("boiler", 6.6)], drawing.Select(s => (s.Device.Value, s.Relief.Value)));
    }

    [Fact]
    public void Stop_actuator_lets_a_modulating_device_be_switched_off_after_reduction()
    {
        var limited = ImmutableDictionary<int, StageLimit>.Empty.Add(1, StageLimit.AtMinimum);

        var steps = ShedOrder.For(Phase.L1, [new ShedCandidate(Charger() with { HasStopActuator = true }, [new SetpointApplied(new Amperes(16))], false, limited)]);

        Assert.Equal([(ShedAction.SwitchOff, 6.0)], steps.Select(s => (s.Action, s.Relief.Value)));
    }

    [Fact]
    public void Storage_sheds_to_zero_charge_never_into_discharge()
    {
        // A battery charging at 10 A is reduced to zero charge (its floor), never switched off and
        // never pushed into discharge; one already discharging is no candidate at all (stopping a
        // discharge would raise the current).
        var battery = new DeviceSpec(
            DeviceName.Parse("battery"),
            DeviceType.Storage,
            [new StageSpec(1, [Phase.L1], new Amperes(16))],
            new ModulatingRange(new Amperes(-16), new Amperes(16), new Amperes(1)),
            1.5,
            null,
            TimeSpan.FromSeconds(20),
            false,
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(60))
        {
            Start = StartPolicy.RestoreOnly,
        };

        Assert.Equal([(ShedAction.ReduceToMinimum, 10.0)], ShedOrder.For(Phase.L1, [Candidate(battery, new SetpointApplied(new Amperes(10)))]).Select(s => (s.Action, s.Relief.Value)));
        Assert.Empty(ShedOrder.For(Phase.L1, [Candidate(battery, new SetpointApplied(new Amperes(-5)))]));
        Assert.False(battery.CanSwitchOff);
    }

    [Fact]
    public void Relay_stages_highest_stage_first()
    {
        var boiler = new DeviceSpec(
            DeviceName.Parse("boiler"),
            DeviceType.Stepped,
            [new StageSpec(1, [Phase.L1], new Amperes(6.6)), new StageSpec(2, [Phase.L2], new Amperes(6.6)), new StageSpec(3, [Phase.L1], new Amperes(6.6))],
            null,
            0.3,
            null,
            TimeSpan.FromSeconds(1),
            false,
            TimeSpan.FromMinutes(6),
            TimeSpan.FromSeconds(90))
        {
            Start = StartPolicy.RestoreOnly,
        };

        var steps = ShedOrder.For(Phase.L1, [Candidate(boiler, new RelayApplied(true), new RelayApplied(true), new RelayApplied(true))]);

        Assert.Equal([3, 1], steps.Select(s => s.Stage));
    }

    [Fact]
    public void Unknown_state_counts_as_on_at_full_rating()
    {
        // A stale "off" could be a heater that is on: protection assumes the worst.
        var steps = ShedOrder.For(Phase.L1, [Candidate(Relay("boiler", 0.35), (AppliedState?)null), Candidate(Charger(), (AppliedState?)null)]);

        Assert.Equal([("boiler", 6.6), ("charger", 14.0)], steps.Select(s => (s.Device.Value, s.Relief.Value)));
    }

    [Fact]
    public void Known_off_stage_is_no_step()
    {
        var steps = ShedOrder.For(Phase.L1, [Candidate(Relay("boiler", 0.35), new RelayApplied(false)), Candidate(Relay("pool", 0.8))]);

        Assert.Equal(["pool"], steps.Select(s => s.Device.Value));
    }

    private static DeviceSpec Relay(string name, double value, params Phase[] phases) => new(
        DeviceName.Parse(name),
        DeviceType.OnOff,
        [new StageSpec(1, phases.Length == 0 ? [Phase.L1] : [.. phases], new Amperes(6.6))],
        null,
        value,
        null,
        TimeSpan.FromSeconds(1),
        false,
        TimeSpan.FromMinutes(6),
        TimeSpan.FromSeconds(90))
    {
        Start = StartPolicy.RestoreOnly,
    };

    private static DeviceSpec Charger() => new(
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

    private static ShedCandidate Candidate(DeviceSpec spec, bool underMinOn = false) =>
        new(spec, [.. spec.Stages.Select(_ => (AppliedState?)new RelayApplied(true))], underMinOn, ImmutableDictionary<int, StageLimit>.Empty);

    private static ShedCandidate Candidate(DeviceSpec spec, params AppliedState?[] applied) =>
        new(spec, [.. applied], false, ImmutableDictionary<int, StageLimit>.Empty);
}
