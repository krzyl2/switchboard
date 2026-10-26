using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Balance;
using Switchboard.Application.Configuration;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Signals;
using Switchboard.Domain.Balance;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Tests.Support;

/// <summary>
/// Drives the guard engine, (optionally) the dispatch engine and the effector gate directly on a
/// virtual clock, with inputs written by the test: phase currents with their report times and
/// directions, phase power, device states, device draws and overrides. The setup comes from a real
/// configuration document through the real parser, defaults and factories. Ticks happen exactly at
/// the deadlines the runner arms (guard and gate). Without <c>dispatch: true</c> only protection
/// runs (the Stage 2 view); with it, every step runs guard → dispatch → gate as the runner does.
/// </summary>
internal sealed class GuardScenario
{
    /// <summary>A cycle boundary (multiple of 30 s since the Unix epoch).</summary>
    public static readonly DateTimeOffset Start = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Nominal voltage of the test documents: phase power follows the current at it.</summary>
    public const double Volts = 230;

    private readonly Dictionary<Phase, PhaseProtection> _phases = [];
    private readonly Dictionary<Phase, PhaseDispatch> _power = [];
    private readonly Dictionary<(DeviceName, int), DeviceStateSample> _states = [];
    private readonly Dictionary<(DeviceName, Phase), DeviceDrawSample> _draws = [];
    private readonly Dictionary<(DeviceName, Phase), DevicePowerSample> _powerDraws = [];
    private readonly HashSet<Phase> _powerPinned = [];
    private readonly DecisionIdGenerator _ids = new("test");
    private readonly bool _dispatch;
    private bool _configurationValid = true;
    private SetupAbsence _setupAbsence = SetupAbsence.Invalid;
    private long _sequence;

    /// <param name="settledLockouts">
    /// True (the default) starts every lockout an hour before <paramref name="start"/>, as in a
    /// process that has been running; false starts them at the first evaluation, as after a restart.
    /// </param>
    /// <param name="dispatch">Run dispatch in every step (Stage 3); false keeps the guard alone.</param>
    /// <param name="port">A recording command port: the gate is then built through its test-only constructor.</param>
    public GuardScenario(string yaml, DateTimeOffset? start = null, ModeStatus? mode = null, bool settledLockouts = true, bool dispatch = false, IDeviceCommandPort? port = null)
    {
        Time = new SteppableTime(start ?? Start);
        DispatchEngine = new DispatchEngine(Time);
        Configuration = TestConfigurations.Valid(yaml).Configuration!;
        Setup = ProtectionSetupFactory.Build(Configuration);
        DispatchSetup = DispatchSetupFactory.Build(Configuration);
        Engine = new GuardEngine(Lockouts, Targets, NullLogger<GuardEngine>.Instance);
        var settings = new EffectorSettings(20);
        Gate = port is null
            ? new EffectorGate(Lockouts, Targets, _ids, settings, NullLogger<EffectorGate>.Instance)
            : new EffectorGate(Lockouts, Targets, _ids, settings, NullLogger<EffectorGate>.Instance, port);
        Mode = mode ?? TestModes.Shadow;
        _dispatch = dispatch;

        // Every device on (the charger at its maximum) and every phase at a quiet 5 A, so a test
        // changes only what it is about; nothing is evaluated until the test asks.
        foreach (var device in Setup.Devices)
        {
            foreach (var stage in device.Stages)
            {
                SetState(device.Id, stage.Index, device.IsSetpoint ? new SetpointApplied(device.Range!.Max) : new RelayApplied(true));
            }

            if (device.HasStopActuator)
            {
                SetState(device.Id, DeviceStateSample.StopStage, new RelayApplied(true));
            }
        }

        foreach (var phase in Setup.Phases)
        {
            Sample(phase, 5, evaluate: false);
        }

        if (settledLockouts)
        {
            Lockouts.Observe(Setup, [.. _states.Values], Now - TimeSpan.FromHours(1));
        }
    }

    public SteppableTime Time { get; }

    public SwitchboardConfiguration Configuration { get; private set; }

    public ProtectionSetup Setup { get; private set; }

    public DispatchSetup DispatchSetup { get; private set; }

