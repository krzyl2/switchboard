using System.Collections.Immutable;
using System.Globalization;
using Switchboard.Application.Configuration;
using Switchboard.Application.Effector;
using Switchboard.Application.Modes;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;

namespace Switchboard.Tests.Support;

/// <summary>
/// A charger stopped through a select (0.7.1, the go-e force state: "1" stops it, "0" lets it run)
/// in the control loop as production wires it: the real signal store reads its states, the guard,
/// dispatch and the effector gate decide, and every command is resolved through the actuators of the
/// configuration loaded now and checked by the allow-list exactly as on the live link
/// (<see cref="PlantCommandPath"/>). The device follows what was sent at the next meter poll, unless
/// the test freezes it, and the car draws what it is allowed (nothing while stopped), so the meter
/// sees the change. Nothing reaches a Home Assistant; the clock is virtual. With
/// <c>switchStop</c> the same charger is stopped by a switch instead (0.7.0's stop), so a rule can
/// be shown to hold for both kinds of stop.
/// </summary>
internal sealed class SelectStopRig
{
    /// <summary>The charger with its select stop; the car draws its own current (draw sensors), and its states report on change.</summary>
    public const string Charger = """
          - id: ev_charger
            type: modulating
            actuator: number.charger_current
            stop_actuator: select.charger_force
            stop_option: "1"
            resume_option: "0"
            phase: L1
            min: 6
            max: 20
            step: 1
            value_per_kwh: 2.03
            state_reports: on_change
            draw: { current: sensor.charger_draw_current, power: sensor.charger_draw_power, max_age: 60s }
        """;

    /// <summary>The same charger with a switch as its stop actuator (on runs it, off stops it).</summary>
    public static readonly string SwitchCharger = Charger.ReplaceLineEndings("\n")
        .Replace("    stop_actuator: select.charger_force\n    stop_option: \"1\"\n    resume_option: \"0\"\n", "    stop_actuator: switch.charger_enable\n", StringComparison.Ordinal);

    public static readonly ImmutableArray<string> Offered = ["0", "1", "2"];

    private const double Volts = 230;

    private int _followed;
    private double? _pushedSetpoint;
    private string? _pushedOption;

    /// <param name="option">What the select reports at the start ("0" neutral: the charger runs).</param>
    /// <param name="switchStop">The charger's stop is a switch (on/off) instead of the select.</param>
    /// <param name="control">The document's control: block (indent 2), for a test that needs other parameters.</param>
    public SelectStopRig(OperatingMode ceiling = OperatingMode.Active, bool allowed = true, string start = "restore_only", string option = "0", double setpoint = 10, string devices = Charger, DateTimeOffset? at = null, bool switchStop = false, string control = "")
    {
        SwitchStop = switchStop;
        Yaml = TestDevices.Document(switchStop ? SwitchCharger : devices, control, defaultStart: start);
        Configuration = TestConfigurations.Valid(Yaml).Configuration!;
        Path = new PlantCommandPath(Configuration, plant: null);
        Harness = new GuardRunnerHarness(Yaml, ceiling, port: Path.Port, gateLogger: Log.For<EffectorGate>());
        if (at is { } later)
        {
            Harness.Time.Advance(later - Harness.Now);
        }

        if (allowed)
        {
            Harness.Control.Set(true, ControlSwitchSource.Api);
        }

        Option = switchStop ? (option == "1" ? "off" : "on") : option;
        Setpoint = setpoint;
    }

    /// <summary>The charger's stop is a switch.</summary>
    public bool SwitchStop { get; }

    /// <summary>What the stop actuator reports while it stops the charger.</summary>
    public string Stopped => SwitchStop ? "off" : "1";

    /// <summary>The draw sensors report at every poll; false: they fall silent (a lost sample).</summary>
    public bool ReportDraw { get; set; } = true;

    /// <summary>The stop actuator's commands in order, by meaning: "stop" or "run", whatever the kind of stop.</summary>
    public IEnumerable<string> StopCommands => Path.Calls
        .Where(c => c.Call.Service is ActuatorService.SelectOption or ActuatorService.TurnOn or ActuatorService.TurnOff)
        .Select(c => c.Call.Service == ActuatorService.TurnOff || c.Call.Option == "1" ? "stop" : "run");

    public string Yaml { get; }

    public SwitchboardConfiguration Configuration { get; }

    public PlantCommandPath Path { get; }

    public GuardRunnerHarness Harness { get; }

    public LogCapture Log { get; } = new();

    /// <summary>What the select reports.</summary>
    public string Option { get; set; }

    /// <summary>What the charger's current number reports (A).</summary>
    public double Setpoint { get; set; }

    /// <summary>What the car would take at most (A).</summary>
    public double CarWants { get; set; } = 10;

    /// <summary>The device ignores what is sent (a lost link, a broken integration).</summary>
    public bool Frozen { get; set; }

    /// <summary>What the car draws now: what it is allowed, nothing while stopped.</summary>
    public double Draw => Option == Stopped ? 0 : Math.Min(Setpoint, CarWants);

    /// <summary>The select calls that left, in order, as option texts.</summary>
    public IEnumerable<string> SelectOptions => Path.Calls.Where(c => c.Call.Service == ActuatorService.SelectOption).Select(c => c.Call.Option!);

    /// <summary>The first state of everything, as the subscription's initial states deliver it, and one step.</summary>
    public SelectStopRig Begin(double l1 = 2)
    {
        PushAll(l1);
        Harness.Runner.Step();
        return this;
    }

