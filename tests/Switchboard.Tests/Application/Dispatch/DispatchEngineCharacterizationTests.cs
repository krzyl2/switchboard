using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Guard;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Planning;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.EconomicsScenarios;

namespace Switchboard.Tests.Application.Dispatch;

/// <summary>
/// Pins what the dispatch engine returns, evaluation by evaluation, before and after it is split into
/// smaller parts (UPGRADE.md, DispatchEngine): the whole <see cref="DispatchEvaluation"/> (wishes with their
/// inputs, states, events, phases, device powers, balance, economics, tariff fallback) and the decisions the
/// gate made from it, for the merit order, settling and the increase limit, foreign load, overrides, rotation
/// ranking, circuit groups, the hourly balance and its allowance gate, the sources and price test, displacement,
/// net billing, dynamic prices and their fallback, the value order that follows prices, the plan's narrowing
/// and a clock step back, an invalid configuration and a reload. The traces under Golden/ were recorded on the
/// engine before the split and are never edited to make a refactoring pass; a deliberate change of behaviour
/// re-records them with SWITCHBOARD_CAPTURE=1, in a commit that says so.
/// </summary>
public sealed class DispatchEngineCharacterizationTests
{
    private static readonly DateTimeOffset T0 = GuardScenario.Start;
    private static readonly TimeSpan Cycle = TimeSpan.FromSeconds(30);
    private const string OneUp = "  dispatch:\n    persistence_up_cycles: 1\n";
    private const double V = GuardScenario.Volts;
    private static readonly string[] AllHeaters = ["heater_l1", "heater_l2", "heater_l3"];

    [Fact]
    public void Foreign_load_the_increase_limit_decreases_a_clock_step_back_and_an_invalid_configuration()
    {
        var s = DispatchScenario.Create(DispatchDevices.Heaters, AllHeaters, string.Empty);
        s.PollUntil(T0 + (10 * Cycle), 3, 3, 3);
        s.PollUntil(T0 + (12 * Cycle), 3 + (2000 / V), 3, 3);
        s.PollUntil(T0 + (14 * Cycle), 3, 3, 3);
        s.PollUntil(T0 + (16 * Cycle), 9.6, 9.6, 3);
        s.PollUntil(T0 + (20 * Cycle), 16.6, 16.6, null);
        s.PollUntil(T0 + (24 * Cycle), 3, 3, 3);
        s.Time.StepBack(TimeSpan.FromHours(1));
        s.Evaluate();
        s.Time.Advance(TimeSpan.FromSeconds(30));
        s.PollUntil(s.Now + (6 * Cycle), 3 + (2000 / V), 3, 3);
        s.PollUntil(s.Now + (6 * Cycle), 3, 3, 3);
        s.ConfigurationValid(false);
        s.PollUntil(s.Now + (2 * Cycle), 3, 3, 3);
        s.ConfigurationValid(true);
        s.PollUntil(s.Now + (14 * Cycle), 3, 3, 3);
        Compare(nameof(Foreign_load_the_increase_limit_decreases_a_clock_step_back_and_an_invalid_configuration), s);
    }

    [Fact]
    public void Merit_order_settling_and_verification_with_commands_leaving()
    {
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(DispatchDevices.HeaterAndCharger, [], OneUp, mode: DispatchScenario.Active, port: port);
        s.Follow(port);
        s.PollUntil(T0 + TimeSpan.FromMinutes(2), 8, 3, 3);
        s.PollUntil(T0 + TimeSpan.FromMinutes(4), 28, 3, 3);
        s.PollUntil(T0 + TimeSpan.FromMinutes(5), 33, 3, 3);
        s.PollUntil(T0 + TimeSpan.FromMinutes(7), 45, 3, 3);
        s.PollUntil(T0 + TimeSpan.FromMinutes(9), 14, 3, 3);
        s.PollUntil(T0 + TimeSpan.FromMinutes(16), 3, 3, 3);
        s.PollUntil(T0 + TimeSpan.FromMinutes(24), 3 + (3500 / V), 3, 3);
        s.PollUntil(T0 + TimeSpan.FromMinutes(32), 3, 3, 3);
        Compare(nameof(Merit_order_settling_and_verification_with_commands_leaving), s);
    }

