using Switchboard.Application.Configuration;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Planning;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Host.Composition;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// What the planner is given about the installation (15-PLANNER, "How the plan meets the gates"): the guard's static allocation limit per phase and circuit group,
/// and each device in watts with the share of its power on each constraint. Why it matters: the plan's capacity is the one place the planner meets the guard's
/// numbers, and it must be exactly the thresholds the guard itself uses (rating less reserve, capped at the top of the may-add band) converted at the nominal
/// voltage; a limit too high would let the plan ask for more than the envelope ever allows, one too low would waste the installation.
/// </summary>
public sealed class DiagnosticsPlannerSourceTests
{
    private static readonly Volts Voltage = new(230);

    private static ConfigurationResult Config(string devices, string extra = "", string control = "", string characteristic = "B") =>
        TestConfigurations.Valid(TestDevices.Document(devices, control, characteristic, extra));

    private static ProtectionSetup Setup(ConfigurationResult config) => ProtectionSetupFactory.Build(config.Configuration!);

    [Fact]
    public void The_static_limit_of_a_phase_is_the_lower_of_the_breaker_less_the_reserve_and_the_top_of_the_may_add_band()
    {
        // Why: the same two numbers the guard's envelope starts from (E = min(In - reserve, may_add top x In) - I_est), without the live term. The test recomputes them
        // from the setup in amperes and checks the conversion at 230 V; the worked example of the design (25 A, 2.5 kW reserve) is 3.25 kW.
        var setup = Setup(Config(TestDevices.Reference));

        var limits = DiagnosticsPlannerSource.Capacity(setup, Voltage);

        foreach (var phase in setup.Phases)
        {
            var amperes = Math.Min(setup.ReserveThreshold(phase).Value, setup.Breaker.Rating.Value * setup.Breaker.MayAddUpTo);
            Assert.Equal(amperes * 230, limits[ConstraintKey.Main(phase)], 6);
        }

        Assert.Equal(setup.Phases.Length, limits.Count);
        Assert.All(limits.Values, w => Assert.InRange(w, 1, setup.Breaker.Rating.Value * 230));
    }

    [Fact]
    public void A_circuit_group_has_its_own_limit_on_its_own_phases()
    {
        // Why: a device behind a downstream breaker is limited by it as well; the group's key is (group, phase), as dispatch's ConstraintKey, with the group's rating
        // and reserve, so a 10 A carport breaker constrains the plan below the 25 A main one.
        const string Groups = """
            circuit_groups:
              - id: carport
                breaker_rating_a: 10
                characteristic: B
                phases: [L1]
            """;
        const string Devices = """
              - id: ev_charger
                type: modulating
                actuator: number.charger_current
                phase: L1
                min: 6
                max: 16
                step: 1
                value_per_kwh: 2.03
                circuit_group: carport
            """;
        var setup = Setup(Config(Devices, Groups));

        var limits = DiagnosticsPlannerSource.Capacity(setup, Voltage);

        var key = new ConstraintKey(GroupName.Parse("carport"), Phase.L1);
        Assert.True(limits.ContainsKey(key));
        Assert.True(limits[key] < limits[ConstraintKey.Main(Phase.L1)], $"group {limits[key]} W, main {limits[ConstraintKey.Main(Phase.L1)]} W");
        Assert.Equal(setup.Groups.Single().Phases.Length + setup.Phases.Length, limits.Count);
    }

    [Fact]
    public void Devices_are_given_in_watts_with_their_phase_shares_and_start_policy()
    {
        // Why: the planner's unit is watts (the conversion happens here, once, at the nominal voltage). The reference charger is 6 to 20 A on one phase: 1.38 to
        // 4.6 kW with the whole of its power on L1; a heater is one 6.6 A relay (1.518 kW) on its phase. start: headroom (the test document's default) lets the
        // planner start them; the value model travels along for the soft-goal test.
        var configuration = Config(TestDevices.Reference).Configuration!;
        var setup = ProtectionSetupFactory.Build(configuration);

        var devices = DiagnosticsPlannerSource.Devices(setup, configuration, Voltage);

        var charger = devices.Single(d => d.Id == DeviceName.Parse("ev_charger"));
        Assert.Equal(PlanDeviceKind.Modulating, charger.Kind);
        Assert.Equal(6 * 230, charger.MinW, 6);
        Assert.Equal(20 * 230, charger.MaxW, 6);
        Assert.Equal([new PlanKeyShare(ConstraintKey.Main(Phase.L1), 1)], charger.Keys.ToArray());
        Assert.True(charger.CanStart);
        Assert.Equal(2.03, charger.Value.Static);
        var heater = devices.Single(d => d.Id == DeviceName.Parse("heater_l2"));
        Assert.Equal(PlanDeviceKind.Relay, heater.Kind);
        Assert.Equal(6.6 * 230, heater.MaxW, 6);
        Assert.Equal(heater.MinW, heater.MaxW, 6);
        Assert.Equal([new PlanKeyShare(ConstraintKey.Main(Phase.L2), 1)], heater.Keys.ToArray());
        Assert.Equal(["ev_charger", "heater_l1", "heater_l2", "heater_l3"], devices.Select(d => d.Id.Value).ToArray());
    }

    [Fact]
    public void A_restore_only_device_cannot_be_started_by_the_plan()
    {
        // Why: start: restore_only (the shipped default) means dispatch never starts the device; the plan must know, because a run entry for it would be a want
        // nobody can act on and a goal on it would otherwise reserve capacity for nothing.
        var configuration = TestConfigurations.Valid(TestDevices.Document(defaultStart: "restore_only")).Configuration!;

        var devices = DiagnosticsPlannerSource.Devices(ProtectionSetupFactory.Build(configuration), configuration, Voltage);

        Assert.All(devices, d => Assert.False(d.CanStart));
    }

    [Fact]
    public void A_spec_identity_changes_with_the_device_s_physical_model_and_not_with_anything_else()
    {
        // Why: an entry made for another spec must not be applied (slice 5 compares it); a heater's identity moves when its rated current does, and stays when only
        // its value per kWh does (a price is not a spec).
        var a = Setup(Config(TestDevices.Reference)).Devices.First(d => d.Id == DeviceName.Parse("heater_l1"));
        var b = Setup(Config(TestDevices.Reference.Replace("rated_current_a: 6.6", "rated_current_a: 7.6", StringComparison.Ordinal))).Devices.First(d => d.Id == DeviceName.Parse("heater_l1"));
        var c = Setup(Config(TestDevices.Reference.Replace("value_per_kwh: 0.35", "value_per_kwh: 0.40", StringComparison.Ordinal))).Devices.First(d => d.Id == DeviceName.Parse("heater_l1"));

        Assert.NotEqual(DiagnosticsPlannerSource.Identity(a), DiagnosticsPlannerSource.Identity(b));
        Assert.Equal(DiagnosticsPlannerSource.Identity(a), DiagnosticsPlannerSource.Identity(c));
    }
}
