using System.Collections.Immutable;
using Switchboard.Application.Effector;
using Switchboard.Application.Modes;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Guard;

/// <summary>The estimated current: measured plus the counted in-flight changes, with its multiple of In.</summary>
internal readonly record struct EffectiveCurrent(Amperes Signed, double Multiple, Amperes InFlight);

/// <summary>How the load model sees one device right now: applied state, resolved target, and whether its change counts.</summary>
internal readonly record struct DeviceView(DeviceSpec Spec, DeviceLevel? Reported, DeviceLevel Target, bool Counts, InFlightEntry? Ledger);

/// <summary>
/// One evaluation's view of the load: the setup, the latest device states and draws, the in-flight
/// ledger, the holds, the time. It answers what a constraint carries (measured plus every counted
/// in-flight change, <see cref="LoadModel"/>), what a circuit group without sub-meter carries, and
/// which devices a reaction may shed, in what state. It reads the guards and the holds as they are at
/// the moment of the call, so holds made earlier in the same evaluation count at once; it never changes
/// anything.
/// </summary>
internal sealed class LoadEstimator
{
    private readonly PhaseGuards _phases;
    private readonly HoldLedger _holds;
    private readonly IInFlightView _inFlight;
    private readonly ILockoutView _lockouts;
    private readonly ProtectionInputs? _protection;
    private readonly ImmutableDictionary<(DeviceName, int), DeviceStateSample> _samples;
    private readonly ImmutableDictionary<(DeviceName, Phase), DeviceDrawSample> _draws;
    private readonly ImmutableDictionary<DeviceName, int> _ranks;
    private readonly DeviceStateInvalidReason _absent;
    private readonly bool _active;

    public LoadEstimator(PhaseGuards phases, HoldLedger holds, IInFlightView inFlight, ILockoutView lockouts, ProtectionSetup setup, ProtectionInputs? protection, DateTimeOffset now, ModeStatus mode)
    {
        _phases = phases;
        _holds = holds;
        _inFlight = inFlight;
        _lockouts = lockouts;
        _protection = protection;
        Setup = setup;
        Now = now;
        _active = mode.Effective == OperatingMode.Active;
        var current = protection is { Setup: not null } && !protection.DeviceStates.IsDefault;
        _samples = current
            ? protection!.DeviceStates.ToImmutableDictionary(s => (s.Device, s.Stage))
            : ImmutableDictionary<(DeviceName, int), DeviceStateSample>.Empty;
        _draws = current && !protection!.DeviceDraws.IsDefault
            ? protection.DeviceDraws.ToImmutableDictionary(d => (d.Device, d.Phase))
            : ImmutableDictionary<(DeviceName, Phase), DeviceDrawSample>.Empty;
        _absent = current ? DeviceStateInvalidReason.Missing : DeviceStateInvalidReason.ConfigurationInvalid;
        _ranks = ShedOrder.Rank(setup.Devices).Select((d, i) => (d, i)).ToImmutableDictionary(x => x.d, x => x.i + 1);
    }

    public ProtectionSetup Setup { get; }

    public DateTimeOffset Now { get; }

    public DeviceStateSample? Sample(DeviceName device, int stage) => _samples.GetValueOrDefault((device, stage));

    public DeviceDrawSample? Draw(DeviceName device, Phase phase) => _draws.GetValueOrDefault((device, phase));

    /// <summary>The device's place in the shed order (1 = shed first); null when it has none.</summary>
    public int? Rank(DeviceName device) => _ranks.TryGetValue(device, out var rank) ? rank : null;

    /// <summary>Why a stage's state is unknown; null when it is fresh.</summary>
    public DeviceStateInvalidReason? InvalidReason(DeviceName device, int stage) =>
        Sample(device, stage) is { } sample ? sample.InvalidReason : _absent;

    public AppliedState? Applied(DeviceName device, int stage) => Sample(device, stage) is { Fresh: true } s ? s.Applied : null;

    /// <summary>Every stage state (and a stop actuator's) is fresh.</summary>
    public bool StateFresh(DeviceSpec device) =>
        device.Stages.All(s => Sample(device.Id, s.Index) is { Fresh: true })
        && (device is not SetpointSpec { HasStopActuator: true } || Sample(device.Id, DeviceStateSample.StopStage) is { Fresh: true });