    [Fact]
    public void Overrides_a_stepped_boiler_and_a_pool_pump_outrank_allocation_but_never_protection()
    {
        var port = new RecordingDeviceCommandPort();
        var s = DispatchScenario.Create(DispatchDevices.HeaterAndPool + "\n" + DispatchDevices.Boiler, ["heater_l1", "pool", "boiler"], OneUp, mode: DispatchScenario.Active, port: port);
        foreach (var stage in new[] { 1, 2, 3 })
        {
            s.SetState(DeviceName.Parse("boiler"), stage, new RelayApplied(false));
        }

        s.Follow(port);
        s.PollUntil(T0 + TimeSpan.FromMinutes(3), 3, 3, 3);
        s.Override("pool", OverrideMode.ForceOn);
        s.PollUntil(T0 + TimeSpan.FromMinutes(5), 3, 3, 3);
        s.Override("heater_l1", OverrideMode.ForceOff);
        s.Override("pool", OverrideMode.Auto);
        s.PollUntil(T0 + TimeSpan.FromMinutes(8), 20, 3, 3);
        s.Override("boiler", OverrideMode.ForceOn);
        s.PollUntil(T0 + TimeSpan.FromMinutes(11), 3, 3, 3);
        s.PollUntil(T0 + TimeSpan.FromMinutes(14), 24, 3, 3);
        s.Override("boiler", OverrideMode.Auto);
        s.Override("heater_l1", OverrideMode.Auto);
        s.PollUntil(T0 + TimeSpan.FromMinutes(20), 3, 3, 3);
        Compare(nameof(Overrides_a_stepped_boiler_and_a_pool_pump_outrank_allocation_but_never_protection), s);
    }