    /// <summary>A configuration reload: the next evaluation sees the new setup.</summary>
    public void Reconfigure(string yaml)
    {
        Configuration = TestConfigurations.Valid(yaml).Configuration!;
        Setup = ProtectionSetupFactory.Build(Configuration);
        DispatchSetup = DispatchSetupFactory.Build(Configuration);
    }

    public LockoutClocks Lockouts { get; } = new();

    public DeviceTargets Targets { get; } = new();

    public GuardEngine Engine { get; }

    public DispatchEngine DispatchEngine { get; }

    public EffectorGate Gate { get; }

    public ModeStatus Mode { get; set; }

    /// <summary>
    /// A change of the mode as the runner applies it before the next step: the gate hears of a change
    /// of posture (commands leave or not) first; a null <paramref name="previous"/> is the first step
    /// of a start.
    /// </summary>
    public void ChangeMode(ModeStatus next, bool start = false)
    {
        if (start || Mode.Commands != next.Commands)
        {
            Gate.ModeChanged(start ? null : Mode, next, Now);
        }

        Mode = next;
    }

    public KillSwitchStatus KillSwitch { get; set; } = KillSwitchStatus.Off;

    public OverrideSnapshot Overrides { get; private set; } = OverrideSnapshot.None;

    public DateTimeOffset Now => Time.GetUtcNow();

    public List<GuardEvaluation> Evaluations { get; } = [];

    public List<DispatchEvaluation> Dispatches { get; } = [];

    public List<GuardIntent> Intents { get; } = [];

    /// <summary>Every intent with the time of the evaluation that produced it.</summary>
    public List<(DateTimeOffset At, GuardIntent Intent)> Timeline { get; } = [];

    public List<GuardEvent> Events { get; } = [];

    public List<Decision> Decisions { get; } = [];

    public GuardEvaluation Last => Evaluations[^1];

    /// <summary>Breaker rating in A (the reference installation: 25 A).</summary>
    public double Rating => Setup.Breaker.Rating.Value;

    /// <summary>
    /// A new current sample on a phase, reported now (or at <paramref name="reportedAt"/>); evaluates.
    /// Unless the test pinned the phase power, the same meter poll reports the power at 230 V.
    /// </summary>
    public GuardEvaluation Sample(Phase phase, double amps, FlowDirection direction = FlowDirection.Import, DateTimeOffset? reportedAt = null, bool evaluate = true)
    {
        var signed = direction == FlowDirection.Export ? -Math.Abs(amps) : Math.Abs(amps);
        var at = reportedAt ?? Now;
        var reading = new CurrentReading(new Amperes(signed), new Amperes(Math.Abs(amps)), direction, DirectionSource.SignedSignal, at);
        _phases[phase] = new PhaseProtection(phase, true, null, reading);
        if (!_powerPinned.Contains(phase))
        {
            SetLoad(phase, signed * Volts, at, pin: false);
        }

        return evaluate ? Evaluate() : Evaluations.LastOrDefault()!;
    }

    /// <summary>Samples every phase of the topology at once (one meter poll).</summary>
    public GuardEvaluation SampleAll(double l1, double l2, double l3)
    {
        Sample(Phase.L1, l1, evaluate: false);
        Sample(Phase.L2, l2, evaluate: false);
        Sample(Phase.L3, l3, evaluate: false);
        return Evaluate();
    }

    /// <summary>A sample as a multiple of the rated current.</summary>
    public GuardEvaluation SampleMultiple(Phase phase, double multiple, FlowDirection direction = FlowDirection.Import) =>
        Sample(phase, multiple * Rating, direction);

    public GuardEvaluation Invalidate(Phase phase, ProtectionInvalidReason reason, bool evaluate = true)
    {
        _phases[phase] = new PhaseProtection(phase, false, reason, null);
        return evaluate ? Evaluate() : Evaluations.LastOrDefault()!;
    }

    /// <summary>The phase's house load (no generation): what dispatch reads (W); <paramref name="pin"/> keeps it when currents are sampled.</summary>
    public void SetLoad(Phase phase, double watts, DateTimeOffset? reportedAt = null, bool pin = true)
    {
        if (pin)
        {
            _powerPinned.Add(phase);
        }

        var w = new Watts(watts);
        _power[phase] = new PhaseDispatch(phase, true, null, w, Watts.Zero, w, new Volts(Volts)) { PowerReportedAt = reportedAt ?? Now };
    }