    public Amperes ReserveThreshold(ConstraintKey key) =>
        key.Group is { } group ? Setup.Group(group)!.ReserveThreshold(key.Phase) : Setup.ReserveThreshold(key.Phase);

    /// <summary>
    /// A device as the load model sees it: its applied state, its target (the ledger's, else the
    /// applied state, with protection's holds applied, including those made earlier in this
    /// evaluation) and whether the change counts: in shadow mode while target and applied state
    /// differ, in active mode while its command window is open (then measurement rules).
    /// </summary>
    public DeviceView View(DeviceSpec device)
    {
        var reported = DeviceLevels.Reported(device, stage => Sample(device.Id, stage));
        var ledger = _inFlight.For(device.Id);
        var target = DeviceLevels.Cap(device, ledger?.Target ?? reported ?? DeviceLevels.Max(device), LimitsOf(device.Id));
        var differs = !target.Equals(reported ?? DeviceLevels.Max(device));
        var counts = !_active ? differs : ledger is null || !target.Equals(ledger.Target) ? differs : ledger.WindowOpen;
        return new DeviceView(device, reported, target, counts, ledger);
    }

    /// <summary>The device's share of the measured current on a phase, or (an estimated group) its worst-case term.</summary>
    public Amperes Basis(DeviceView view, Phase phase, bool groupTerm)
    {
        if (_active && view.Ledger is { WindowOpen: true } ledger && view.Target.Equals(ledger.Target))
        {
            var captured = groupTerm ? ledger.GroupTermAtCommand : ledger.ContributionAtCommand;
            if (captured.TryGetValue(phase, out var atCommand))
            {
                return atCommand;
            }
        }

        var draw = Draw(view.Spec.Id, phase);
        return groupTerm ? LoadModel.GroupTerm(view.Spec, phase, view.Reported, draw) : LoadModel.Contribution(view.Spec, phase, view.Reported, draw);
    }

    /// <summary>
    /// The counted change on a phase. While an active command's window is open it is the change
    /// from the level the command started at, against the contribution captured then: the relay
    /// may report "on" before the meter sees its load, and the estimate must not drop it early.
    /// </summary>
    public Amperes Delta(DeviceView view, Phase phase, bool groupTerm)
    {
        if (!view.Counts)
        {
            return Amperes.Zero;
        }

        var from = _active && view.Ledger is { WindowOpen: true, From: { } started } ledger && view.Target.Equals(ledger.Target) ? started : view.Reported;
        return LoadModel.InFlightDelta(view.Spec, phase, view.Target, from, Basis(view, phase, groupTerm));
    }

    /// <summary>A circuit group without a sub-meter: its current is the worst case of its members.</summary>
    public bool Estimated(ConstraintKey key) => key.Group is { } group && Setup.Group(group) is { SubMetered: false };

    private IEnumerable<DeviceSpec> DevicesOn(ConstraintKey key) =>
        Setup.Devices.Where(d => d.IsOn(key.Phase) && (key.Group is not { } group || Setup.Group(group)!.Members.Contains(d.Id)));