    [Fact]
    public void A_rotation_group_follows_the_phase_ranking_after_its_stability_cycles()
    {
        var s = DispatchScenario.Create(DispatchDevices.RotatedHeaters, AllHeaters, OneUp);
        s.PollUntil(T0 + TimeSpan.FromMinutes(3), 3, 10, 12);
        s.PollUntil(T0 + TimeSpan.FromMinutes(6), 10, 3, 12);
        s.PollUntil(T0 + TimeSpan.FromMinutes(9), 12, 10, 3);
        s.PollUntil(T0 + TimeSpan.FromMinutes(12), 3, 3, 3);
        Compare(nameof(A_rotation_group_follows_the_phase_ranking_after_its_stability_cycles), s);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Circuit_groups_bind_dispatch_estimated_and_sub_metered(bool subMetered)
    {
        const string Devices = """
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
        var group = subMetered
            ? "circuit_groups:\n  - id: carport\n    breaker_rating_a: 16\n    characteristic: B\n    phases: [L1]\n    current: { L1: sensor.carport_current_l1 }\n    current_max_age: 90s\n"
            : "circuit_groups:\n  - id: carport\n    breaker_rating_a: 7\n    characteristic: B\n    phases: [L1]\n";
        var s = DispatchScenario.Create(Devices, ["carport_heater", "house_heater"], string.Empty, extra: group);
        Action refresh = subMetered ? () => s.SampleGroup("carport", Phase.L1, 5) : () => { };
        refresh();
        s.PollUntil(T0 + (8 * Cycle), 3, 3, 3, refresh);
        if (subMetered)
        {
            refresh = () => s.SampleGroup("carport", Phase.L1, 21);
            s.PollUntil(T0 + (14 * Cycle), 8, 3, 3, refresh);
            refresh = () => s.SampleGroup("carport", Phase.L1, null);
            s.PollUntil(T0 + (18 * Cycle), 8, 3, 3, refresh);
        }

        s.PollUntil(T0 + (30 * Cycle), 3, 3, 3);
        Compare(nameof(Circuit_groups_bind_dispatch_estimated_and_sub_metered) + (subMetered ? "_sub_metered" : "_estimated"), s);
    }

    [Fact]
    public void The_hourly_balance_gates_starts_stops_and_the_unknown_balance_and_resets_without_counters()
    {
        var s = DispatchScenario.Create(DispatchDevices.Heaters, AllHeaters, EconomicsScenarios.OneBoundary);
        Export(s, 3000);
        Run(s, Feed(s, 600), 5);
        Export(s, -500);
        Run(s, Feed(s, -100), 4);
        Export(s, 3000);
        Run(s, Feed(s, 600), 3);
        Run(s, Feed(s, null), 3);
        Run(s, Feed(s, 600, TimeSpan.FromMinutes(5)), 2);
        Run(s, Feed(s, 600), 2);
        s.Energy = null;
        s.Balance = null;
        s.PollUntil(s.Now + TimeSpan.FromMinutes(2), 1, 1, 1);
        s.ConfigurationValid(false);
        s.PollUntil(s.Now + TimeSpan.FromMinutes(1), 1, 1, 1);
        s.ConfigurationValid(true);
        Run(s, Feed(s, 600), 3);
        Compare(nameof(The_hourly_balance_gates_starts_stops_and_the_unknown_balance_and_resets_without_counters), s);
    }

    [Fact]
    public void Sources_the_price_test_and_displacement_under_hourly_netting()
    {
        var heater = Device("heater_l1", "L1", 6.6, 1500, 0.35, "pv_only");
        var car = Device("ev_charger", "L2", 6.6, 1500, 2.03, "pv_only");
        var s = DispatchScenario.Create(heater + car, ["ev_charger"], EconomicsScenarios.OneBoundary, start: Workday(11), extra: "\n" + Economics());
        s.Time.Zone = Warsaw;
        Export(s, 900);
        Run(s, Feed(s, 600), 3);
        Export(s, 3000);
        Run(s, Feed(s, 600), 3);
        Export(s, -300);
        Run(s, Feed(s, 100), 3);
        Run(s, Feed(s, null), 2);
        Compare(nameof(Sources_the_price_test_and_displacement_under_hourly_netting), s);
    }

    [Fact]
    public void A_zone_class_runs_and_stops_at_the_zone_end_and_a_reload_changes_the_settlement()
    {
        var s = Create(HeatersAndCar(), Workday(6, 30), ["ev_charger"]);
        Export(s, 0);
        Run(s, Feed(s, 100), 12);
        Run(s, Feed(s, 100), 12);
        Export(s, 3000);
        Run(s, Feed(s, 600), 3);
        s.Reconfigure(Document(HeatersAndCar(), Economics("net_billing", "0.35")));
        Run(s, Feed(s, 600), 3);
        Export(s, 1000);
        Run(s, Feed(s, 600), 3);
        Export(s, -500);
        Run(s, Feed(s, 600), 3);
        s.Reconfigure(Document(HeatersAndCar(), economics: string.Empty));
        Run(s, Feed(s, 600), 3);
        Compare(nameof(A_zone_class_runs_and_stops_at_the_zone_end_and_a_reload_changes_the_settlement), s);
    }

    [Fact]
    public void Net_billing_decides_on_the_instantaneous_surplus_smoothed_over_its_own_window()
    {
        var s = Create(HeatersAndCar(), Workday(11), ["heater_l1", "heater_l2", "heater_l3", "ev_charger"], Economics("net_billing", "0.35"));
        Export(s, 3000);
        Run(s, Feed(s, 600), 4);
        Export(s, 1000);
        Run(s, Feed(s, 600), 3);
        Export(s, -500);
        Run(s, Feed(s, 600), 3);
        Run(s, null, 3);
        Export(s, 3000);
        Run(s, null, 3);
        Compare(nameof(Net_billing_decides_on_the_instantaneous_surplus_smoothed_over_its_own_window), s);
    }

    [Fact]
    public void A_dynamic_export_price_goes_live_stale_to_its_fallback_and_back()
    {
        var heater = Device("dev", "L1", 6.6, 1500, 0.35, "pv_only");
        var economics = Economics(exportPrice: $"\n      signal: {PriceScenarios.Signal}\n      fallback: 0.30\n");
        var (s, p) = PriceScenarios.Create(heater, Workday(11), ["dev"], economics);
        Export(s, 3000);
        p.Report("500");
        p.Run(2);
        p.Report("200");
        p.Run(2);
        p.Run(4);
        p.Report("200");
        p.Run(2);
        p.Report("not a number");
        p.Run(2);
        Compare(nameof(A_dynamic_export_price_goes_live_stale_to_its_fallback_and_back), s);
    }

    [Fact]
    public void The_value_order_follows_prices_only_after_its_stability_cycles()
    {
        var follower = Device("a_follow", "L1", 6.6, 1500, 1.0, null, "    value_follows: import_price\n    value_factor: 2\n");
        var fixedValue = Device("b_static", "L2", 6.6, 1500, 2.0, null);
        var s = Create(follower + fixedValue, Workday(6, 58), []);
        s.PollUntil(s.Now + TimeSpan.FromSeconds(50), 1, 1, 1);
        s.PollUntil(s.Now + TimeSpan.FromSeconds(140), 1, 1, 1);
        s.PollUntil(s.Now + TimeSpan.FromSeconds(60), 1, 1, 1);
        s.Reconfigure(Document(follower + fixedValue));
        s.PollUntil(s.Now + TimeSpan.FromSeconds(60), 1, 1, 1);
        Compare(nameof(The_value_order_follows_prices_only_after_its_stability_cycles), s);
    }

    [Fact]
    public void The_plan_narrows_defers_completes_and_runs_an_urgent_goal_first_and_a_mode_off_is_v2()
    {
        var view = new ScriptedPlanView(PlannerDispatchMode.Full);
        var car = Device("dev", "L1", 6.6, 1500, 2.03, "pv_and_zone:offpeak");
        var other = Device("dev_lo", "L1", 6.6, 1500, 0.35, "pv_only");
        var s = Create(car + other, Workday(23), ["dev", "dev_lo"], plan: view);
        var dev = DeviceName.Parse("dev");
        void Entry(PlanWant want, string? klass = null, double kwh = 5, double max = 10_000, bool zone = true) =>
            view.Entries[dev] = ScriptedPlanView.Entry(s.Now, want, klass, kwh, max, zone);
        void Step(double wh, double minutes) => Run(s, Feed(s, wh), minutes);

        var low = DeviceName.Parse("dev_lo");
        void Running(bool on) => s.SetState("dev", new RelayApplied(on));

        Export(s, -500);
        Entry(PlanWant.Defer);
        Step(-100, 3);
        Entry(PlanWant.Run, "zone:offpeak", kwh: 5);
        Step(-100, 4);
        Running(true);
        Step(-100, 4);
        Entry(PlanWant.Defer);
        Step(-100, 3);
        Running(false);
        Entry(PlanWant.Run, "zone:offpeak", kwh: 0);
        Step(-100, 3);
        Entry(PlanWant.Run, "zone:offpeak", max: 1000);
        Step(-100, 3);
        Running(true);
        Entry(PlanWant.Run, "zone:offpeak", kwh: 0.02);
        Step(-100, 5);
        Entry(PlanWant.Done);
        Step(-100, 3);
        Running(false);
        Step(100, 3);
        view.Entries.Clear();
        view.Entries[low] = ScriptedPlanView.Entry(s.Now, PlanWant.Urgent, "zone:offpeak");
        Step(-100, 3);
        Entry(PlanWant.Urgent, "zone:offpeak");
        Step(-100, 3);
        view.Mode = PlannerDispatchMode.Narrow;
        Step(-100, 3);
        view.Mode = PlannerDispatchMode.Off;
        Step(-100, 3);
        view.Mode = PlannerDispatchMode.Full;
        view.Entries.Clear();
        Step(-100, 3);
        s.Override("dev", OverrideMode.ForceOff);
        Entry(PlanWant.Run, "zone:offpeak");
        Step(-100, 3);
        Compare(nameof(The_plan_narrows_defers_completes_and_runs_an_urgent_goal_first_and_a_mode_off_is_v2), s);
    }

    private static void Compare(string name, GuardScenario s)
    {
        var actual = Render(s).ReplaceLineEndings("\n");
        var path = RepoPaths.Combine("tests", "Switchboard.Tests", "Application", "Dispatch", "Golden", name + ".txt");
        if (Environment.GetEnvironmentVariable("SWITCHBOARD_CAPTURE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    /// <summary>A hash of the whole evaluation, so nothing in it can change unseen, and a readable line per wish and event so a failing diff says where.</summary>
    private static string Render(GuardScenario s)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"dispatches={s.Dispatches.Count} decisions={s.Decisions.Count}");
        var shown = new Dictionary<string, string>();
        for (var i = 0; i < s.Dispatches.Count; i++)
        {
            var d = s.Dispatches[i];
            var full = new StringBuilder();
            Dump(full, d, string.Empty, 0);
            text.AppendLine(CultureInfo.InvariantCulture, $"# {i} {d.At:O} hash={Hash(full.ToString())}");

            // Readable lines only where they differ from the last evaluation's (the hash already covers every field).
            void Show(string key, string line)
            {
                if (!shown.TryGetValue(key, out var last) || last != line)
                {
                    shown[key] = line;
                    text.AppendLine("   " + line);
                }
            }

            Show("ranking", $"ranking {string.Join(',', d.Ranking)}");
            foreach (var w in d.Wishes)
            {
                Show("wish " + w.Device, $"wish {w.Device} {w.Level} {w.Reason} {w.Change} {w.Origin} blocked={w.BlockedBy}");
            }

            foreach (var state in d.Devices)
            {
                Show("state " + state.Device, $"state {state.Device} {state.Reason} pending={state.Pending} rank={state.StartRank}");
            }

            foreach (var e in d.Events)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"   event {e.Scope} {e.Phase} {e.Action} {e.Reason}");
            }

            foreach (var change in d.TariffFallback)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"   fallback {change}");
            }
        }

        text.AppendLine("-- decisions");
        foreach (var decision in s.Decisions)
        {
            var full = new StringBuilder();
            Dump(full, decision, string.Empty, 0);
            text.AppendLine(CultureInfo.InvariantCulture, $"{decision.At:O} {decision.Device} {decision.Action} {decision.Reason} commanded={decision.Commanded} hash={Hash(full.ToString())}");
        }

        return text.ToString();
    }

    private static string Hash(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

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
}
