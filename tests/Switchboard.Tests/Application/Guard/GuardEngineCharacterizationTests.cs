using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text;
using Switchboard.Application.Guard;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Guard;

/// <summary>
/// Pins what the guard engine returns, evaluation by evaluation, before and after it is split into
/// smaller parts (UPGRADE.md, GuardEngine): the whole <see cref="GuardEvaluation"/> (status, intents with
/// their inputs, events, envelope, limits, cycle) plus the next deadline and the exported memory at
/// checkpoints, for tier reactions, the shed order, release persistence, safe states, the kill switch,
/// the budget and its clock step, exporting phases, circuit groups, stepped and setpoint devices,
/// in-flight changes of the load model, a redeclared device and a restore. The traces under Golden/
/// were recorded on the unsplit engine and are never edited to make a refactoring pass; a deliberate
/// change of behaviour re-records them with SWITCHBOARD_CAPTURE=1, in a commit that says so.
/// </summary>
public sealed class GuardEngineCharacterizationTests
{
    private static readonly DateTimeOffset T0 = GuardScenario.Start;
    private const string OneBoundary = "  dispatch:\n    persistence_up_cycles: 1\n";

    private const string GroupDevices = """
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
          - id: boiler
            type: stepped
            value_per_kwh: 0.3
            stages:
              - { actuator: switch.boiler_1, phase: L2, rated_current_a: 3, rated_power_w: 690 }
              - { actuator: switch.boiler_2, phase: L2, rated_current_a: 3, rated_power_w: 690 }
              - { actuator: switch.boiler_3, phase: L3, rated_current_a: 3, rated_power_w: 690 }
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
    public void Tier_ladder_sheds_in_order_and_releases_after_persistence_with_dispatch_in_flight()
    {
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(DispatchDevices.HeaterAndCharger, [], OneBoundary, mode: DispatchScenario.Active, port: port);
        s.Follow(port);
        var t = new Trace();
        s.PollUntil(T0 + TimeSpan.FromMinutes(2), 8, 3, 3);
        t.Mark("quiet", s);
        s.PollUntil(T0 + TimeSpan.FromMinutes(4), 28, 3, 3);
        t.Mark("1.12 In", s);
        s.PollUntil(T0 + TimeSpan.FromMinutes(5), 33, 3, 3);
        t.Mark("1.32 In", s);
        s.PollUntil(T0 + TimeSpan.FromMinutes(7), 45, 3, 3);
        t.Mark("1.8 In", s);
        s.PollUntil(T0 + TimeSpan.FromMinutes(9), 14, 3, 3);
        t.Mark("inside the reserve", s);
        s.PollUntil(T0 + TimeSpan.FromMinutes(16), 3, 3, 3);
        t.Mark("released", s);
        Compare(nameof(Tier_ladder_sheds_in_order_and_releases_after_persistence_with_dispatch_in_flight), t.Finish(s));
    }

    [Fact]
    public void Three_phases_reserve_request_and_clearing_with_the_ordinary_tier_streak()
    {
        var s = new GuardScenario(TestDevices.Document());
        var t = new Trace();
        s.Poll(TimeSpan.FromSeconds(10), T0 + TimeSpan.FromMinutes(2), p => p == Phase.L1 ? 15 : 8);
        t.Mark("reserve over", s);
        s.Poll(TimeSpan.FromSeconds(10), T0 + TimeSpan.FromMinutes(5), p => p == Phase.L1 ? 26 : p == Phase.L2 ? 23.5 : 8);
        t.Mark("ordinary tier", s);
        s.Poll(TimeSpan.FromSeconds(10), T0 + TimeSpan.FromMinutes(9), _ => 8);
        t.Mark("clearing", s);
        s.Poll(TimeSpan.FromSeconds(10), T0 + TimeSpan.FromMinutes(14), _ => 8);
        t.Mark("released", s);
        Compare(nameof(Three_phases_reserve_request_and_clearing_with_the_ordinary_tier_streak), t.Finish(s));
    }

    [Fact]
    public void Safe_states_the_kill_switch_stale_device_state_and_shutdown_hold_and_never_release_by_themselves()
    {
        var s = new GuardScenario(TestDevices.Document());
        var t = new Trace();
        s.SampleAll(8, 8, 8);
        s.Invalidate(Phase.L2, ProtectionInvalidReason.CurrentStale);
        t.Mark("L2 stale", s);
        s.RunFor(TimeSpan.FromSeconds(40));
        s.Sample(Phase.L2, 8);
        t.Mark("L2 back", s);
        s.RunUntil(T0 + TimeSpan.FromMinutes(4));
        s.SampleAll(8, 8, 8);
        t.Mark("persistence", s);
        s.Invalidate(Phase.L1, ProtectionInvalidReason.CurrentAwaiting, evaluate: false);
        s.Invalidate(Phase.L2, ProtectionInvalidReason.SourceDisconnected, evaluate: false);
        s.Invalidate(Phase.L3, ProtectionInvalidReason.CurrentStale);
        t.Mark("nothing valid", s);
        s.RunFor(TimeSpan.FromSeconds(50));
        s.SampleAll(8, 8, 8);
        s.RunFor(TimeSpan.FromMinutes(3));
        s.SampleAll(8, 8, 8);
        t.Mark("meter back", s);
        s.KillSwitch = new KillSwitchStatus(true, s.Now, KillSwitchSource.Api);
        s.Evaluate();
        s.RunFor(TimeSpan.FromSeconds(31));
        s.SampleAll(8, 8, 8);
        s.KillSwitch = new KillSwitchStatus(false, s.Now, KillSwitchSource.Api);
        s.Evaluate();
        t.Mark("kill switch", s);
        s.RunFor(TimeSpan.FromMinutes(4));
        s.SampleAll(8, 8, 8);
        s.SetState("heater_l3", null, invalid: DeviceStateInvalidReason.Stale);
        s.Evaluate();
        t.Mark("device state stale", s);
        s.ConfigurationValid(false);
        s.Evaluate();
        s.ConfigurationValid(true);
        s.SampleAll(8, 8, 8);
        t.Mark("configuration", s);
        s.Evaluate(GuardTrigger.Shutdown);
        s.RunFor(TimeSpan.FromMinutes(1));
        s.Evaluate();
        t.Mark("shutdown", s);
        Compare(nameof(Safe_states_the_kill_switch_stale_device_state_and_shutdown_hold_and_never_release_by_themselves), t.Finish(s));
    }

    [Fact]
    public void No_setup_in_this_process_records_the_cause_and_holds_nothing()
    {
        var s = new GuardScenario(TestDevices.Document());
        var t = new Trace();
        s.ConfigurationValid(false, SetupAbsence.NotRead);
        s.Evaluate();
        s.ConfigurationValid(false, SetupAbsence.Missing);
        s.Evaluate();
        t.Mark("no setup", s);
        Compare(nameof(No_setup_in_this_process_records_the_cause_and_holds_nothing), t.Finish(s));
    }

    [Fact]
    public void Budget_drains_stops_increases_recovers_and_holds_across_a_clock_step_back()
    {
        var s = new GuardScenario(TestDevices.Document());
        var t = new Trace();
        s.Sample(Phase.L1, 8);
        s.Hold(Phase.L1, 22, TimeSpan.FromSeconds(10), T0 + TimeSpan.FromSeconds(100));
        t.Mark("consuming", s);
        s.Hold(Phase.L1, 26, TimeSpan.FromSeconds(10), T0 + TimeSpan.FromMinutes(5));
        t.Mark("low", s);
        s.Hold(Phase.L1, 29, TimeSpan.FromSeconds(10), T0 + TimeSpan.FromMinutes(8));
        t.Mark("exhausted", s);
        s.Time.StepBack(TimeSpan.FromHours(1));
        s.Evaluate();
        t.Mark("step back", s);
        s.Time.Advance(TimeSpan.FromSeconds(30));
        s.Evaluate();
        s.Hold(Phase.L1, 8, TimeSpan.FromSeconds(10), s.Now + TimeSpan.FromMinutes(12));
        t.Mark("recovering", s);
        Compare(nameof(Budget_drains_stops_increases_recovers_and_holds_across_a_clock_step_back), t.Finish(s));
    }

    [Fact]
    public void An_exporting_overload_is_recorded_once_and_sheds_nothing()
    {
        var s = new GuardScenario(TestDevices.Document());
        var t = new Trace();
        s.Sample(Phase.L1, 8);
        s.Hold(Phase.L1, 33, TimeSpan.FromSeconds(10), T0 + TimeSpan.FromMinutes(2), FlowDirection.Export);
        t.Mark("exporting", s);
        s.Hold(Phase.L1, 33, TimeSpan.FromSeconds(10), T0 + TimeSpan.FromMinutes(3), FlowDirection.Import);
        t.Mark("confirmed import", s);
        Compare(nameof(An_exporting_overload_is_recorded_once_and_sheds_nothing), t.Finish(s));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Circuit_groups_shed_members_only_with_their_own_budget(bool subMetered)
    {
        var port = new RecordingDeviceCommandPort();
        var s = new GuardScenario(
            TestDevices.Document(GroupDevices, OneBoundary, extra: subMetered ? SubMetered : Estimated),
            settledLockouts: false,
            dispatch: true,
            port: port,
            mode: DispatchScenario.Active);
        s.Follow(port);
        var t = new Trace();
        if (subMetered)
        {
            s.SampleGroup("carport", Phase.L1, 8);
        }

        s.SampleAll(8, 3, 3);
        s.RunFor(TimeSpan.FromSeconds(40));
        s.SetState("ev_charger", new SetpointApplied(new Amperes(6)));
        s.SetState("boiler", new RelayApplied(false), stage: 1);
        s.SetState("boiler", new RelayApplied(false), stage: 2);
        s.SetState("boiler", new RelayApplied(false), stage: 3);
        for (var i = 0; i < 8; i++)
        {
            s.RunFor(TimeSpan.FromSeconds(10));
            if (subMetered)
            {
                s.SampleGroup("carport", Phase.L1, 21);
            }

            s.SampleAll(i < 4 ? 21 : 9, 3, 3);
        }

        t.Mark("group over", s);
        s.SetState("ev_charger", new SetpointApplied(new Amperes(16)));
        if (subMetered)
        {
            s.SampleGroup("carport", Phase.L1, null);
        }

        s.SampleAll(9, 3, 3);
        t.Mark("group meter lost or charger at maximum", s);
        for (var i = 0; i < 40; i++)
        {
            s.RunFor(TimeSpan.FromSeconds(10));
            if (subMetered)
            {
                s.SampleGroup("carport", Phase.L1, 5);
            }

            s.SampleAll(5, 3, 3);
        }

        t.Mark("quiet", s);
        Compare(nameof(Circuit_groups_shed_members_only_with_their_own_budget) + (subMetered ? "_sub_metered" : "_estimated"), t.Finish(s));
    }

    [Fact]
    public void A_stage_held_for_a_device_the_reload_no_longer_declares_is_dropped_and_a_restore_earns_recovery()
    {
        var s = new GuardScenario(TestDevices.Document(), settledLockouts: false);
        s.Engine.Restore(
            new GuardMemory(
                new[] { Phase.L1, Phase.L2, Phase.L3 }.ToImmutableDictionary(p => p, _ => new BudgetMemory(0.4, T0 - TimeSpan.FromMinutes(5))),
                CleanShutdown: true),
            s.Now);
        var t = new Trace();
        s.SampleAll(8, 8, 8);
        t.Mark("restored", s);
        s.Invalidate(Phase.L3, ProtectionInvalidReason.CurrentStale);
        t.Mark("held", s);
        var reference = TestDevices.Reference.ReplaceLineEndings("\n");
        var withoutHeater = reference[..reference.IndexOf("- id: heater_l3", StringComparison.Ordinal)]
            + reference[reference.IndexOf("- id: ev_charger", StringComparison.Ordinal)..];
        s.Reconfigure(TestDevices.Document(withoutHeater));
        s.RunFor(TimeSpan.FromSeconds(30));
        s.SampleAll(8, 8, 8);
        t.Mark("redeclared", s);
        Compare(nameof(A_stage_held_for_a_device_the_reload_no_longer_declares_is_dropped_and_a_restore_earns_recovery), t.Finish(s));
    }

    [Fact]
    public void A_stepped_device_held_by_stage_is_released_by_stage()
    {
        var s = new GuardScenario(TestDevices.Document(DispatchDevices.Boiler, OneBoundary));
        var t = new Trace();
        s.SampleAll(8, 3, 3);
        s.Hold(Phase.L1, 33, TimeSpan.FromSeconds(10), T0 + TimeSpan.FromMinutes(2));
        t.Mark("overload", s);
        s.Hold(Phase.L1, 3, TimeSpan.FromSeconds(10), T0 + TimeSpan.FromMinutes(8));
        t.Mark("released", s);
        Compare(nameof(A_stepped_device_held_by_stage_is_released_by_stage), t.Finish(s));
    }

    private static void Compare(string name, string actual)
    {
        var path = RepoPaths.Combine("tests", "Switchboard.Tests", "Application", "Guard", "Golden", name + ".txt");
        actual = actual.ReplaceLineEndings("\n");
        if (Environment.GetEnvironmentVariable("SWITCHBOARD_CAPTURE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    /// <summary>A reflection dump: every public property, collections in order (dictionaries sorted), the setup skipped.</summary>
    private static void Dump(StringBuilder text, object? value, string indent, int depth)
    {
        switch (value)
        {
            case null:
                text.AppendLine(indent + "null");
                return;
            case string or bool or Enum or char:
                text.AppendLine(indent + value);
                return;
            case double d:
                text.AppendLine(indent + d.ToString("R", CultureInfo.InvariantCulture));
                return;
            case DateTimeOffset at:
                text.AppendLine(indent + at.ToString("O", CultureInfo.InvariantCulture));
                return;
            case TimeSpan span:
                text.AppendLine(indent + span.ToString("c", CultureInfo.InvariantCulture));
                return;
            case IFormattable f when value.GetType().IsPrimitive:
                text.AppendLine(indent + f.ToString(null, CultureInfo.InvariantCulture));
                return;
        }

        var type = value.GetType();
        if (depth > 14)
        {
            text.AppendLine(indent + "<deep " + type.Name + ">");
            return;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition().Name.StartsWith("ImmutableArray", StringComparison.Ordinal) && (bool)type.GetProperty("IsDefault")!.GetValue(value)!)
        {
            text.AppendLine(indent + "default");
            return;
        }

        if (value is IEnumerable items)
        {
            var isDictionary = type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>));
            var rendered = new List<string>();
            foreach (var item in items)
            {
                var one = new StringBuilder();
                Dump(one, item, indent + "  ", depth + 1);
                rendered.Add(one.ToString());
            }

            if (isDictionary)
            {
                rendered.Sort(StringComparer.Ordinal);
            }

            text.AppendLine(indent + "[" + rendered.Count + "]");
            foreach (var r in rendered)
            {
                text.Append(r);
            }

            return;
        }

        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.Name is not ("EqualityContract" or "Setup"))
            .ToList();
        if (properties.Count == 0)
        {
            text.AppendLine(indent + Convert.ToString(value, CultureInfo.InvariantCulture));
            return;
        }

        text.AppendLine(indent + type.Name);
        foreach (var property in properties)
        {
            text.AppendLine(indent + " ." + property.Name);
            Dump(text, property.GetValue(value), indent + "   ", depth + 1);
        }
    }

    /// <summary>
    /// Checkpoints (deadline, exported memory) and every evaluation the scenario made: a readable line
    /// (intents, events, limits, safe state) and a hash of the whole evaluation, so nothing in it can
    /// change unseen while a failing diff still says where. SWITCHBOARD_DUMP=1 writes the full dumps
    /// next to the trace (not committed).
    /// </summary>
    private sealed class Trace
    {
        private readonly StringBuilder _checkpoints = new();

        public void Mark(string label, GuardScenario s)
        {
            var export = new StringBuilder();
            Dump(export, s.Engine.Export(), string.Empty, 0);
            _checkpoints.AppendLine(CultureInfo.InvariantCulture, $"== {label}: evaluations={s.Evaluations.Count} now={s.Now:O} deadline={s.Engine.NextDeadline(s.Now):O} export={Hash(export.ToString())}");
            foreach (var line in export.ToString().Split('\n').Where(l => l.Contains("Budget", StringComparison.Ordinal) || l.Contains("CleanShutdown", StringComparison.Ordinal)))
            {
                _checkpoints.AppendLine("   " + line.Trim());
            }
        }

        public string Finish(GuardScenario s)
        {
            var text = new StringBuilder(_checkpoints.ToString());
            text.AppendLine("-- evaluations");
            for (var i = 0; i < s.Evaluations.Count; i++)
            {
                var e = s.Evaluations[i];
                var full = new StringBuilder();
                Dump(full, e, string.Empty, 0);
                text.AppendLine(CultureInfo.InvariantCulture, $"# {i} {e.At:O} cycle={e.CycleIndex} boundary={e.AtBoundary} valid={e.ConfigurationValid} safe={e.Status.SafeState.Active}/{e.Status.SafeState.Scope}/{e.Status.SafeState.Reason} hash={Hash(full.ToString())}");
                foreach (var intent in e.Intents)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"   intent {intent.Device} stage={intent.Stage} {intent.Target} {intent.Reason} phase={intent.Phase} group={intent.Group} over={intent.Overridden} cause={intent.Inputs.SafeStateCause}");
                }

                foreach (var ev in e.Events)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"   event {ev.Scope} group={ev.Group} phase={ev.Phase} {ev.Action} {ev.Reason} cause={ev.Inputs.SafeStateCause}");
                }

                foreach (var (device, stages) in e.Limits.OrderBy(l => l.Key))
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"   limit {device} {string.Join(',', stages.OrderBy(x => x.Key).Select(x => x.Key + "=" + x.Value))}");
                }
            }

            if (Environment.GetEnvironmentVariable("SWITCHBOARD_DUMP") == "1")
            {
                var all = new StringBuilder();
                foreach (var e in s.Evaluations)
                {
                    Dump(all, e, string.Empty, 0);
                }

                File.WriteAllText(Path.Combine(Path.GetTempPath(), "guard-dump-" + Hash(text.ToString()) + ".txt"), all.ToString());
            }

            return text.ToString();
        }

        private static string Hash(string value) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
    }
}