    /// <summary>Dispatch data of a phase not valid (power stale or unmapped).</summary>
    public void InvalidatePower(Phase phase, DispatchInvalidReason reason = DispatchInvalidReason.PowerNotFresh)
    {
        _powerPinned.Add(phase);
        _power[phase] = new PhaseDispatch(phase, false, reason, null, null, null, null);
    }

    public void SetState(DeviceName device, int stage, AppliedState? applied, DeviceStateInvalidReason? invalid = null, DateTimeOffset? reportedAt = null) =>
        _states[(device, stage)] = new DeviceStateSample(device, stage, applied is not null, applied is null ? invalid ?? DeviceStateInvalidReason.Stale : null, applied, reportedAt ?? Now);

    public void SetState(string device, AppliedState? applied, int stage = 1, DeviceStateInvalidReason? invalid = null) =>
        SetState(DeviceName.Parse(device), stage, applied, invalid);

    /// <summary>A device's own draw current on a phase (null: not fresh), reported now.</summary>
    public void SetDraw(string device, Phase phase, double? amps, DateTimeOffset? reportedAt = null) =>
        _draws[(DeviceName.Parse(device), phase)] = new DeviceDrawSample(DeviceName.Parse(device), phase, amps is not null, amps is { } a ? new Amperes(a) : null, reportedAt ?? Now);

    /// <summary>A device's own draw power on a phase (null: not fresh), reported now.</summary>
    public void SetPowerDraw(string device, Phase phase, double? watts, DateTimeOffset? reportedAt = null) =>
        _powerDraws[(DeviceName.Parse(device), phase)] = new DevicePowerSample(DeviceName.Parse(device), phase, watts is not null, watts is { } w ? new Watts(w) : null, reportedAt ?? Now);

    /// <summary>Sets a manual override (as the API would) from the next evaluation on.</summary>
    public void Override(string device, OverrideMode mode) =>
        Overrides = new OverrideSnapshot(
            Overrides.Version + 1,
            mode == OverrideMode.Auto
                ? Overrides.Devices.Remove(DeviceName.Parse(device))
                : Overrides.Devices.SetItem(DeviceName.Parse(device), new DeviceOverride(mode, Now, OverrideSource.Api)));

    /// <summary>An invalid (or, with <paramref name="absence"/>, missing) document from the next evaluation on; true restores the valid one.</summary>
    public void ConfigurationValid(bool valid, SetupAbsence absence = SetupAbsence.Invalid)
    {
        _configurationValid = valid;
        _setupAbsence = absence;
    }

    public ProtectionInputs Inputs()
    {
        var phases = Setup.Phases.Select(p => _phases.TryGetValue(p, out var entry)
            ? entry
            : new PhaseProtection(p, false, ProtectionInvalidReason.CurrentAwaiting, null));
        return _configurationValid
            ? new ProtectionInputs(++_sequence, Now, Topology(), [.. phases], null, Setup, [.. _states.Values.OrderBy(s => s.Device).ThenBy(s => s.Stage)], null)
            {
                DeviceDraws = [.. _draws.Values.OrderBy(d => d.Device).ThenBy(d => d.Phase)],
                Groups = [.. Setup.Groups.SelectMany(g => g.Phases.Select(p => GroupEntry(g, p)))],
            }
            : new ProtectionInputs(
                ++_sequence,
                Now,
                null,
                [.. Setup.Phases.Select(p => new PhaseProtection(p, false, ProtectionInvalidReason.ConfigurationInvalid, null))],
                null,
                null,
                [],
                _setupAbsence);
    }

    /// <summary>A circuit group's sub-meter reading on a phase (null: not valid), as the signal layer would supply it.</summary>
    public void SampleGroup(string group, Phase phase, double? amps, DateTimeOffset? reportedAt = null)
    {
        var key = (Switchboard.Domain.Connection.GroupName.Parse(group), phase);
        _groupReadings[key] = amps is { } a
            ? new GroupProtection(key.Item1, phase, true, null, new CurrentReading(new Amperes(a), new Amperes(Math.Abs(a)), FlowDirection.Import, DirectionSource.SignedSignal, reportedAt ?? Now), false)
            : new GroupProtection(key.Item1, phase, false, ProtectionInvalidReason.CurrentStale, null, false);
    }

