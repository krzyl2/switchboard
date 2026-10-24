using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Configuration;
using Switchboard.Application.Decisions;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Tests.Support;

/// <summary>
/// Drives the guard engine and the effector gate directly on a virtual clock, with protection
/// inputs written by the test: phase currents with their report times and directions, and device
/// states. The setup comes from a real configuration document through the real parser, defaults
/// and factory. Ticks happen exactly at the engine's deadlines, as the runner arms them.
/// </summary>
internal sealed class GuardScenario
{
    /// <summary>A cycle boundary (multiple of 30 s since the Unix epoch).</summary>
    public static readonly DateTimeOffset Start = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly Dictionary<Phase, PhaseProtection> _phases = [];
    private readonly Dictionary<(DeviceName, int), DeviceStateSample> _states = [];
    private readonly DecisionIdGenerator _ids = new("test");
    private bool _configurationValid = true;
    private SetupAbsence _setupAbsence = SetupAbsence.Invalid;
    private long _sequence;

    /// <param name="settledLockouts">
    /// True (the default) starts every lockout an hour before <paramref name="start"/>, as in a
    /// process that has been running; false starts them at the first evaluation, as after a restart.
    /// </param>
    public GuardScenario(string yaml, DateTimeOffset? start = null, ModeStatus? mode = null, bool settledLockouts = true)
    {
        Time = new SteppableTime(start ?? Start);
        Configuration = TestConfigurations.Valid(yaml).Configuration!;
        Setup = ProtectionSetupFactory.Build(Configuration);
        Engine = new GuardEngine(Lockouts, NullLogger<GuardEngine>.Instance);
        Gate = new EffectorGate(Lockouts, _ids, NullLogger<EffectorGate>.Instance);
        Mode = mode ?? ModeState.Resolve(OperatingMode.Shadow, OperatingMode.Shadow);
        // Every device on (the charger at its maximum) and every phase at a quiet 5 A, so a test
        // changes only what it is about; nothing is evaluated until the test asks.
        foreach (var device in Setup.Devices)
        {
            foreach (var stage in device.Stages)
            {
                SetState(device.Id, stage.Index, device.Type == DeviceType.Modulating ? new SetpointApplied(device.Range!.Max) : new RelayApplied(true));
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

    /// <summary>A configuration reload: the next evaluation sees the new setup.</summary>
    public void Reconfigure(string yaml)
    {
        Configuration = TestConfigurations.Valid(yaml).Configuration!;
        Setup = ProtectionSetupFactory.Build(Configuration);
    }

    public LockoutClocks Lockouts { get; } = new();

    public GuardEngine Engine { get; }

    public EffectorGate Gate { get; }

    public ModeStatus Mode { get; set; }

    public KillSwitchStatus KillSwitch { get; set; } = KillSwitchStatus.Off;

    public DateTimeOffset Now => Time.GetUtcNow();

    public List<GuardEvaluation> Evaluations { get; } = [];

    public List<GuardIntent> Intents { get; } = [];

    /// <summary>Every intent with the time of the evaluation that produced it.</summary>
    public List<(DateTimeOffset At, GuardIntent Intent)> Timeline { get; } = [];

    public List<GuardEvent> Events { get; } = [];

    public List<Decision> Decisions { get; } = [];

    public GuardEvaluation Last => Evaluations[^1];

    /// <summary>Breaker rating in A (the reference installation: 25 A).</summary>
    public double Rating => Setup.Breaker.Rating.Value;

    /// <summary>A new current sample on a phase, reported now (or at <paramref name="reportedAt"/>); evaluates.</summary>
    public GuardEvaluation Sample(Phase phase, double amps, FlowDirection direction = FlowDirection.Import, DateTimeOffset? reportedAt = null, bool evaluate = true)
    {
        var signed = direction == FlowDirection.Export ? -Math.Abs(amps) : Math.Abs(amps);
        var reading = new CurrentReading(new Amperes(signed), new Amperes(Math.Abs(amps)), direction, DirectionSource.SignedSignal, reportedAt ?? Now);
        _phases[phase] = new PhaseProtection(phase, true, null, reading);
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

    public void SetState(DeviceName device, int stage, AppliedState? applied, DeviceStateInvalidReason? invalid = null) =>
        _states[(device, stage)] = new DeviceStateSample(device, stage, applied is not null, applied is null ? invalid ?? DeviceStateInvalidReason.Stale : null, applied, Now);

    public void SetState(string device, AppliedState? applied, int stage = 1, DeviceStateInvalidReason? invalid = null) =>
        SetState(DeviceName.Parse(device), stage, applied, invalid);

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

    public GuardEvaluation Evaluate(GuardTrigger trigger = GuardTrigger.Snapshot, bool withInputs = true)
    {
        var protection = withInputs ? Inputs() : null;
        Lockouts.Observe(protection?.Setup ?? Engine.Setup, protection?.DeviceStates ?? [], Now);
        var evaluation = Engine.Evaluate(new GuardInput(trigger, protection, KillSwitch, Mode), Now);
        Evaluations.Add(evaluation);
        Intents.AddRange(evaluation.Intents);
        Timeline.AddRange(evaluation.Intents.Select(i => (Now, i)));
        Events.AddRange(evaluation.Events);
        Decisions.AddRange(Gate.Apply(evaluation, Mode, Now));
        return evaluation;
    }

    /// <summary>Advances to <paramref name="end"/>, evaluating at every engine deadline on the way (as the runner's timer does).</summary>
    public void RunUntil(DateTimeOffset end)
    {
        while (Engine.NextDeadline(Now) is { } deadline && deadline <= end)
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

    public PhaseGuardStatus PhaseStatus(Phase phase) => Last.Status.Phases.Single(p => p.Phase == phase);

    public DeviceGuardStatus DeviceStatus(string device) => Last.Status.Devices.Single(d => d.Id.Value == device);

    public IEnumerable<GuardIntent> IntentsFor(string device) => Intents.Where(i => i.Device.Value == device);

    public IEnumerable<GuardIntent> Sheds => Intents.Where(i => i.Target is GuardTarget.Off or GuardTarget.AtMinimum);

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

    /// <summary>A three-phase document with <paramref name="devices"/> (list items, indent 2) under devices:.</summary>
    public static string Document(string devices = Reference, string control = "", string characteristic = "B") =>
        TestConfigurations.ThreePhase(control: control, characteristic: characteristic).TrimEnd('\n') + "\ndevices:\n" + devices.ReplaceLineEndings("\n").TrimEnd('\n') + "\n";
}
