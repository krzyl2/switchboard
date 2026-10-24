using Switchboard.Application.Decisions;
using Switchboard.Application.Guard;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Dispatch;

/// <summary>
/// CONN-03: a circuit group is a downstream breaker with its own per-phase limit and thermal budget,
/// modelled like the main breaker and enforced alongside it, by the guard (sheds only members, the
/// decision names the group) and by dispatch (the tighter envelope binds).
/// </summary>
public sealed class CircuitGroupTests
{
    private static readonly DateTimeOffset T0 = GuardScenario.Start;
    private static readonly TimeSpan Cycle = TimeSpan.FromSeconds(30);

    // A 16 A carport breaker on L1 behind which the charger and one heater sit; another, cheaper
    // heater on L1 outside the group.
    private const string Devices = """
          - id: carport_heater
            type: onoff
            actuator: switch.carport_heater
            phase: L1
            rated_current_a: 6.6
            rated_power_w: 1500
            value_per_kwh: 0.35
            circuit_group: carport
          - id: house_heater
            type: onoff
            actuator: switch.house_heater
            phase: L1
            rated_current_a: 6.6
            rated_power_w: 1500
            value_per_kwh: 0.10
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

    private const string SubMetered = """
        circuit_groups:
          - id: carport
            breaker_rating_a: 16
            characteristic: B
            phases: [L1]
            current: { L1: sensor.carport_current_l1 }
            current_max_age: 90s
        """;

    private const string Estimated = """
        circuit_groups:
          - id: carport
            breaker_rating_a: 16
            characteristic: B
            phases: [L1]
        """;

    [Fact]
    public void Group_over_its_tier_sheds_only_members_and_names_the_group()
    {
        // The main L1 carries 21 A (0.84 × 25: may add) while the carport sub-meter reads 21 A
        // (1.31 × 16: shed one). The group sheds its cheapest member (the carport heater), never the
        // cheaper house heater outside it, and the decision names the group.
        var s = Scenario(SubMetered);
        s.SampleGroup("carport", Phase.L1, 8);
        s.SampleAll(8, 3, 3);
        s.RunFor(TimeSpan.FromSeconds(5));
        s.SampleGroup("carport", Phase.L1, 21);

        var evaluation = s.Sample(Phase.L1, 21);

        var shed = Assert.Single(evaluation.Intents);
        Assert.Equal(("carport_heater", "carport", ReasonCode.GuardTierExceeded), (shed.Device.Value, shed.Group!.Value.Value, shed.Reason));
        var decision = Assert.Single(s.DecisionsFor("carport_heater"));
        Assert.Equal("carport", decision.Group!.Value.Value);
        Assert.Empty(s.DecisionsFor("house_heater"));
    }

    [Fact]
    public void Group_budget_is_independent_of_the_main_phase()
    {
        // 20 A on the 16 A group (1.25 × In) for a minute consumes the group's budget; the main L1
        // at 20 A (0.8 × 25) keeps its full budget.
        var s = Scenario(SubMetered);
        s.SampleGroup("carport", Phase.L1, 8);
        s.SampleAll(8, 3, 3);
        for (var i = 1; i <= 6; i++)
        {
            s.RunFor(TimeSpan.FromSeconds(10));
            s.SampleGroup("carport", Phase.L1, 20);
            s.Sample(Phase.L1, 20);
        }

        var group = s.Last.Status.Groups.Single();
        Assert.True(group.Phase.Budget < 1, $"group budget {group.Phase.Budget}");
        Assert.Equal(1, s.PhaseStatus(Phase.L1).Budget, 12);
        Assert.True(s.Engine.Export().GroupBudgets["carport".ToGroup()][Phase.L1].Budget < 1);
    }

    [Fact]
    public void Group_without_sub_meter_uses_worst_case_member_current()
    {
        // No sub-meter: the group current is the worst case of its members, the heater's 6.6 A
        // (reported on) plus the charger's 16 A setpoint (no draw signal): 22.6 A, 1.41 × 16, shed
        // one. The carport heater is shed without any group measurement.
        var s = Scenario(Estimated);
        s.SetState("ev_charger", new SetpointApplied(new Amperes(6)));
        s.SampleAll(8, 3, 3);
        Assert.Equal(12.6, s.Last.Status.Groups.Single().Phase.Current!.Value.Value, 9);
        Assert.Empty(s.Intents);
        s.RunFor(TimeSpan.FromSeconds(5));
        s.SetState("ev_charger", new SetpointApplied(new Amperes(16)));

        var evaluation = s.Sample(Phase.L1, 9);

        Assert.Equal(22.6, s.Last.Status.Groups.Single().Phase.Current!.Value.Value, 9);
        var shed = Assert.Single(evaluation.Intents);
        Assert.Equal(("carport_heater", "carport"), (shed.Device.Value, shed.Group!.Value.Value));
    }

    [Fact]
    public void Estimated_group_current_steps_band_timers_on_member_reports()
    {
        // A member report is a new sample of an estimated group: the charger reporting a lower
        // setpoint (10 A) brings the group to 16.6 A (1.04 × 16) at once, without a main poll.
        var s = Scenario(Estimated);
        s.SampleAll(8, 3, 3);
        s.RunFor(TimeSpan.FromSeconds(5));
        s.SetState("ev_charger", new SetpointApplied(new Amperes(10)));

        s.Evaluate();

        Assert.Equal(16.6, s.Last.Status.Groups.Single().Phase.Current!.Value.Value, 9);
        Assert.Equal(s.Now, s.Last.Status.Groups.Single().Phase.ReportedAt);
    }

    [Fact]
    public void Group_envelope_binds_when_tighter_than_main()
    {
        // The main L1 has 11 A to its reserve threshold, but a 7 A group breaker (reserve 0, may add
        // up to 6.3 A) cannot take a 6.6 A heater with the start hysteresis: the heater behind it
        // stays off while the cheaper one outside the group starts on the same phase.
        var devices = Devices[..Devices.IndexOf("  - id: ev_charger", StringComparison.Ordinal)];
        var s = DispatchScenario.Create(devices, ["carport_heater", "house_heater"], extra: Estimated.Replace("breaker_rating_a: 16", "breaker_rating_a: 7"));
        s.PollUntil(T0 + (8 * Cycle), 3, 3, 3);

        Assert.Empty(s.DecisionsFor("carport_heater"));
        Assert.Contains(s.DecisionsFor("house_heater"), d => d.Action == DecisionAction.SwitchOn);
        var group = s.Last.Envelope.For(new ConstraintKey("carport".ToGroup(), Phase.L1))!;
        Assert.Equal(7 * 0.9, group.Available!.Value.Value, 9);
        Assert.Equal(ReasonCode.HeadroomInsufficient, s.Dispatches[^1].Devices.Single(d => d.Device.Value == "carport_heater").Reason);
    }

    [Fact]
    public void Stale_sub_meter_holds_the_members_off()
    {
        // The group's sub-meter is stale: its members are held off (guard_stale_signal, the
        // decision names the group), the house heater outside it is untouched, the group budget holds.
        var s = Scenario(SubMetered);
        s.SampleGroup("carport", Phase.L1, 8);
        s.SampleAll(8, 3, 3);
        s.SampleGroup("carport", Phase.L1, null);

        s.Evaluate();

        Assert.Equal(["carport_heater", "ev_charger"], s.Last.Intents.Select(i => i.Device.Value).Order());
        Assert.All(s.Last.Intents, i => Assert.Equal((ReasonCode.GuardStaleSignal, "carport"), (i.Reason, i.Group!.Value.Value)));
        Assert.Contains(s.Events, e => e.Action == DecisionAction.SafeStateEntered && e.Scope == DecisionScope.Group);
        Assert.Equal(BudgetTrend.Held, s.Last.Status.Groups.Single().Phase.BudgetTrend);
    }

    private static GuardScenario Scenario(string groups) =>
        new(TestDevices.Document(Devices, extra: groups));
}

internal static class GroupNames
{
    public static Switchboard.Domain.Connection.GroupName ToGroup(this string name) => Switchboard.Domain.Connection.GroupName.Parse(name);
}