    /// <summary>The energy counters of the hourly balance as the signal layer would hand them over; null leaves the balance unmapped (and so off).</summary>
    public EnergyInputs? Energy { get; set; }

    /// <summary>The balance of the running hour as the monitor last published it; null before the first reading.</summary>
    public HourlyBalanceStatus? Balance { get; set; }

    /// <summary>The price signals of the store cycle as the signal layer would hand them over; null leaves a dynamic price at its fallback.</summary>
    public Switchboard.Domain.Economics.PriceInputs? Prices { get; set; }

    public DispatchInputs DispatchInputs() =>
        new(_sequence, Now, [.. Setup.Phases.Select(p => _power.TryGetValue(p, out var entry) ? entry : new PhaseDispatch(p, false, DispatchInvalidReason.PowerNotFresh, null, null, null, null))])
        {
            DevicePowers = [.. _powerDraws.Values.OrderBy(d => d.Device).ThenBy(d => d.Phase)],
            Energy = Energy,
        };

    public GuardEvaluation Evaluate(GuardTrigger trigger = GuardTrigger.Snapshot, bool withInputs = true)
    {
        var protection = withInputs ? Inputs() : null;
        var dispatchInputs = DispatchInputs();
        var controls = protection is null ? null : new ControlInputs(protection, dispatchInputs, _configurationValid ? DispatchSetup : null);
        Lockouts.Observe(protection?.Setup ?? Engine.Setup, protection?.DeviceStates ?? [], Now);
        Gate.BeginStep(controls, Now);
        var evaluation = Engine.Evaluate(new GuardInput(trigger, protection, KillSwitch, Mode), Now);
        DispatchEvaluation? dispatch = null;
        if (_dispatch)
        {
            dispatch = DispatchEngine.Evaluate(new DispatchInput(dispatchInputs, controls?.DispatchSetup, evaluation, Targets, Overrides, Lockouts, Mode) { Balance = Balance, Prices = Prices }, Now);
            Dispatches.Add(dispatch);
        }

        Evaluations.Add(evaluation);
        Intents.AddRange(evaluation.Intents);
        Timeline.AddRange(evaluation.Intents.Select(i => (Now, i)));
        Events.AddRange(evaluation.Events);
        Decisions.AddRange(Gate.Apply(evaluation, dispatch, Overrides, Mode, controls, Now));
        return evaluation;
    }

    /// <summary>The next deadline the runner would arm: the earliest of the guard's and the gate's.</summary>
    public DateTimeOffset? NextDeadline()
    {
        var guard = Engine.NextDeadline(Now);
        var gate = Gate.NextDeadline(Now);
        return guard is { } g && gate is { } e ? (g < e ? g : e) : guard ?? gate;
    }

    /// <summary>Advances to <paramref name="end"/>, evaluating at every deadline on the way (as the runner's timer does).</summary>
    public void RunUntil(DateTimeOffset end)
    {
        while (NextDeadline() is { } deadline && deadline <= end)
        {
            Time.Advance(deadline - Now);
            Evaluate(GuardTrigger.Tick);
        }

        if (end > Now)
        {
            Time.Advance(end - Now);
        }
    }

    public void RunFor(TimeSpan duration) => RunUntil(Now + duration);

    /// <summary>Repeats the phase's current every <paramref name="every"/> (a meter polling a steady value) until <paramref name="end"/>, with ticks in between.</summary>
    public void Hold(Phase phase, double amps, TimeSpan every, DateTimeOffset end, FlowDirection direction = FlowDirection.Import)
    {
        while (Now + every <= end)
        {
            RunUntil(Now + every);
            Sample(phase, amps, direction);
        }

        RunUntil(end);
    }