    /// <summary>
    /// A circuit group phase's reading: the sub-meter when there is one; otherwise the worst-case
    /// sum of its members (fresh draw, relay rated when on or unknown, setpoint or its maximum
    /// when unknown), a new sample whenever the main meter or a member input reports.
    /// </summary>
    public (bool Valid, CurrentReading? Reading, ProtectionInvalidReason? Reason) GroupReading(CircuitGroupSpec group, Phase phase, bool configurationValid)
    {
        if (!configurationValid)
        {
            return (false, null, ProtectionInvalidReason.ConfigurationInvalid);
        }

        var entry = _protection?.Groups.IsDefault == false ? _protection.Groups.FirstOrDefault(g => g.Group == group.Id && g.Phase == phase) : null;
        if (group.SubMetered)
        {
            return entry is { DataValid: true, Current: { } current } ? (true, current, null) : (false, null, entry?.InvalidReason ?? ProtectionInvalidReason.CurrentMissing);
        }

        var main = _protection?.For(phase);
        if (entry is not { DataValid: true } || main is not { DataValid: true, Current: { } mainReading })
        {
            return (false, null, entry?.InvalidReason ?? main?.InvalidReason ?? ProtectionInvalidReason.CurrentAwaiting);
        }

        var total = Amperes.Zero;
        var reportedAt = mainReading.ReportedAt;
        foreach (var device in DevicesOn(new ConstraintKey(group.Id, phase)))
        {
            var reported = DeviceLevels.Reported(device, stage => Sample(device.Id, stage));
            total += LoadModel.GroupTerm(device, phase, reported, Draw(device.Id, phase));
            foreach (var at in device.Stages.Select(s => Sample(device.Id, s.Index)?.ReportedAt).Append(Draw(device.Id, phase)?.ReportedAt))
            {
                if (at is { } time && time > reportedAt && time <= Now)
                {
                    reportedAt = time;
                }
            }
        }

        return (true, new CurrentReading(total, total, FlowDirection.Import, DirectionSource.SignedSignal, reportedAt), null);
    }

    /// <summary>
    /// The estimate I_est on a constraint: measured plus every counted in-flight change of the
    /// devices on it. A would-start adds its ceiling, a would-stop relieves at most what the
    /// device really draws (a charger at 20 A with no car relieves nothing), and the holds made
    /// earlier in this evaluation count at once. On an exporting phase the multiple is the larger
    /// of measured and estimated magnitude: shedding there raises the current.
    /// </summary>
    public EffectiveCurrent Effective(ConstraintKey key)
    {
        var guard = _phases[key];
        if (guard.Reading is not { } reading)
        {
            return new EffectiveCurrent(Amperes.Zero, 0, Amperes.Zero);
        }

        var estimated = Estimated(key);
        var delta = Amperes.Zero;
        foreach (var device in DevicesOn(key))
        {
            delta += Delta(View(device), key.Phase, estimated);
        }

        var rating = guard.Profile.Rating.Value;
        var signed = reading.Signed.Value + delta.Value;
        return reading.Signed.Value >= 0
            ? new EffectiveCurrent(new Amperes(Math.Max(0, signed)), Math.Max(0, signed) / rating, delta)
            : new EffectiveCurrent(new Amperes(signed), Math.Max(reading.Magnitude.Value, Math.Abs(signed)) / rating, delta);
    }

    /// <summary>
    /// Shed candidates from the same view as the estimate: a device whose change counts is seen at
    /// its target (a would-started heater is shed before a running device worth more), any other
    /// at its applied state (unknown counts as on at full rating).
    /// </summary>
    public IReadOnlyList<ShedCandidate> Candidates(ConstraintKey key) =>
    [
        .. DevicesOn(key).Select(d =>
        {
            var view = View(d);
            ImmutableArray<AppliedState?> applied = view.Counts
                ? d is SetpointSpec
                    ? [new SetpointApplied(view.Target is SetpointLevel { Enabled: true } target ? target.Setpoint : Amperes.Zero)]
                    : [.. (view.Target is RelayLevel { Relays.IsDefault: false } targetRelays ? targetRelays.Relays : []).Select(on => (AppliedState?)new RelayApplied(on))]
                : d is SetpointSpec && view.Reported is SetpointLevel { Enabled: false }
                    ? [new SetpointApplied(Amperes.Zero)]
                    : [.. d.Stages.Select(s => Applied(d.Id, s.Index))];
            // A fresh draw tells a step that relieves nothing (an idle charger at a high
            // setpoint); a device seen at its target is relieved by its in-flight change instead.
            var draw = !view.Counts && d.HasCurrentDraw(key.Phase) && Draw(d.Id, key.Phase) is { Fresh: true, Current: { } current } ? current : (Amperes?)null;
            return new ShedCandidate(d, applied, _lockouts.UnderMinOn(d.Id, Now), LimitsOf(d.Id)) { Draw = draw };
        }),
    ];

    public ImmutableDictionary<int, StageLimit> LimitsOf(DeviceName device) => _holds.LimitsOf(device);
}