    /// <summary>The meter only, the charger's own states not reported yet (unknown: protection holds it off), and one step.</summary>
    public SelectStopRig BeginWithoutCharger(double l1 = 2)
    {
        _pushedSetpoint = Setpoint;
        _pushedOption = Option;
        PushAll(l1);
        Harness.Runner.Step();
        return this;
    }

    /// <summary>
    /// The charger's states as an initial state delivers them: what the device reports now, with
    /// Home Assistant's own report time <paramref name="reportedAt"/> (an unchanged state keeps its
    /// old last_reported), no step.
    /// </summary>
    public void ReportCharger(DateTimeOffset reportedAt)
    {
        Harness.Store.Apply(EntityUpdateBatch.Of(
            BatchOrigin.Initial,
            new EntityUpdate(EntityId.Parse("number.charger_current"), Format(Setpoint), "A", reportedAt, reportedAt),
            new EntityUpdate(EntityId.Parse("select.charger_force"), Option, null, reportedAt, reportedAt) { Options = Offered }), Harness.Now);
        _pushedSetpoint = Setpoint;
        _pushedOption = Option;
    }

    /// <summary>Home Assistant is gone: the source disconnects (every state unknown) and the link carries nothing.</summary>
    public void Disconnect()
    {
        Harness.Store.ApplySourceStatus(SignalSourceStatus.Disconnected);
        Path.Port.Answer = _ => (CommandOutcome.NotSent, CommandNotSentReason.LinkDown);
        Frozen = true;
    }

    /// <summary>A new link delivered its states: the source is connected again and the link generation grows.</summary>
    public void Reconnect()
    {
        Harness.Store.ApplySourceStatus(SignalSourceStatus.Connected);
        Path.Port.Answer = command => Path.Refused.Any(r => ReferenceEquals(r.Command, command)) ? (CommandOutcome.NotSent, CommandNotSentReason.NotDeclared) : (CommandOutcome.Acknowledged, null);
        Path.Port.LinkGeneration++;
        Follow();
        Frozen = false;
        _pushedSetpoint = null;
        _pushedOption = null;
    }

    /// <summary>One meter poll <paramref name="seconds"/> later: the device follows what was sent, everything reports, one step.</summary>
    public void Poll(double l1, double seconds = 10)
    {
        Harness.Time.Advance(TimeSpan.FromSeconds(seconds));
        Follow();
        PushAll(l1);
        Harness.Runner.Step();
    }

    /// <summary>Polls every 10 s for <paramref name="duration"/> at the same base load.</summary>
    public void Hold(double l1, TimeSpan duration)
    {
        var end = Harness.Now + duration;
        while (Harness.Now < end)
        {
            Poll(l1);
        }
    }

    /// <summary>The ledger's view of the charger.</summary>
    public LedgerEntry Ledger => Harness.Gate.Ledger.Entry(DeviceName.Parse("ev_charger"))!;

    /// <summary>The device takes what was sent (unless frozen).</summary>
    public void Follow()
    {
        foreach (var (_, call, _) in Path.Calls.Skip(_followed))
        {
            if (Frozen)
            {
                continue;
            }

            switch (call.Service)
            {
                case ActuatorService.SelectOption:
                    Option = call.Option!;
                    break;
                case ActuatorService.TurnOn:
                    Option = "on";
                    break;
                case ActuatorService.TurnOff:
                    Option = "off";
                    break;
                case ActuatorService.SetValue:
                    Setpoint = call.Value!.Value;
                    break;
            }
        }

        _followed = Path.Calls.Count;
    }

    /// <summary>
    /// The meter (base load plus the car's draw on L1, the others quiet), the asleep inverter, the
    /// car's draw, and the charger's own states only when they changed (they report on change).
    /// </summary>
    private void PushAll(double l1)
    {
        var now = Harness.Now;
        var updates = new List<EntityUpdate>();
        void Add(string entity, double value, string unit) => updates.Add(new EntityUpdate(EntityId.Parse(entity), Format(value), unit, now, now));

        var amps = new[] { l1 + Draw, 5, 5 };
        foreach (var (letter, index) in new[] { ("a", 0), ("b", 1), ("c", 2) })
        {
            Add($"sensor.meter_current_{letter}", amps[index], "A");
            Add($"sensor.meter_power_{letter}", -amps[index] * Volts, "W");
            Add($"sensor.meter_voltage_{letter}", Volts, "V");
            Add($"sensor.inverter_current_{letter}", 0, "A");
        }

        Add("sensor.inverter_power", 0, "W");
        if (ReportDraw)
        {
            Add("sensor.charger_draw_current", Draw, "A");
            Add("sensor.charger_draw_power", Draw * Volts, "W");
        }

        if (_pushedSetpoint != Setpoint)
        {
            Add("number.charger_current", Setpoint, "A");
            _pushedSetpoint = Setpoint;
        }

        if (_pushedOption != Option)
        {
            updates.Add(SwitchStop
                ? new EntityUpdate(EntityId.Parse("switch.charger_enable"), Option, null, now, now)
                : new EntityUpdate(EntityId.Parse("select.charger_force"), Option, null, now, now) { Options = Offered });
            _pushedOption = Option;
        }

        Harness.Store.Apply(new EntityUpdateBatch(BatchOrigin.Change, [.. updates], [], []), now);
        Harness.Publish();
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