    /// <summary>Every phase polled every <paramref name="every"/> with the currents <paramref name="currents"/> returns at each poll, until <paramref name="end"/>.</summary>
    public void Poll(TimeSpan every, DateTimeOffset end, Func<Phase, double> currents)
    {
        while (Now + every <= end)
        {
            RunUntil(Now + every);
            foreach (var phase in Setup.Phases)
            {
                Sample(phase, currents(phase), evaluate: false);
            }

            Evaluate();
        }

        RunUntil(end);
    }

    public PhaseGuardStatus PhaseStatus(Phase phase) => Last.Status.Phases.Single(p => p.Phase == phase);

    public DeviceGuardStatus DeviceStatus(string device) => Last.Status.Devices.Single(d => d.Id.Value == device);

    public IEnumerable<GuardIntent> IntentsFor(string device) => Intents.Where(i => i.Device.Value == device);

    public IEnumerable<GuardIntent> Sheds => Intents.Where(i => i.Target is GuardTarget.Off or GuardTarget.AtMinimum);

    /// <summary>The ledger's view of a device (its intended level, deferral, window, unresponsive flag).</summary>
    public LedgerEntry? Ledger(string device) => Targets.Entry(DeviceName.Parse(device));

    /// <summary>Device decisions about one device, in order.</summary>
    public IEnumerable<Decision> DecisionsFor(string device) => Decisions.Where(d => d.Device?.Value == device);

    /// <summary>The envelope entry of a main phase in the last evaluation.</summary>
    public EnvelopeEntry Envelope(Phase phase) => Last.Envelope.For(ConstraintKey.Main(phase))!;

    private readonly Dictionary<(Switchboard.Domain.Connection.GroupName, Phase), GroupProtection> _groupReadings = [];

    private GroupProtection GroupEntry(Switchboard.Domain.Connection.CircuitGroupSpec group, Phase phase) =>
        _groupReadings.TryGetValue((group.Id, phase), out var reading) ? reading
        : group.SubMetered ? new GroupProtection(group.Id, phase, false, ProtectionInvalidReason.CurrentAwaiting, null, false)
        : new GroupProtection(group.Id, phase, true, null, null, true);

    private Switchboard.Domain.Connection.Topology? Topology() => Configuration.Connection.Topology;
}

/// <summary>Keeps every recorded decision in memory; can be told to fail like a full disk.</summary>
internal sealed class RecordingDecisionRecorder : IDecisionRecorder
{
    private readonly ConcurrentQueue<Decision> _decisions = new();

    public IReadOnlyList<Decision> Decisions => [.. _decisions];

    public bool Fail { get; set; }

    public DecisionLogStatus Status => new(!Fail, Fail ? "disk full" : null, 0);

    public void Record(IReadOnlyList<Decision> decisions)
    {
        if (Fail)
        {
            throw new IOException("No space left on device.");
        }

        foreach (var decision in decisions)
        {
            _decisions.Enqueue(decision);
        }
    }

    public DecisionPage Query(DecisionQuery query) =>
        new([.. _decisions.Reverse().Where(d => query.Reason is null || d.Reason == query.Reason).Take(query.Limit)], null, !Fail);
}

/// <summary>Device declarations for guard tests: the reference installation's loads with neutral entity names.</summary>
internal static class TestDevices
{
    /// <summary>Three 1.5 kW heater elements (6.6 A), one per phase, value 0.35; a 6–20 A charger on L1, value 2.03.</summary>
    public const string Reference = """
          - id: heater_l1
            type: onoff
            actuator: switch.heater_1
            phase: L1
            rated_current_a: 6.6
            rated_power_w: 1500
            value_per_kwh: 0.35
          - id: heater_l2
            type: onoff
            actuator: switch.heater_2
            phase: L2
            rated_current_a: 6.6
            rated_power_w: 1500
            value_per_kwh: 0.35
          - id: heater_l3
            type: onoff
            actuator: switch.heater_3
            phase: L3
            rated_current_a: 6.6
            rated_power_w: 1500
            value_per_kwh: 0.35
          - id: ev_charger
            type: modulating
            actuator: number.charger_current
            phase: L1
            min: 6
            max: 20
            step: 1
            value_per_kwh: 2.03
        """;

    /// <summary>The reference devices with the charger's own draw current on L1 (what relief may never exceed).</summary>
    public static string ReferenceWithChargerDraw => Reference.ReplaceLineEndings("\n") + "\n    draw: { current: sensor.charger_draw_current, power: sensor.charger_draw_power, max_age: 60s }";

