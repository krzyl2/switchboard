using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.State;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Infrastructure.Persistence;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// What Stage 3 adds to the two files in /data. The decision log is the audit trail of the
/// maintainer's shadow week: a dispatch decision must come back with the headroom, hysteresis,
/// settling and verification it was made from, and 0.3.x lines (still within retention) must stay
/// readable. state.json carries the manual overrides across a restart (SAFE-04) and the circuit
/// group budgets (CONN-03), and is read as untrusted input like every other key.
/// </summary>
public sealed class DispatchPersistenceTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 24, 12, 0, 30, TimeSpan.Zero);

    private readonly TempDataDirectory _data = new();

    [Fact]
    public void Dispatch_decision_round_trips_with_every_input()
    {
        var decision = Start();

        var line = DecisionJson.Serialize(decision);
        var parsed = DecisionJson.TryParse(line)!;

        Assert.Contains("\"v\":2", line, StringComparison.Ordinal);
        Assert.Contains("\"origin\":\"dispatch\"", line, StringComparison.Ordinal);
        Assert.Equal((decision.Origin, decision.Group, decision.Target), (parsed.Origin, parsed.Group, parsed.Target));
        var expected = decision.Inputs.Dispatch!;
        var actual = parsed.Inputs.Dispatch!;
        Assert.Equal(expected.Headroom, actual.Headroom);
        Assert.Equal(expected.Contribution.OrderBy(c => c.Key), actual.Contribution.OrderBy(c => c.Key));
        Assert.Equal(expected.InFlight.OrderBy(c => c.Key.ToString(), StringComparer.Ordinal), actual.InFlight.OrderBy(c => c.Key.ToString(), StringComparer.Ordinal));
        Assert.Equal(
            expected with { Headroom = [], Contribution = ImmutableDictionary<Phase, Amperes>.Empty, InFlight = ImmutableDictionary<ConstraintKey, Amperes>.Empty },
            actual with { Headroom = [], Contribution = ImmutableDictionary<Phase, Amperes>.Empty, InFlight = ImmutableDictionary<ConstraintKey, Amperes>.Empty });
    }

    [Fact]
    public void Setpoint_target_and_a_guard_decision_without_target_round_trip()
    {
        var charger = new Decision("test-sp", At, DecisionScope.Device, DeviceName.Parse("ev_charger"), null, Phase.L1, DecisionAction.SetSetpoint, ReasonCode.HeadroomAvailable, null, OperatingMode.Shadow, false, ReasonCode.ModeShadow, DecisionInputs.None, DecisionOrigin.Dispatch, null, DeviceLevel.At(new Amperes(13)));
        var shed = TestDecisions.Shed(At);

        Assert.Equal(DeviceLevel.At(new Amperes(13)), DecisionJson.TryParse(DecisionJson.Serialize(charger))!.Target);
        var parsed = DecisionJson.TryParse(DecisionJson.Serialize(shed))!;
        Assert.Equal(((DecisionOrigin?)null, (DeviceLevel?)null, (GroupName?)null), (parsed.Origin, parsed.Target, parsed.Group));
        Assert.Equal(shed.Inputs, parsed.Inputs);
    }

    [Fact]
    public void Legacy_mode_note_line_still_parses()
    {
        // Written by 0.3.1 with the add-on option set to active; 30 days of retention keep it on disk.
        const string Line = """{"v":1,"codes":2,"id":"a1b2c3-000000001","at":"2026-10-23T18:00:00.0000000Z","scope":"system","device":null,"stage":null,"phase":null,"action":"mode_resolved","reason":"mode_shadow","overridden":null,"mode":"shadow","commanded":false,"refused_by":null,"inputs":{"mode":{"ceiling":"active","ui_choice":"shadow","note":"active_unavailable_before_stage3"}}}""";

        var parsed = DecisionJson.TryParse(Line)!;

        Assert.Equal(ModeNote.ActiveUnavailableBeforeStage3, parsed.Inputs.Mode!.Note);
        Assert.Equal((DecisionAction.ModeResolved, (DecisionOrigin?)null, (DeviceLevel?)null), (parsed.Action, parsed.Origin, parsed.Target));
    }

    [Fact]
    public void A_0_7_0_mode_line_with_ui_choice_still_reads()
    {
        // 0.7.0 wrote the 0.6.x UI choice next to the control switch; 0.7.1 no longer writes it, and a
        // line of the last 30 days that carries it must still read, with everything else intact.
        const string Line = """{"v":2,"codes":3,"id":"a1b2c3-000000003","at":"2026-10-25T10:00:00.0000000Z","scope":"system","device":null,"stage":null,"phase":null,"action":"mode_resolved","reason":"mode_shadow","overridden":null,"mode":"shadow","commanded":false,"refused_by":null,"origin":null,"group":null,"target":null,"inputs":{"mode":{"ceiling":"shadow","ui_choice":"shadow","note":"none","control_allowed":false,"configuration_valid":true,"state_confirmed":true,"missing":["option_shadow","control_off"],"source":"default"}}}""";

        var parsed = DecisionJson.TryParse(Line)!;

        Assert.Equal(DecisionAction.ModeResolved, parsed.Action);
        Assert.Equal((OperatingMode.Shadow, ModeNote.None, (bool?)false), (parsed.Inputs.Mode!.Ceiling, parsed.Inputs.Mode.Note, parsed.Inputs.Mode.ControlAllowed));
        Assert.Equal([ModeCondition.OptionShadow, ModeCondition.ControlOff], parsed.Inputs.Mode.Missing);
    }

    [Fact]
    public void Mode_decisions_write_no_ui_choice()
    {
        var decision = TestDecisions.Mode(new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero));

        var line = DecisionJson.Serialize(decision);

        Assert.DoesNotContain("ui_choice", line, StringComparison.Ordinal);
        Assert.Equal(decision.Inputs, DecisionJson.TryParse(line)!.Inputs);
    }

    // BAL-07: the balance a decision was made beside is in the log line, so the shadow week can be
    // read back hour by hour; lines written before it carry none and must still read.
    [Fact]
    public void The_balance_of_the_step_round_trips_with_unknown_values_kept_unknown()
    {
        var known = Start(new BalanceDecisionInputs(240.5, 3100, 1650, 410.25, 150, 300, 540, true));
        var unknown = Start(new BalanceDecisionInputs(null, null, 1650, null, null, null, 0, false));

        var line = DecisionJson.Serialize(known);

        Assert.Contains("\"balance\":{\"balance_wh\":240.5,\"free_w\":3100,\"load_w\":1650,\"projection_wh\":410.25,\"lo_wh\":150,\"hi_wh\":300,\"t_rem_s\":540,\"burning\":true}", line, StringComparison.Ordinal);
        Assert.Equal(known.Inputs.Dispatch!.Balance, DecisionJson.TryParse(line)!.Inputs.Dispatch!.Balance);
        Assert.Equal(unknown.Inputs.Dispatch!.Balance, DecisionJson.TryParse(DecisionJson.Serialize(unknown))!.Inputs.Dispatch!.Balance);
    }

    [Fact]
    public void A_line_written_before_the_balance_existed_still_reads_without_one()
    {
        var line = DecisionJson.Serialize(Start());

        Assert.DoesNotContain("\"balance\"", line, StringComparison.Ordinal);
        Assert.Null(DecisionJson.TryParse(line)!.Inputs.Dispatch!.Balance);
    }

    [Fact]
    public void A_balance_that_is_not_what_the_writer_writes_is_no_decision()
    {
        var line = DecisionJson.Serialize(Start(new BalanceDecisionInputs(1, 2, 3, 4, 5, 6, 7, false)));

        Assert.NotNull(DecisionJson.TryParse(line));
        Assert.Null(DecisionJson.TryParse(line.Replace("\"t_rem_s\":7", "\"t_rem_s\":\"soon\"", StringComparison.Ordinal)));
        Assert.Null(DecisionJson.TryParse(line.Replace("\"balance_wh\":1", "\"balance_wh\":\"1\"", StringComparison.Ordinal)));
    }

    [Fact]
    public void Legacy_guard_line_still_parses_and_cannot_carry_stage_3_fields()
    {
        // A 0.3.x shed line reads as before. Dispatch inputs exist only in format 2: a format 1 line
        // claiming them is not a decision (nobody wrote it), and format 2 needs origin, group and target.
        const string Shed = """{"v":1,"codes":2,"id":"a1b2c3-000000002","at":"2026-10-23T18:00:10.0000000Z","scope":"device","device":"heater_l1","stage":null,"phase":"L1","action":"switch_off","reason":"guard_tier_exceeded","overridden":null,"mode":"shadow","commanded":false,"refused_by":"mode_shadow","inputs":{"device":{"applied":"on","setpoint_a":null,"state_fresh":true,"state_invalid_reason":null,"shed_rank":1,"value_per_kwh":0.35,"shed_priority":null,"min_on_remaining_s":0,"min_off_remaining_s":0,"can_switch_off":true}}}""";
        var parsed = DecisionJson.TryParse(Shed)!;
        Assert.Equal((DecisionAction.SwitchOff, "heater_l1"), (parsed.Action, parsed.Device!.Value.Value));

        var current = DecisionJson.Serialize(Start());
        using var document = JsonDocument.Parse(current);
        var dispatchPart = ",\"dispatch\":" + document.RootElement.GetProperty("inputs").GetProperty("dispatch").GetRawText();
        Assert.Null(DecisionJson.TryParse(Shed.Replace("\"can_switch_off\":true}", "\"can_switch_off\":true}" + dispatchPart, StringComparison.Ordinal)));
        Assert.NotNull(DecisionJson.TryParse(Shed.Replace("\"v\":1", "\"v\":2", StringComparison.Ordinal).Replace("\"inputs\":{\"device\"", "\"origin\":null,\"group\":null,\"target\":null,\"inputs\":{\"device\"", StringComparison.Ordinal).Replace("\"can_switch_off\":true}", "\"can_switch_off\":true}" + dispatchPart, StringComparison.Ordinal)));
        Assert.Null(DecisionJson.TryParse(current.Replace("\"origin\":\"dispatch\",", string.Empty, StringComparison.Ordinal)));
        Assert.Null(DecisionJson.TryParse(current.Replace("\"verdict\":\"match\"", "\"verdict\":\"probably\"", StringComparison.Ordinal)));
        Assert.Null(DecisionJson.TryParse(current.Replace("\"relays\":[true]", "\"relays\":[]", StringComparison.Ordinal)));
    }

    [Fact]
    public void Overrides_and_group_budgets_survive_a_restart_in_schema_4()
    {
        var file = File();
        var state = WithStage3(RuntimeState.Fresh);

        file.Save(state);
        var load = file.Load();

        Assert.Equal(StateFileStatus.Loaded, load.Status);
        using var json = JsonDocument.Parse(System.IO.File.ReadAllText(file.FilePath));
        Assert.Equal(4, json.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal(state.Overrides.OrderBy(o => o.Key), load.State.Overrides.OrderBy(o => o.Key));
        var carport = load.State.GroupBudgets[GroupName.Parse("carport")];
        Assert.Equal(state.GroupBudgets[GroupName.Parse("carport")].OrderBy(b => b.Key), carport.OrderBy(b => b.Key));
        Assert.Equal(0.6, load.State.GuardMemory.GroupBudgets[GroupName.Parse("carport")][Phase.L1].Budget);
    }

    [Fact]
    public void The_file_is_always_schema_4_because_the_control_switch_must_survive()
    {
        // The control switch and the observation record live from schema 3 and the goal ledger only in schema 4, so the file is
        // always written as 4 (a rollback to 0.10.x sets it aside, as the schema 3 bump did for 0.6.x; the CHANGELOG says so).
        var file = File();
        file.Save(RuntimeState.Fresh with { KillSwitch = true });

        using var json = JsonDocument.Parse(System.IO.File.ReadAllText(file.FilePath));
        Assert.Equal(4, json.RootElement.GetProperty("schema_version").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("ui_mode", out _));
        Assert.True(file.Load().State.KillSwitch);
    }

    [Fact]
    public void An_auto_override_is_never_written_so_the_file_always_reads_back()
    {
        // auto is the absence of an override and is refused when read: writing one would make the
        // next start set the whole state aside (budgets full, kill switch off).
        var file = File();
        file.Save(RuntimeState.Fresh with
        {
            KillSwitch = true,
            Overrides = ImmutableDictionary<DeviceName, DeviceOverride>.Empty.Add(DeviceName.Parse("heater_l1"), new DeviceOverride(OverrideMode.Auto, At, OverrideSource.Api)),
        });

        var load = file.Load();

        Assert.Equal((StateFileStatus.Loaded, true), (load.Status, load.State.KillSwitch));
        Assert.Empty(load.State.Overrides);
    }

    [Fact]
    public void A_schema_1_file_of_0_3_x_is_read_with_no_override()
    {
        const string Version1 = """{"schema_version":1,"saved_at":"2026-10-24T11:00:00.0000000Z","clean_shutdown":true,"kill_switch":false,"kill_switch_changed_at":null,"ui_mode":"shadow","phases":{"L1":{"budget":0.9,"at":"2026-10-24T11:00:00.0000000Z"}},"devices":{},"mqtt":{"discovered":["sensor/l1_headroom"]}}""";
        var file = File();
        System.IO.File.WriteAllText(file.FilePath, Version1);

        var load = file.Load();

        Assert.Equal(StateFileStatus.Loaded, load.Status);
        Assert.Empty(load.State.Overrides);
        Assert.Empty(load.State.GroupBudgets);
        Assert.Equal(0.9, load.State.Budgets[Phase.L1].Budget);
    }

    [Theory]
    [InlineData("\"mode\":\"force_on\"", "\"mode\":\"auto\"")]
    [InlineData("\"mode\":\"force_on\"", "\"mode\":\"on\"")]
    [InlineData("\"source\":\"api\"", "\"source\":\"browser\"")]
    [InlineData("\"heater_l1\":{\"mode\"", "\"switch.heater_1\":{\"mode\"")]
    [InlineData("\"carport\":{", "\"Car Port\":{")]
    [InlineData("\"budget\":0.6", "\"budget\":1.6")]
    [InlineData("\"schema_version\":4", "\"schema_version\":5")]
    [InlineData(",\"group_budgets\":{", ",\"extra\":{},\"group_budgets\":{")]
    public void Untrusted_stage_3_content_is_refused_as_a_whole(string valid, string tampered)
    {
        // An edited override must never half-apply: a force_on from a tampered file would outrank
        // dispatch. Anything out of shape resets the whole state, as for the Stage 2 keys.
        var file = File();
        file.Save(WithStage3(RuntimeState.Fresh));
        var text = System.IO.File.ReadAllText(file.FilePath);
        Assert.Contains(valid, text, StringComparison.Ordinal);
        System.IO.File.WriteAllText(file.FilePath, text.Replace(valid, tampered, StringComparison.Ordinal));

        var load = file.Load();

        Assert.Equal(StateFileStatus.ResetCorrupt, load.Status);
        Assert.Empty(load.State.Overrides);
    }

    [Fact]
    public void A_schema_1_file_cannot_carry_overrides()
    {
        // The override keys belong to schema 2 and 3 only; a version-1 file with them is not a 0.3.x file.
        var file = File();
        file.Save(WithStage3(RuntimeState.Fresh));
        System.IO.File.WriteAllText(file.FilePath, System.IO.File.ReadAllText(file.FilePath).Replace("\"schema_version\":4", "\"schema_version\":1", StringComparison.Ordinal));

        Assert.Equal(StateFileStatus.ResetCorrupt, file.Load().Status);
    }

    [Fact]
    public async Task Override_survives_a_restart_through_state_json()
    {
        // SAFE-04: set from Home Assistant, written at once, and in force again before the first
        // step after a restart (as restored); the file is the real one, schema 2.
        var file = File();
        var holder = new GuardRunnerHarness(TestDevices.Document()).Holder;
        var clock = new Switchboard.Application.Signals.SignalClock(new SteppableTime(At));
        var overrides = new DeviceOverrides(new GuardInbox(), holder, clock, NullLogger<DeviceOverrides>.Instance);
        var coordinator = new RuntimeStateCoordinator(file, file.Load(), () => null, Microsoft.Extensions.Options.Options.Create(TestDecisions.Options()), clock.Time, NullLogger<RuntimeStateCoordinator>.Instance);
        coordinator.Attach(overrides);

        overrides.Set(DeviceName.Parse("heater_l2"), OverrideMode.ForceOff, OverrideSource.Mqtt);
        await coordinator.FlushAsync(CancellationToken.None);

        var reloaded = File().Load();
        Assert.Equal(new DeviceOverride(OverrideMode.ForceOff, At, OverrideSource.Mqtt), reloaded.State.Overrides[DeviceName.Parse("heater_l2")]);
        var restarted = new DeviceOverrides(new GuardInbox(), holder, clock, NullLogger<DeviceOverrides>.Instance);
        restarted.Restore(reloaded.State.Overrides);
        Assert.Equal((OverrideMode.ForceOff, OverrideSource.Restored), (restarted.Current.For(DeviceName.Parse("heater_l2")).Mode, restarted.Current.For(DeviceName.Parse("heater_l2")).Source!.Value));

        // Back to auto: the override is gone from the file.
        restarted.Set(DeviceName.Parse("heater_l2"), OverrideMode.Auto, OverrideSource.Api);
        var again = new RuntimeStateCoordinator(file, reloaded, () => null, Microsoft.Extensions.Options.Options.Create(TestDecisions.Options()), clock.Time, NullLogger<RuntimeStateCoordinator>.Instance);
        again.OnOverridesChanged(restarted.Current);
        await again.FlushAsync(CancellationToken.None);
        Assert.Empty(File().Load().State.Overrides);
        Assert.Contains("\"schema_version\":4", System.IO.File.ReadAllText(file.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void State_holds_no_dispatch_memory()
    {
        // SAFE-07: after a restart every device starts unset. A persisted target, deferral, window
        // or unresponsive flag would act on a world that may have changed while the add-on was down.
        var forbidden = new[] { "Target", "Deferred", "Window", "Settling", "Unresponsive", "Mismatch", "Lockout", "Intent" };

        Assert.DoesNotContain(typeof(RuntimeState).GetProperties(), p => forbidden.Any(f => p.Name.Contains(f, StringComparison.OrdinalIgnoreCase)));
        File().Save(WithStage3(RuntimeState.Fresh));
        using var json = JsonDocument.Parse(System.IO.File.ReadAllText(File().FilePath));
        Assert.Equal(["mode", "changed_at", "source"], json.RootElement.GetProperty("overrides").GetProperty("heater_l1").EnumerateObject().Select(p => p.Name));
    }

    public void Dispose() => _data.Dispose();

    private RuntimeStateFile File() => new(_data.Path, TestDecisions.Options(), NullLogger<RuntimeStateFile>.Instance);

    private static RuntimeState WithStage3(RuntimeState state) => state with
    {
        Overrides = ImmutableDictionary<DeviceName, DeviceOverride>.Empty
            .Add(DeviceName.Parse("heater_l1"), new DeviceOverride(OverrideMode.ForceOn, At, OverrideSource.Api))
            .Add(DeviceName.Parse("ev_charger"), new DeviceOverride(OverrideMode.ForceOff, null, OverrideSource.Restored)),
        GroupBudgets = ImmutableDictionary<GroupName, ImmutableDictionary<Phase, BudgetMemory>>.Empty
            .Add(GroupName.Parse("carport"), ImmutableDictionary<Phase, BudgetMemory>.Empty.Add(Phase.L1, new BudgetMemory(0.6, At))),
    };

    /// <summary>A dispatch start in shadow mode with every dispatch input filled.</summary>
    private static Decision Start(BalanceDecisionInputs? balance = null) => new(
        "test-start",
        At,
        DecisionScope.Device,
        DeviceName.Parse("heater_l1"),
        null,
        Phase.L1,
        DecisionAction.SwitchOn,
        ReasonCode.HeadroomAvailable,
        null,
        OperatingMode.Shadow,
        false,
        ReasonCode.ModeShadow,
        DecisionInputs.None with
        {
            Dispatch = new DispatchDecisionInputs(
                [new ConstraintHeadroom(ConstraintKey.Main(Phase.L1), new Amperes(9.1), new Amperes(8.5)), new ConstraintHeadroom(new ConstraintKey(GroupName.Parse("carport"), Phase.L1), new Amperes(10), null)],
                new Amperes(1.3),
                new Amperes(-0.87),
                3,
                ImmutableDictionary<Phase, Amperes>.Empty.Add(Phase.L1, new Amperes(0)),
                ImmutableDictionary<ConstraintKey, Amperes>.Empty.Add(ConstraintKey.Main(Phase.L1), new Amperes(6.6)).Add(new ConstraintKey(GroupName.Parse("carport"), Phase.L1), new Amperes(6.6)),
                SettlingState.Closed,
                new VerificationInputs(1500, 1460, MeasureUnit.Watts, Verdict.Match),
                TimeSpan.FromMinutes(6),
                TimeSpan.Zero,
                At - TimeSpan.FromSeconds(30),
                OverrideMode.Auto)
            {
                Balance = balance,
            },
        },
        DecisionOrigin.Dispatch,
        GroupName.Parse("carport"),
        DeviceLevel.Relay(true));
}
