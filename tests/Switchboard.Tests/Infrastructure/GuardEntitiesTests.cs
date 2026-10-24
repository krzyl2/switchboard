using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application;
using Switchboard.Application.Decisions;
using Switchboard.Application.Guard;
using Switchboard.Application.Signals;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Infrastructure.Mqtt;
using Switchboard.Tests.Support;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// The guard's MQTT entities (BRK-04, SAFE-03, PKG-03). Their identity follows the scheme the
/// maintainer froze as an API; their values are what a user's automation acts on, so headroom is
/// measured (never the shadow estimate) and an unknown value is published as unknown.
/// </summary>
public sealed class GuardEntitiesTests
{
    private readonly GuardScenario _scenario = new(TestDevices.Document());
    private readonly RecordingDecisionRecorder _decisions = new();
    private readonly MemoryLedger _ledger = new();
    private readonly GuardStatusHolder _holder;
    private readonly KillSwitchState _killSwitch;
    private Volts? _voltage = new Volts(230);

    public GuardEntitiesTests()
    {
        _holder = new GuardStatusHolder(GuardStatus.Initial(_scenario.Now, _scenario.Mode), NullLogger<GuardStatusHolder>.Instance);
        _killSwitch = new KillSwitchState(new GuardInbox(), new SignalClock(_scenario.Time), NullLogger<KillSwitchState>.Instance);
    }

    [Fact]
    public void Every_entity_follows_the_frozen_identity()
    {
        var guard = new MqttTopicGuard("homeassistant", "switchboard");
        Publish(_scenario.Evaluate());

        var entities = Entities().Current().Entities;

        Assert.Equal(3 + (3 * 6) + 4, entities.Length);
        foreach (var entity in entities)
        {
            using var config = JsonDocument.Parse(entity.Config);
            var root = config.RootElement;
            Assert.Equal($"switchboard_{entity.ObjectId}", root.GetProperty("unique_id").GetString());
            Assert.Equal($"{entity.Component}.switchboard_{entity.ObjectId}", root.GetProperty("default_entity_id").GetString());
            Assert.Equal($"switchboard/{entity.ObjectId}/state", root.GetProperty("state_topic").GetString());
            Assert.Equal("switchboard/availability", root.GetProperty("availability_topic").GetString());
            Assert.Equal(["switchboard"], root.GetProperty("device").GetProperty("identifiers").EnumerateArray().Select(e => e.GetString()));
            Assert.True(root.GetProperty("has_entity_name").GetBoolean());
            Assert.True(guard.IsAllowed($"homeassistant/{entity.Component}/switchboard/{entity.ObjectId}/config"), entity.ObjectId);
            Assert.True(guard.IsAllowed($"switchboard/{entity.ObjectId}/state"), entity.ObjectId);
        }

        Assert.Contains(entities, e => e is { Component: "sensor", ObjectId: "l1_headroom" });
        Assert.Contains(entities, e => e is { Component: "sensor", ObjectId: "l3_overload_budget" });
        Assert.Contains(entities, e => e is { Component: "switch", ObjectId: "kill_switch" });
        Assert.Contains(entities, e => e is { Component: "sensor", ObjectId: "device_ev_charger_state" });
    }

    [Fact]
    public void Budget_and_recovery_remaining_are_entities()
    {
        // BRK-04: 1.32 × In for 18 s (from the previous report) leaves 0.4 of the budget, and a
        // full refill then takes 0.6 × 600 s.
        _scenario.Evaluate();
        _scenario.Time.Advance(TimeSpan.FromSeconds(10));
        _scenario.SampleMultiple(Phase.L1, 1.32);
        _scenario.Time.Advance(TimeSpan.FromSeconds(8));
        Publish(_scenario.Evaluate(GuardTrigger.Tick));

        var states = States();

        Assert.Equal("40.0", states["l1_overload_budget"]);
        Assert.Equal("360", states["l1_recovery_remaining"]);
        Assert.Equal("100.0", states["l2_overload_budget"]);
        Assert.Equal("0", states["l2_recovery_remaining"]);
        var config = JsonDocument.Parse(Entities().Current().Entities.Single(e => e.ObjectId == "l1_recovery_remaining").Config).RootElement;
        Assert.Equal(("s", "duration"), (config.GetProperty("unit_of_measurement").GetString(), config.GetProperty("device_class").GetString()));
    }