    /// <summary>
    /// A three-phase document with <paramref name="devices"/> (list items, indent 2) under devices:.
    /// Devices default to <paramref name="defaultStart"/>: headroom, the Stage 3 dispatch these
    /// scenarios are about; a test of start: restore_only passes "restore_only", or null for the
    /// shipped default.
    /// </summary>
    public static string Document(string devices = Reference, string control = "", string characteristic = "B", string extra = "", string? defaultStart = "headroom") =>
        TestConfigurations.ThreePhase(control: WithDefaultStart(control, defaultStart), characteristic: characteristic).TrimEnd('\n') + "\ndevices:\n" + devices.ReplaceLineEndings("\n").TrimEnd('\n') + "\n" + extra.ReplaceLineEndings("\n");

    /// <summary>The control block with devices.default_start merged in (a devices: section the test wrote keeps its keys).</summary>
    public static string WithDefaultStart(string control, string? defaultStart)
    {
        if (defaultStart is null)
        {
            return control;
        }

        var text = control.ReplaceLineEndings("\n");
        if (text.Length > 0 && !text.EndsWith('\n'))
        {
            text += "\n";
        }

        var line = $"    default_start: {defaultStart}\n";
        return text.StartsWith("  devices:\n", StringComparison.Ordinal) || text.Contains("\n  devices:\n", StringComparison.Ordinal)
            ? text.Replace("  devices:\n", "  devices:\n" + line, StringComparison.Ordinal)
            : text + "  devices:\n" + line;
    }
}

/// <summary>
/// A command port for tests: it records what the gate asked for and answers at once (acknowledged
/// unless <see cref="Answer"/> says otherwise), or holds its answers until <see cref="Release"/>, so
/// a test can drive failures, timeouts, unsent commands and late answers on the virtual clock.
/// </summary>
internal sealed class RecordingDeviceCommandPort : IDeviceCommandPort
{
    private readonly Queue<CommandResult> _results = new();
    private readonly List<CommandResult> _held = [];
    private long _sequence;

    public List<string> Commands { get; } = [];

    public List<DeviceCommand> Submitted { get; } = [];

    /// <summary>How each command is answered; acknowledged by default.</summary>
    public Func<DeviceCommand, (CommandOutcome Outcome, CommandNotSentReason? NotSent)> Answer { get; set; } = _ => (CommandOutcome.Acknowledged, null);

    /// <summary>Answers are kept back until <see cref="Release"/> (a slow link).</summary>
    public bool Hold { get; set; }

    /// <summary>Called for every submitted command: a test makes the device report the commanded level (a relay that follows).</summary>
    public Action<DeviceCommand>? OnSubmit { get; set; }

    public long LinkGeneration { get; set; } = 1;

    /// <summary>Flushes seen: the end of each step's submissions.</summary>
    public int Flushes { get; private set; }

    public CommandTicket Submit(DeviceCommand command)
    {
        var ticket = new CommandTicket(++_sequence);
        Submitted.Add(command);
        Commands.Add(command.Kind switch
        {
            DeviceCommandKind.Relay => $"{command.Device} relay {command.Stage} {(command.On ? "on" : "off")}",
            DeviceCommandKind.Setpoint => $"{command.Device} setpoint {command.Setpoint!.Value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            _ => $"{command.Device} enabled {(command.On ? "on" : "off")}",
        });
        OnSubmit?.Invoke(command);
        var (outcome, notSent) = Answer(command);
        var result = new CommandResult(ticket, command.Device, outcome, notSent, DateTimeOffset.UnixEpoch, outcome == CommandOutcome.Acknowledged ? null : "test");
        if (Hold)
        {
            _held.Add(result);
        }
        else
        {
            _results.Enqueue(result);
        }

        return ticket;
    }

    public void Flush() => Flushes++;

    public bool TryTakeResult(out CommandResult result) => _results.TryDequeue(out result!);

    /// <summary>Lets the held answers through (the next step takes them).</summary>
    public void Release()
    {
        foreach (var result in _held)
        {
            _results.Enqueue(result);
        }

        _held.Clear();
    }
}