    [Fact]
    public void Shadow_published_headroom_is_measured_not_hypothetical()
    {
        // The heater is held off in shadow mode but nothing switched it: the phase still carries
        // 33 A. Headroom computed from "as if applied" would tell an automation there is room.
        _scenario.Evaluate();
        _scenario.Time.Advance(TimeSpan.FromSeconds(10));
        Publish(_scenario.Sample(Phase.L1, 33));
        var phase = _scenario.PhaseStatus(Phase.L1);
        Assert.NotEqual(phase.Current, phase.EffectiveCurrent);

        var states = States();

        var threshold = 25 - (2500.0 / 230);
        Assert.Equal((threshold - 33).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), states["l1_headroom"]);
        Assert.Equal(((threshold - 33) * 230).ToString("0", System.Globalization.CultureInfo.InvariantCulture), states["l1_headroom_power"]);
        Assert.Equal((28.25 - 33).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), states["l1_breaker_headroom"]);
    }

    [Fact]
    public void Unknown_values_publish_None()
    {
        // Home Assistant shows "None" as unknown; a stale number would look like a real reading.
        _scenario.Invalidate(Phase.L2, ProtectionInvalidReason.CurrentStale);
        Publish(_scenario.Last);
        _voltage = null;

        var states = States();

        Assert.Equal("None", states["l2_headroom"]);
        Assert.Equal("None", states["l2_breaker_headroom"]);
        Assert.Equal("None", states["l1_headroom_power"]);
        Assert.Equal("safe_state", states["l2_guard_state"]);
        Assert.Equal("unsafe", states["device_heater_l2_state"]);
        Assert.Equal("None", states["last_decision"]);
    }

    [Fact]
    public void Phase_entities_only_for_topology_phases()
    {
        var single = new GuardScenario(SinglePhase);
        _holder.Publish(single.Evaluate().Status);

        var objects = Entities().Current().Entities.Select(e => e.ObjectId).ToList();

        Assert.Contains("l1_headroom", objects);
        Assert.DoesNotContain(objects, o => o.StartsWith("l2_", StringComparison.Ordinal) || o.StartsWith("l3_", StringComparison.Ordinal));
    }

    [Fact]
    public void Mode_last_decision_and_device_states_carry_their_reasons()
    {
        _scenario.KillSwitch = new KillSwitchStatus(true, _scenario.Now, KillSwitchSource.Api);
        var evaluation = _scenario.Evaluate(GuardTrigger.KillSwitch);
        _decisions.Record(_scenario.Decisions);
        Publish(evaluation);

        var entities = Entities().Current().Entities.ToDictionary(e => e.ObjectId);

        Assert.Equal("ON", entities["kill_switch"].State);
        Assert.Equal("shadow", entities["mode"].State);
        Assert.Equal("shed", entities["device_heater_l1_state"].State);
        Assert.Equal("kill_switch", JsonDocument.Parse(entities["device_heater_l1_state"].Attributes!).RootElement.GetProperty("reason").GetString());
        Assert.False(JsonDocument.Parse(entities["device_ev_charger_state"].Attributes!).RootElement.GetProperty("can_switch_off").GetBoolean());
        Assert.Equal("kill_switch", entities["last_decision"].State);
    }

    [Fact]
    public void Kill_switch_release_is_distinguishable()
    {
        // The release keeps the reason of the condition it ends (kill_switch), so as a reason code
        // the last-decision state would read exactly like engaging it; an automation watching it
        // could not tell the add-on may act again. The state names the action instead.
        _scenario.SampleAll(8, 8, 8);
        _scenario.KillSwitch = new KillSwitchStatus(true, _scenario.Now, KillSwitchSource.Api);
        _scenario.Evaluate(GuardTrigger.KillSwitch);
        _decisions.Record(_scenario.Decisions);
        Assert.Equal("kill_switch", States()["last_decision"]);

        _scenario.Time.Advance(TimeSpan.FromSeconds(10));
        _scenario.KillSwitch = new KillSwitchStatus(false, _scenario.Now, KillSwitchSource.Api);
        var recorded = _scenario.Decisions.Count;
        _scenario.Evaluate(GuardTrigger.KillSwitch);
        _decisions.Record(_scenario.Decisions.Skip(recorded).ToList());

        var last = Entities().Current().Entities.Single(e => e.ObjectId == "last_decision");
        Assert.Equal("kill_switch_disengaged", last.State);
        using var attributes = JsonDocument.Parse(last.Attributes!);
        Assert.Equal(("kill_switch_disengaged", "kill_switch"), (attributes.RootElement.GetProperty("action").GetString(), attributes.RootElement.GetProperty("reason").GetString()));
    }

    [Fact]
    public void Last_decision_options_are_reason_codes_or_condition_ending_actions_verbatim()
    {
        // The state is a reason code of list version 2, except for the two decisions that end a
        // condition, which use their action's own name: never an invented value.
        var reasons = ReasonCodes.All.Select(ReasonCodes.Name).ToHashSet(StringComparer.Ordinal);
        var actions = Enum.GetValues<DecisionAction>().Select(a => JsonNamingPolicy.SnakeCaseLower.ConvertName(a.ToString())).ToHashSet(StringComparer.Ordinal);
        var extra = GuardEntities.LastDecisionStates.Where(o => !reasons.Contains(o)).ToList();

        Assert.Equal(["kill_switch_disengaged", "safe_state_exited"], extra);
        Assert.All(extra, o => Assert.Contains(o, actions));
        Assert.True(reasons.IsSubsetOf(GuardEntities.LastDecisionStates));
    }

    [Fact]
    public void Without_a_valid_configuration_earlier_entities_stay_unknown()
    {
        // No configuration known in this process (a broken file at start): the entities announced
        // before (ledger) stay, unknown, and the set is not authoritative, so nothing is retracted.
        _ledger.Replace(["sensor/l2_headroom", "sensor/device_heater_l2_state", "switch/kill_switch"]);

        var set = Entities().Current();

        Assert.False(set.Authoritative);
        Assert.Equal("None", set.Entities.Single(e => e.ObjectId == "device_heater_l2_state").State);
        Assert.Equal("None", set.Entities.Single(e => e.ObjectId == "l2_guard_state").State);
        Assert.DoesNotContain(set.Entities, e => e.ObjectId.StartsWith("l1_", StringComparison.Ordinal));
    }

    [Fact]
    public void Kill_switch_commands_are_exactly_ON_and_OFF()
    {
        var entities = Entities();

        entities.HandleCommand("switchboard/kill_switch/set", "on");
        Assert.False(_killSwitch.Status.Engaged);
        entities.HandleCommand("switchboard/kill_switch/set", "ON");
        Assert.Equal((true, KillSwitchSource.Mqtt), (_killSwitch.Status.Engaged, _killSwitch.Status.Source!.Value));
        entities.HandleCommand("switchboard/kill_switch/set", "OFF");
        Assert.False(_killSwitch.Status.Engaged);
    }

    private const string SinglePhase = """
        schema_version: 1
        connection:
          topology: single_phase
          breaker_rating_a: 32
          characteristic: B
          voltage_v: 230
        signals:
          grid_current:
            entity: sensor.meter_current
            kind: current
            unit: A
            sign: positive_import
            max_age: 90s
            role: phase_current
            phase: L1
        devices:
          - id: heater
            type: onoff
            actuator: switch.heater
            phase: L1
            rated_current_a: 6.6
            rated_power_w: 1500
            value_per_kwh: 0.35
        """;

    private GuardEntities Entities() => new(
        _holder,
        _decisions,
        _killSwitch,
        _ledger,
        () => _voltage,
        MsOptions.Create(TestOptions.Mqtt()),
        new AppVersion("0.3.0"),
        NullLogger<GuardEntities>.Instance);

    private void Publish(GuardEvaluation evaluation) => _holder.Publish(evaluation.Status);

    private Dictionary<string, string> States() => Entities().Current().Entities.ToDictionary(e => e.ObjectId, e => e.State);
}
