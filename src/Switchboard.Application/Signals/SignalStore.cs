using System.Collections.Immutable;
using System.Globalization;
using Switchboard.Application.Configuration;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Signals;

/// <param name="ProbeLatencyAllowance">How long a freshness probe may take to answer (the state read timeout).</param>
public sealed record SignalStoreSettings(TimeSpan ProbeLatencyAllowance);

/// <summary>
/// The signal mapping layer's state machine: the only place where entity IDs meet logical signals
/// (SIG-01). Normalizes units and signs (SIG-02), resolves direction (SIG-03), keeps freshness from
/// report times (SIG-05, SIG-06), detects refresh intervals (SIG-07), derives per-phase house load
/// (SIG-08) and runs the balance check (SIG-09). It also reads the devices' applied states (a
/// state declared on_change is fresh while available, with no age limit), their declared draws and
/// the circuit groups' sub-meters. Deterministic: it never reads a clock, every call is handed
/// <c>now</c>, and it is driven by exactly one reader.
/// </summary>
public sealed class SignalStore
{
    private static readonly ImmutableArray<Phase> AllPhases = [Phase.L1, Phase.L2, Phase.L3];

    private readonly SignalStoreSettings _settings;
    private readonly Dictionary<Phase, BalanceState> _balance = AllPhases.ToDictionary(p => p, _ => new BalanceState());
    private SwitchboardConfiguration? _configuration;
    private ImmutableArray<SignalRuntime> _signals = [];
    private Dictionary<EntityId, SignalRuntime> _byEntity = [];
    private Dictionary<SignalName, SignalRuntime> _byName = [];
    private ImmutableArray<TrackedEntity> _tracked = [];
    private ProtectionSetup? _setup;
    private DispatchSetup? _dispatchSetup;
    private ImmutableDictionary<SignalName, ImmutableArray<DeviceName>> _dependents = ImmutableDictionary<SignalName, ImmutableArray<DeviceName>>.Empty;
    private ImmutableArray<(DeviceName Device, int Stage, SignalName Signal, bool Storage)> _deviceStates = [];
    private ImmutableArray<(DeviceName Device, Phase Phase, SignalName Signal)> _currentDraws = [];
    private ImmutableArray<(DeviceName Device, Phase Phase, SignalName Signal)> _powerDraws = [];
    private ImmutableArray<(GroupName Group, Phase Phase, SignalName? Signal)> _groupPhases = [];
    private bool _trackedChanged;
    private bool _resyncPending;
    private long _sequence;
    private long _balanceRejections;
    private long _invalidRejections;

    public SignalStore(SignalStoreSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>The configuration result last applied, valid or not; null before the first one.</summary>
    public ConfigurationResult? ConfigurationResult { get; private set; }

    public SignalSourceStatus Source { get; private set; } = SignalSourceStatus.Disconnected;

    /// <summary>
    /// A valid document replaces the signal set; signals reading the same entity the same way keep
    /// their samples, so editing a comment never blanks protection data. A signal whose reading
    /// changed (kind, unit or sign) re-reads the state it last saw under the new definition, as a new
    /// initial state would deliver it: Home Assistant sends nothing for a state that does not change,
    /// so waiting for the next push could leave the signal awaiting for hours (an inverter asleep all
    /// night). An invalid or missing document clears everything: no signal, no valid protection data,
    /// nothing tracked.
    /// </summary>
    public void ApplyConfiguration(ConfigurationResult result, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(result);
        ConfigurationResult = result;
        if (result.Status != ConfigurationStatus.Valid || result.Configuration is null)
        {
            _configuration = null;
            _signals = [];
            _byEntity = [];
            _byName = [];
            _setup = null;
            _dispatchSetup = null;
            _dependents = ImmutableDictionary<SignalName, ImmutableArray<DeviceName>>.Empty;
            _deviceStates = [];
            _currentDraws = [];
            _powerDraws = [];
            _groupPhases = [];
            SetTracked([]);
            return;
        }

        var previous = _byEntity;
        var runtimes = ImmutableArray.CreateBuilder<SignalRuntime>(result.Configuration.Signals.Length);
        var reread = new List<(SignalRuntime Runtime, RawSample Raw)>();
        foreach (var definition in result.Configuration.Signals)
        {
            if (previous.TryGetValue(definition.Entity, out var kept) && kept.ReadsLike(definition))
            {
                kept.Definition = definition;
                runtimes.Add(kept);
            }
            else
            {
                var runtime = new SignalRuntime(definition);
                runtimes.Add(runtime);
                if (kept?.Raw is { } raw)
                {
                    reread.Add((runtime, raw));
                }
            }
        }

        var configuration = result.Configuration;
        _configuration = configuration;
        _setup = ProtectionSetupFactory.Build(configuration);
        _dispatchSetup = DispatchSetupFactory.Build(configuration);
        _dependents = Dependents(configuration);
        _deviceStates =
        [
            .. configuration.Devices.SelectMany(d => d.Stages
                .Select(s => (d.Id, s.Index, s.StateSignal, d.Type == DeviceType.Storage))
                .Concat(d.StopSignal is { } stop ? [(d.Id, DeviceStateSample.StopStage, stop, false)] : [])),
        ];
        _currentDraws = [.. configuration.Devices.Where(d => d.Draw is not null).SelectMany(d => d.Draw!.Current.OrderBy(c => c.Key).Select(c => (d.Id, c.Key, c.Value)))];
        _powerDraws = [.. configuration.Devices.Where(d => d.Draw is not null).SelectMany(d => d.Draw!.Power.OrderBy(c => c.Key).Select(c => (d.Id, c.Key, c.Value)))];
        _groupPhases = [.. configuration.Groups.SelectMany(g => g.Phases.Select(p => (g.Id, p, g.Current.TryGetValue(p, out var name) ? name : (SignalName?)null)))];
        _signals = runtimes.ToImmutable();
        _byEntity = _signals.ToDictionary(s => s.Definition.Entity);
        _byName = _signals.ToDictionary(s => s.Definition.Name);
        SetTracked([.. _signals.Select(s => new TrackedEntity(s.Definition.Entity, s.Definition.Unit)).OrderBy(t => t.Entity)]);

        // After the new set is in place, so the balance check and direction see the new definitions.
        // The report time stays the one Home Assistant gave: re-reading never makes a sample younger.
        foreach (var (runtime, raw) in reread)
        {
            ApplyUpdate(runtime, new EntityUpdate(runtime.Definition.Entity, raw.State, raw.Unit, raw.LastUpdated, raw.Reported), BatchOrigin.Probe, now);
            runtime.Raw = runtime.Raw! with { HaReported = raw.HaReported };
        }
    }

    /// <summary>
    /// The clock stepped back (an NTP step, a restored VM): every held report time now lies in the
    /// future, so held values would look fresh while newer reports, stamped before them, are dropped
    /// as late. Everything is forgotten and every signal is read again (see <see cref="TakeDueProbes"/>).
    /// </summary>
    public void ForgetSamplesAfterClockStep()
    {
        foreach (var signal in _signals)
        {
            signal.MarkAbsent(Presence.Awaiting);
        }

        _resyncPending = true;
    }

    public void ApplySourceStatus(SignalSourceStatus status)
    {
        Source = status;
        if (status != SignalSourceStatus.Connected)
        {
            foreach (var signal in _signals)
            {
                signal.Refresh.Break();
            }
        }
    }

    public void Apply(EntityUpdateBatch batch, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (_configuration is null)
        {
            return;
        }

        foreach (var id in batch.Removed)
        {
            Find(id)?.MarkAbsent(Presence.Removed);
        }

        foreach (var id in batch.Missing)
        {
            Find(id)?.MarkAbsent(Presence.Missing);
        }

        foreach (var update in batch.Updates)
        {
            if (Find(update.Entity) is { } signal)
            {
                if (batch.Origin == BatchOrigin.Initial)
                {
                    signal.Refresh.Break();
                }

                ApplyUpdate(signal, update, batch.Origin, now);
            }
        }
    }

    /// <summary>Returns the new tracked set once after it changed.</summary>
    public bool TryTakeTrackedChange(out ImmutableArray<TrackedEntity> tracked)
    {
        tracked = _tracked;
        var changed = _trackedChanged;
        _trackedChanged = false;
        return changed;
    }

    /// <summary>
    /// Entities whose freshness should be confirmed now: signals silent for after_fraction × max_age
    /// (a repeated value produces no event, only a newer last_reported), and direction signals a
    /// changed current is still waiting for after the pairing wait. Each is marked as probed.
    /// </summary>
    public IReadOnlyList<EntityId> TakeDueProbes(DateTimeOffset now)
    {
        if (_configuration is null || Source != SignalSourceStatus.Connected)
        {
            return [];
        }

        if (_resyncPending)
        {
            _resyncPending = false;
            foreach (var signal in _signals)
            {
                signal.LastProbeAt = now;
            }

            return [.. _signals.Select(s => s.Definition.Entity)];
        }

        var due = new List<SignalRuntime>();
        foreach (var signal in _signals)
        {
            if (ProbeEligible(signal) && now >= ProbeDue(signal))
            {
                due.Add(signal);
            }
        }

        foreach (var signal in _signals)
        {
            if (signal.Accepted is not { PairingProbeRequested: false } accepted
                || DirectionSignalOf(signal) is not { } direction
                || Classify(direction, now).State != SignalState.Fresh
                || Aligned(signal, direction)
                || now < accepted.ArrivedAt + Direction.PairingWait)
            {
                continue;
            }

            signal.Accepted = accepted with { PairingProbeRequested = true };
            if (ProbeEligible(direction) && !due.Contains(direction))
            {
                due.Add(direction);
            }
        }

        foreach (var signal in due)
        {
            signal.LastProbeAt = now;
        }

        return [.. due.Select(s => s.Definition.Entity)];
    }

    /// <summary>The next moment the snapshot changes without any input: a staleness transition, a probe due, a pairing wait running out.</summary>
    public DateTimeOffset? NextDeadline(DateTimeOffset now)
    {
        if (_configuration is null)
        {
            return null;
        }

        DateTimeOffset? next = null;
        void Consider(DateTimeOffset candidate)
        {
            if (candidate > now && (next is null || candidate < next))
            {
                next = candidate;
            }
        }

        foreach (var signal in _signals)
        {
            if (signal.Accepted is { } accepted && signal.RawStatus != RawStatus.Unavailable && signal.Definition.Freshness == StateFreshness.Age)
            {
                Consider(accepted.Effective + signal.Definition.MaxAge);
            }

            if (Source == SignalSourceStatus.Connected && ProbeEligible(signal))
            {
                Consider(ProbeDue(signal));
            }

            if (signal.Accepted is { PairingProbeRequested: false } pending
                && DirectionSignalOf(signal) is { } direction
                && Classify(direction, now).State == SignalState.Fresh
                && !Aligned(signal, direction))
            {
                Consider(pending.ArrivedAt + Direction.PairingWait);
            }
        }

        return next;
    }

    /// <summary>
    /// Run-time configuration warnings. A repeating value stays fresh only through a probe that
    /// starts after after_fraction × max_age and must answer before max_age, so the rest of max_age
    /// has to cover one refresh plus the probe's own answer time.
    /// </summary>
    public ImmutableArray<ConfigurationIssue> RuntimeWarnings()
    {
        if (_configuration is null)
        {
            return [];
        }

        var parameters = _configuration.Control.Signals;
        var warnings = ImmutableArray.CreateBuilder<ConfigurationIssue>();
        foreach (var signal in _signals)
        {
            if (signal.Definition.Freshness == StateFreshness.OnChange || signal.Refresh.Estimate(parameters.Refresh) is not { } refresh)
            {
                continue;
            }

            var margin = signal.Definition.MaxAge * (1 - parameters.FreshnessProbe.AfterFraction);
            if (margin <= refresh + _settings.ProbeLatencyAllowance)
            {
                warnings.Add(new ConfigurationIssue(
                    ConfigurationIssueCodes.MaxAgeShortForProbe,
                    signal.Definition.MaxAgePath,
                    null,
                    null,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"max_age {signal.Definition.MaxAge.TotalSeconds:0.#} s is too short for a {refresh.TotalSeconds:0.#} s refresh: a value that repeats may turn stale before its last report can be confirmed. Raise max_age.")));
            }
        }

        return warnings.ToImmutable();
    }

    /// <summary>An immutable picture of everything at <paramref name="now"/>; a new instance every call.</summary>
    public SignalSnapshot Snapshot(DateTimeOffset now)
    {
        var sequence = ++_sequence;
        var rejections = new RejectionCounters(_balanceRejections, _invalidRejections);
        if (_configuration is not { } configuration)
        {
            return new SignalSnapshot(
                sequence,
                now,
                false,
                Source,
                null,
                [],
                new ProtectionInputs(
                    sequence,
                    now,
                    null,
                    [.. AllPhases.Select(p => new PhaseProtection(p, false, ProtectionInvalidReason.ConfigurationInvalid, null))],
                    null,
                    null,
                    [],
                    ConfigurationResult?.Status switch
                    {
                        null => SetupAbsence.NotRead,
                        ConfigurationStatus.Missing => SetupAbsence.Missing,
                        _ => SetupAbsence.Invalid,
                    }),
                new DispatchInputs(sequence, now, [.. AllPhases.Select(p => new PhaseDispatch(p, false, DispatchInvalidReason.ConfigurationInvalid, null, null, null, null))]),
                [],
                [],
                rejections);
        }

        var phases = configuration.Connection.Phases;
        var generation = Generation(now);
        return new SignalSnapshot(
            sequence,
            now,
            true,
            Source,
            configuration.Connection,
            Readings(configuration, now),
            Protection(configuration, sequence, now),
            new DispatchInputs(sequence, now, [.. phases.Select(p => Dispatch(p, generation, now))]) { DevicePowers = DevicePowers(now) },
            [.. phases.Select(p => _balance[p].ToPhaseBalance(p))],
            [.. phases.Select(p => Reserve(configuration, p))],
            rejections)
        {
            DispatchSetup = _dispatchSetup,
        };
    }

    private DirectionParameters Direction => _configuration!.Control.Signals.Direction;

    private SignalRuntime? Find(EntityId id) => _byEntity.GetValueOrDefault(id);

    private SignalRuntime? WithRole(SignalRole role, Phase? phase = null) =>
        _signals.FirstOrDefault(s => s.Definition.Role == role && (phase is null || s.Definition.Phase == phase));

    private void SetTracked(ImmutableArray<TrackedEntity> tracked)
    {
        if (!tracked.SequenceEqual(_tracked))
        {
            _tracked = tracked;
            _trackedChanged = true;
        }
    }

    private void ApplyUpdate(SignalRuntime signal, EntityUpdate update, BatchOrigin origin, DateTimeOffset now)
    {
        var reported = update.Reported < now ? update.Reported : now;
        if (signal.Raw is { } raw)
        {
            // Older than what we hold: a late probe answer or a reordered event. It must never
            // revert a newer pushed value.
            if (update.LastUpdated < raw.LastUpdated)
            {
                return;
            }

            // The same state, reported again: freshness only (SIG-06), and only when Home Assistant's
            // own report time moved on. Comparing the clamped time instead would let a clock running
            // ahead in Home Assistant re-clamp the same frozen report to every later arrival.
            if (update.LastUpdated == raw.LastUpdated)
            {
                if (update.Reported <= raw.HaReported)
                {
                    return;
                }

                var confirmed = reported > raw.Reported ? reported : raw.Reported;
                signal.Raw = raw with { Reported = confirmed, HaReported = update.Reported };
                if (signal.RawStatus == RawStatus.Accepted && signal.Accepted is { } held && confirmed > held.Effective)
                {
                    signal.Accepted = held with { Effective = confirmed };
                }

                return;
            }
        }

        var normalized = SignalNormalizer.Normalize(signal.Definition, update.State, update.Unit);
        signal.Presence = Presence.Present;
        signal.Raw = new RawSample(update.State, update.Unit, update.LastUpdated, reported, update.Reported);
        if (normalized.Outcome == NormalizationOutcome.Unavailable)
        {
            signal.RawStatus = RawStatus.Unavailable;
            signal.RawDetail = normalized.Detail;
            signal.Refresh.Break();
            return;
        }

        // Refresh is measured on pushed reports only: an initial state or a probe answer says
        // nothing about how often the source reports.
        if (origin is BatchOrigin.Change or BatchOrigin.Added)
        {
            signal.Refresh.Observe(reported, _configuration!.Control.Signals.Refresh.WindowIntervals);
        }

        if (normalized.Outcome == NormalizationOutcome.Rejected)
        {
            signal.RawStatus = RawStatus.Invalid;
            signal.RawDetail = normalized.Detail;
            signal.RejectedCount++;
            _invalidRejections++;
            return;
        }

        var previous = signal.Accepted;
        signal.Accepted = new AcceptedSample(normalized.Value!, update.LastUpdated, reported, reported, now, PairingProbeRequested: false);
        signal.RawStatus = RawStatus.Accepted;
        signal.RawDetail = SignalDetail.None;

        if (IsBalanceTrigger(signal.Definition.Role) && !BalanceAccepts(signal, now))
        {
            // The sample is rejected, never the decision: the last accepted value stays in use, and
            // ages (see Classify).
            signal.Accepted = previous;
            signal.RawStatus = RawStatus.BalanceRejected;
            signal.RawDetail = SignalDetail.BalanceResidual;
            signal.RejectedCount++;
            _balanceRejections++;
        }
    }

    private static bool IsBalanceTrigger(SignalRole role) =>
        role is SignalRole.PhasePower or SignalRole.InverterPower or SignalRole.InverterPhaseCurrent;

    /// <summary>
    /// SIG-09: r(p) = net import + generation − controlled loads, the controlled loads being the
    /// fresh measured power draws of declared devices on the phase (a model value, such as a heater's
    /// rated power, is never used: a heater whose thermostat opened would then reject every true
    /// sample). Phase current is never an input, so protection data is never touched. Only inputs
    /// reported within max_skew of the triggering sample are compared; a device draw counts when it
    /// was reported within max_skew of the trigger, or has been unchanged since before trigger −
    /// max_skew, and is left out otherwise (leaving a term out can only make a sample pass). Device
    /// draws never trigger the check and are never rejected by it. Only an impossible negative
    /// residual is refused; a positive residual is foreign load, which is normal. Without a measured
    /// generation (no inverter mapped, or an inverter asleep counted as zero) nothing is compared:
    /// an export is then explained by production nobody measures, not by a broken sample.
    /// </summary>
    private bool BalanceAccepts(SignalRuntime trigger, DateTimeOffset now)
    {
        var generation = Generation(now);
        if (generation.InputTimes.IsEmpty)
        {
            return true;
        }

        var configuration = _configuration!;
        var parameters = configuration.Control.Signals.Balance;
        var triggerTime = trigger.Accepted!.SampleReported;
        var phases = trigger.Definition.Role == SignalRole.PhasePower
            ? [trigger.Definition.Phase!.Value]
            : configuration.Connection.Phases;
        var impossible = new List<Phase>();

        foreach (var phase in phases)
        {
            if (WithRole(SignalRole.PhasePower, phase) is not { } power
                || Classify(power, now).State != SignalState.Fresh
                || generation.For(phase) is not { } share)
            {
                continue;
            }

            var balance = _balance[phase];
            var inputs = generation.InputTimes.Append(power.Accepted!.Effective);
            if (inputs.Any(t => (t - triggerTime).Duration() > parameters.MaxSkew))
            {
                balance.SkippedMisaligned++;
                continue;
            }

            var residual = Value(power) + share.Value - ControlledLoad(phase, triggerTime, parameters.MaxSkew, now);
            balance.Evaluations++;
            balance.LastEvaluated = now;
            balance.Residual = new Watts(residual);
            if (residual < -parameters.ResidualThreshold.Value)
            {
                impossible.Add(phase);
            }
        }

        foreach (var phase in impossible)
        {
            _balance[phase].Rejections++;
            _balance[phase].LastRejected = now;
        }

        return impossible.Count == 0;
    }

    /// <summary>
    /// SIG-08: each phase's share of local generation, from primary measurements only. With the
    /// inverter's phase currents the production is split by them; without, it sits on the
    /// inverter's declared phase or is split equally. A mapped inverter that is not fresh makes the
    /// share unknown, never zero, unless it is declared unavailable_as_zero and is asleep: Home
    /// Assistant says unavailable, or it fell silent after reporting no production (SolarEdge
    /// reports 0 W at dusk and nothing more until morning). "unknown" (HA has no value yet, as after
    /// a restart) and silence after a production stay unknown. No inverter mapped means no generation.
    /// </summary>
    private GenerationShares Generation(DateTimeOffset now)
    {
        var phases = _configuration!.Connection.Phases;
        if (WithRole(SignalRole.InverterPower) is not { } inverter)
        {
            return GenerationShares.Known(phases, _ => 0, []);
        }

        var (state, detail) = Classify(inverter, now);
        double production;
        var times = new List<DateTimeOffset>();
        if (state == SignalState.Fresh)
        {
            production = Value(inverter);
            times.Add(inverter.Accepted!.Effective);
        }
        else if (inverter.Definition.UnavailableAsZero
            && ((state == SignalState.Unavailable && detail == SignalDetail.StateUnavailable)
                || (state == SignalState.Stale && inverter.Accepted is not null && Value(inverter) <= 0)))
        {
            return GenerationShares.Known(phases, _ => 0, []);
        }
        else
        {
            return GenerationShares.Unknown;
        }

        var currents = phases.Select(p => WithRole(SignalRole.InverterPhaseCurrent, p)).ToArray();
        if (currents.All(c => c is null))
        {
            return inverter.Definition.Phase is { } own
                ? GenerationShares.Known(phases, p => p == own ? production : 0, times)
                : GenerationShares.Known(phases, _ => production / phases.Length, times);
        }

        if (production == 0)
        {
            return GenerationShares.Known(phases, _ => 0, times);
        }

        if (currents.Any(c => c is null || Classify(c, now).State != SignalState.Fresh))
        {
            return GenerationShares.Unknown;
        }

        var magnitudes = currents.Select(c => Math.Abs(Value(c!))).ToArray();
        times.AddRange(currents.Select(c => c!.Accepted!.Effective));
        var sum = magnitudes.Sum();
        return GenerationShares.Known(
            phases,
            p => sum == 0 ? production / phases.Length : production * magnitudes[phases.IndexOf(p)] / sum,
            times);
    }

    /// <summary>Σ of the fresh device power draws on a phase that are aligned with the trigger (see <see cref="BalanceAccepts"/>).</summary>
    private double ControlledLoad(Phase phase, DateTimeOffset trigger, TimeSpan maxSkew, DateTimeOffset now)
    {
        var total = 0.0;
        foreach (var (_, drawPhase, name) in _powerDraws)
        {
            if (drawPhase != phase || _byName[name] is not { } draw || Classify(draw, now).State != SignalState.Fresh)
            {
                continue;
            }

            var accepted = draw.Accepted!;
            var aligned = (accepted.Effective - trigger).Duration() <= maxSkew || accepted.SampleReported <= trigger - maxSkew;
            if (aligned)
            {
                total += Value(draw);
            }
        }

        return total;
    }

    private PhaseDispatch Dispatch(Phase phase, GenerationShares generation, DateTimeOffset now)
    {
        if (Source != SignalSourceStatus.Connected)
        {
            return new PhaseDispatch(phase, false, DispatchInvalidReason.SourceDisconnected, null, null, null, null);
        }

        var power = WithRole(SignalRole.PhasePower, phase);
        Watts? netImport = power is not null && Classify(power, now).State == SignalState.Fresh ? new Watts(Value(power)) : null;
        var share = generation.For(phase);
        Watts? load = netImport is { } n && share is { } g ? n + g : null;
        var voltageSignal = WithRole(SignalRole.PhaseVoltage, phase);
        Volts? voltage = voltageSignal is not null && Classify(voltageSignal, now).State == SignalState.Fresh
            ? new Volts(Value(voltageSignal))
            : null;

        DispatchInvalidReason? reason = power is null ? DispatchInvalidReason.PowerUnmapped
            : netImport is null ? DispatchInvalidReason.PowerNotFresh
            : share is null ? DispatchInvalidReason.GenerationUnknown
            : null;
        return new PhaseDispatch(phase, reason is null, reason, netImport, share, load, voltage)
        {
            PowerReportedAt = netImport is null ? null : power!.Accepted!.Effective,
        };
    }

    /// <summary>The declared power draws (W), for dispatch; a draw that is not fresh carries no value.</summary>
    private ImmutableArray<DevicePowerSample> DevicePowers(DateTimeOffset now) =>
    [
        .. _powerDraws.Select(d =>
        {
            var signal = _byName[d.Signal];
            var fresh = Source == SignalSourceStatus.Connected && Classify(signal, now).State == SignalState.Fresh;
            return new DevicePowerSample(d.Device, d.Phase, fresh, fresh ? new Watts(Value(signal)) : null, signal.Accepted?.Effective);
        }),
    ];

    /// <summary>The declared current draws (A), for protection; a draw that is not fresh carries no value and is never relief.</summary>
    private ImmutableArray<DeviceDrawSample> DeviceDraws(DateTimeOffset now) =>
    [
        .. _currentDraws.Select(d =>
        {
            var signal = _byName[d.Signal];
            var fresh = Source == SignalSourceStatus.Connected && Classify(signal, now).State == SignalState.Fresh;
            return new DeviceDrawSample(d.Device, d.Phase, fresh, fresh ? new Amperes(Value(signal)) : null, signal.Accepted?.Effective);
        }),
    ];

    /// <summary>
    /// Circuit group phases: a sub-meter is read like a phase current (stale, unavailable or missing
    /// means no valid data, and the guard holds the members off); a group without one is estimated by
    /// the guard from its members, and is valid while the source is connected.
    /// </summary>
    private ImmutableArray<GroupProtection> Groups(DateTimeOffset now) =>
    [
        .. _groupPhases.Select(g =>
        {
            if (g.Signal is not { } name)
            {
                return new GroupProtection(g.Group, g.Phase, Source == SignalSourceStatus.Connected, Source == SignalSourceStatus.Connected ? null : ProtectionInvalidReason.SourceDisconnected, null, true);
            }

            var signal = _byName[name];
            var reason = ProtectionReason(signal, now);
            return new GroupProtection(g.Group, g.Phase, reason is null, reason, reason is null ? Reading(signal, now) : null, false);
        }),
    ];

    private ProtectionInputs Protection(SwitchboardConfiguration configuration, long sequence, DateTimeOffset now)
    {
        var topology = configuration.Connection.Topology;
        TotalProtection? total = null;
        ProtectionInvalidReason? totalReason = null;
        if (topology == Topology.ThreePhaseSummed)
        {
            var totalSignal = WithRole(SignalRole.TotalCurrent)!;
            totalReason = ProtectionReason(totalSignal, now);
            total = new TotalProtection(totalReason is null, totalReason, totalReason is null ? Reading(totalSignal, now) : null);
        }

        var phases = ImmutableArray.CreateBuilder<PhaseProtection>();
        foreach (var phase in configuration.Connection.Phases)
        {
            var current = WithRole(SignalRole.PhaseCurrent, phase);
            var currentReason = current is null ? null : ProtectionReason(current, now);
            var reason = topology == Topology.ThreePhaseSummed ? totalReason ?? currentReason : currentReason;
            var reading = current is not null && currentReason is null ? Reading(current, now) : null;
            phases.Add(new PhaseProtection(phase, reason is null, reason, reading));
        }

        return new ProtectionInputs(sequence, now, topology, phases.ToImmutable(), total, _setup, DeviceStates(now), null)
        {
            DeviceDraws = DeviceDraws(now),
            Groups = Groups(now),
        };
    }

    /// <summary>The applied state of every declared device stage and stop actuator, from its state signal (SIG-05).</summary>
    private ImmutableArray<DeviceStateSample> DeviceStates(DateTimeOffset now)
    {
        var samples = ImmutableArray.CreateBuilder<DeviceStateSample>(_deviceStates.Length);
        foreach (var (device, stage, name, storage) in _deviceStates)
        {
            var signal = _byName[name];
            var (state, detail) = Classify(signal, now);
            samples.Add(DeviceStateInterpreter.Interpret(
                device,
                stage,
                Source,
                state,
                detail,
                state == SignalState.Fresh ? signal.Accepted?.Value : null,
                signal.Accepted?.Effective ?? signal.Raw?.Reported,
                allowNegativeSetpoint: storage));
        }

        return samples.MoveToImmutable();
    }

    /// <summary>
    /// SIG-05: the devices that depend on a signal. A phase current protects every device with a
    /// stage on its phase (the summed current protects all of them); a state signal protects its own
    /// device; a group sub-meter protects the group's members. A draw signal lists its device, which
    /// it does not hold off when stale (protection does not need it): a stale draw is no longer
    /// trusted for relief or increases. Direction and power signals only ever cost cautious mode.
    /// </summary>
    private static ImmutableDictionary<SignalName, ImmutableArray<DeviceName>> Dependents(SwitchboardConfiguration configuration)
    {
        var devices = configuration.Devices;
        var builder = ImmutableDictionary.CreateBuilder<SignalName, ImmutableArray<DeviceName>>();
        foreach (var signal in configuration.Signals)
        {
            ImmutableArray<DeviceName> dependents = signal.Role switch
            {
                SignalRole.PhaseCurrent when configuration.Connection.Topology != Topology.ThreePhaseSummed =>
                    [.. devices.Where(d => d.Stages.Any(s => s.Phases.Contains(signal.Phase!.Value))).Select(d => d.Id)],
                // Protection reads the total only in three_phase_summed; elsewhere it is display-only.
                SignalRole.TotalCurrent when configuration.Connection.Topology == Topology.ThreePhaseSummed => [.. devices.Select(d => d.Id)],
                SignalRole.DeviceState => [.. devices.Where(d => d.Stages.Any(s => s.StateSignal == signal.Name) || d.StopSignal == signal.Name).Select(d => d.Id)],
                SignalRole.DeviceCurrent or SignalRole.DevicePower =>
                    [.. devices.Where(d => d.Draw is { } draw && (draw.Current.ContainsValue(signal.Name) || draw.Power.ContainsValue(signal.Name))).Select(d => d.Id)],
                SignalRole.GroupCurrent => [.. configuration.Groups.Where(g => g.Current.ContainsValue(signal.Name)).SelectMany(g => g.Members)],
                _ => [],
            };
            builder[signal.Name] = dependents;
        }

        return builder.ToImmutable();
    }

    private ProtectionInvalidReason? ProtectionReason(SignalRuntime signal, DateTimeOffset now)
    {
        if (Source != SignalSourceStatus.Connected)
        {
            return ProtectionInvalidReason.SourceDisconnected;
        }

        return Classify(signal, now) switch
        {
            (SignalState.Fresh, _) => null,
            (SignalState.Stale, _) => ProtectionInvalidReason.CurrentStale,
            (SignalState.Rejected, _) => ProtectionInvalidReason.CurrentRejected,
            (_, SignalDetail.AwaitingFirstSample) => ProtectionInvalidReason.CurrentAwaiting,
            (_, SignalDetail.EntityMissing or SignalDetail.EntityRemoved) => ProtectionInvalidReason.CurrentMissing,
            _ => ProtectionInvalidReason.CurrentUnavailable,
        };
    }

    private CurrentReading Reading(SignalRuntime signal, DateTimeOffset now)
    {
        var direction = ResolveDirection(signal, now);
        var signed = SignedValue(signal, direction);
        return new CurrentReading(new Amperes(signed), new Amperes(Math.Abs(signed)), direction.Direction, direction.Source, signal.Accepted!.Effective);
    }

    private ReserveDerivation Reserve(SwitchboardConfiguration configuration, Phase phase)
    {
        var signal = configuration.Connection.Topology == Topology.ThreePhaseSummed
            ? WithRole(SignalRole.TotalCurrent)!
            : WithRole(SignalRole.PhaseCurrent, phase)!;
        var control = configuration.Control;
        var characteristic = configuration.Connection.Characteristic;
        return MinimumSafeReserve.Compute(
            phase,
            configuration.Connection,
            control.Breaker.Tiers[characteristic],
            control.Breaker.ContinuousMultiple[BreakerCharacteristic.Unknown],
            signal.Refresh.Estimate(control.Signals.Refresh),
            signal.Definition.MaxAge,
            new Amperes(signal.Definition.Sigma ?? 0),
            control.ReserveDerivation);
    }

    private ImmutableArray<SignalReading> Readings(SwitchboardConfiguration configuration, DateTimeOffset now)
    {
        var readings = ImmutableArray.CreateBuilder<SignalReading>();
        var refreshParameters = configuration.Control.Signals.Refresh;
        foreach (var signal in _signals)
        {
            var definition = signal.Definition;
            var (state, detail) = Classify(signal, now);
            var direction = signal.Accepted is not null && definition.Kind is SignalKind.Current or SignalKind.Power
                ? ResolveDirection(signal, now)
                : null;
            readings.Add(new SignalReading(
                definition.Name,
                definition.Kind,
                definition.Role,
                definition.Phase,
                state,
                detail,
                state == SignalState.Unavailable ? null : DisplayValue(signal, direction),
                signal.Raw?.State,
                signal.Raw?.Unit,
                signal.Accepted?.Effective ?? signal.Raw?.Reported,
                definition.MaxAge,
                signal.Refresh.Estimate(refreshParameters),
                signal.Refresh.Count,
                direction,
                signal.RejectedCount,
                _dependents.GetValueOrDefault(definition.Name, [])));
        }

        foreach (var (role, phase) in UnmappedSlots(configuration))
        {
            var kind = role switch
            {
                SignalRole.PhaseCurrent or SignalRole.InverterPhaseCurrent => SignalKind.Current,
                SignalRole.PhaseVoltage => SignalKind.Voltage,
                _ => SignalKind.Power,
            };
            readings.Add(new SignalReading(null, kind, role, phase, SignalState.Unmapped, SignalDetail.None, null, null, null, null, null, null, 0, null, 0, []));
        }

        return readings.ToImmutable();
    }

    /// <summary>Optional role slots of the topology that no declared signal fills, so the user sees what could be mapped.</summary>
    private IEnumerable<(SignalRole Role, Phase? Phase)> UnmappedSlots(SwitchboardConfiguration configuration)
    {
        var phases = configuration.Connection.Phases;
        var perPhase = configuration.Connection.Topology == Topology.ThreePhaseSummed
            ? new[] { SignalRole.PhaseCurrent, SignalRole.PhasePower, SignalRole.PhaseVoltage }
            : [SignalRole.PhasePower, SignalRole.PhaseVoltage];
        foreach (var role in perPhase)
        {
            foreach (var phase in phases.Where(p => WithRole(role, p) is null))
            {
                yield return (role, phase);
            }
        }

        if (configuration.Connection.Topology == Topology.ThreePhaseSummed && WithRole(SignalRole.TotalPower) is null)
        {
            yield return (SignalRole.TotalPower, null);
        }

        if (WithRole(SignalRole.InverterPower) is null)
        {
            yield return (SignalRole.InverterPower, null);
        }
        else if (phases.All(p => WithRole(SignalRole.InverterPhaseCurrent, p) is null))
        {
            foreach (var phase in phases)
            {
                yield return (SignalRole.InverterPhaseCurrent, phase);
            }
        }
    }

    /// <summary>
    /// State precedence: Unavailable, then Stale, then Rejected (an unusable sample), then Fresh
    /// (SIG-05). A sample refused by the balance check leaves the held value Fresh until it ages out
    /// (SIG-09), with the refusal in the detail.
    /// </summary>
    private static (SignalState State, SignalDetail Detail) Classify(SignalRuntime signal, DateTimeOffset now)
    {
        switch (signal.Presence)
        {
            case Presence.Awaiting:
                return (SignalState.Unavailable, SignalDetail.AwaitingFirstSample);
            case Presence.Missing:
                return (SignalState.Unavailable, SignalDetail.EntityMissing);
            case Presence.Removed:
                return (SignalState.Unavailable, SignalDetail.EntityRemoved);
        }

        if (signal.RawStatus == RawStatus.Unavailable)
        {
            return (SignalState.Unavailable, signal.RawDetail);
        }

        if (signal.Accepted is not { } accepted)
        {
            return (SignalState.Rejected, signal.RawDetail);
        }

        // Aged from the last accepted report: a signal whose every new sample is rejected goes
        // stale like a frozen one instead of showing an old value forever. A device state declared
        // on_change has no age limit: it reports only when it changes, so its freshness is its
        // availability (Unavailable, Missing and a disconnected source are all handled above or by
        // the device-state interpreter).
        if (signal.Definition.Freshness == StateFreshness.Age && now - accepted.Effective >= signal.Definition.MaxAge)
        {
            return (SignalState.Stale, SignalDetail.MaxAgeExceeded);
        }

        // SIG-09 rejects the sample, never the decision: protection, dispatch and direction keep
        // using the last accepted value. The same-state path never advances it while the newest
        // state is the refused one, so it still goes stale at max_age.
        return signal.RawStatus switch
        {
            RawStatus.Invalid => (SignalState.Rejected, signal.RawDetail),
            RawStatus.BalanceRejected => (SignalState.Fresh, SignalDetail.BalanceResidual),
            _ => (SignalState.Fresh, SignalDetail.None),
        };
    }

    /// <summary>
    /// SIG-03. A signed value carries its own direction. An unsigned one takes it from its signed
    /// direction power, which must be fresh and reported no more than max_skew before the value
    /// itself (a newer direction power is fine: the value simply has not changed since). Within the
    /// deadband it counts as import. A changed value whose direction power from the same meter poll
    /// has not arrived yet is pending for the pairing wait; after that, or with the direction power
    /// not fresh, the direction is unknown: cautious mode, counted as import. Protection data stays
    /// valid either way.
    /// </summary>
    private DirectionInfo ResolveDirection(SignalRuntime signal, DateTimeOffset now)
    {
        var definition = signal.Definition;
        if (definition.Sign != SignConvention.Unsigned)
        {
            return new DirectionInfo(DirectionSource.SignedSignal, Value(signal) < 0 ? FlowDirection.Export : FlowDirection.Import, null);
        }

        if (definition.DirectionDeclaredUnavailable || DirectionSignalOf(signal) is not { } direction)
        {
            return new DirectionInfo(DirectionSource.NotConfigured, FlowDirection.Unknown, null);
        }

        var from = direction.Definition.Name;
        if (Classify(direction, now).State != SignalState.Fresh)
        {
            return new DirectionInfo(DirectionSource.Unavailable, FlowDirection.Unknown, from);
        }

        if (Aligned(signal, direction))
        {
            var power = Value(direction);
            var deadband = Direction.Deadband.Value;
            return power > deadband ? new DirectionInfo(DirectionSource.SignedSignal, FlowDirection.Import, from)
                : power < -deadband ? new DirectionInfo(DirectionSource.SignedSignal, FlowDirection.Export, from)
                : new DirectionInfo(DirectionSource.Deadband, FlowDirection.Import, from);
        }

        return now - signal.Accepted!.ArrivedAt < Direction.PairingWait
            ? new DirectionInfo(DirectionSource.Pending, FlowDirection.Unknown, from)
            : new DirectionInfo(DirectionSource.Unavailable, FlowDirection.Unknown, from);
    }

    private SignalRuntime? DirectionSignalOf(SignalRuntime signal) =>
        signal.Definition is { Sign: SignConvention.Unsigned, DirectionFrom: { } from } ? _byName.GetValueOrDefault(from) : null;

    private bool Aligned(SignalRuntime signal, SignalRuntime direction) =>
        signal.Accepted is { } value
        && direction.Accepted is { } power
        && power.Effective >= value.SampleReported - Direction.MaxSkew;

    private static double SignedValue(SignalRuntime signal, DirectionInfo direction)
    {
        var value = Value(signal);
        return signal.Definition.Sign == SignConvention.Unsigned && direction.Direction == FlowDirection.Export ? -value : value;
    }

    private static SignalValue? DisplayValue(SignalRuntime signal, DirectionInfo? direction)
    {
        if (signal.Accepted?.Value is not { } value || direction is null || signal.Definition.Sign != SignConvention.Unsigned)
        {
            return signal.Accepted?.Value;
        }

        var signed = SignedValue(signal, direction);
        return value switch
        {
            CurrentValue => new CurrentValue(new Amperes(signed)),
            PowerValue => new PowerValue(new Watts(signed)),
            _ => value,
        };
    }

    private static double Value(SignalRuntime signal) => SignalNormalizer.Magnitude(signal.Accepted!.Value) ?? 0;

    // An on_change state is never probed: it has no age to confirm.
    private static bool ProbeEligible(SignalRuntime signal) =>
        signal.Presence == Presence.Present && signal.Raw is not null && signal.RawStatus != RawStatus.Unavailable
        && signal.Definition.Freshness == StateFreshness.Age;

    private DateTimeOffset ProbeDue(SignalRuntime signal)
    {
        var basis = signal.Accepted?.Effective ?? signal.Raw!.Reported;
        var last = signal.LastProbeAt is { } probed && probed > basis ? probed : basis;
        return last + (signal.Definition.MaxAge * _configuration!.Control.Signals.FreshnessProbe.AfterFraction);
    }

    private sealed class BalanceState
    {
        public Watts? Residual { get; set; }

        public DateTimeOffset? LastEvaluated { get; set; }

        public DateTimeOffset? LastRejected { get; set; }

        public long Evaluations { get; set; }

        public long Rejections { get; set; }

        public long SkippedMisaligned { get; set; }

        public PhaseBalance ToPhaseBalance(Phase phase) =>
            new(phase, Residual, LastEvaluated, LastRejected, Evaluations, Rejections, SkippedMisaligned);
    }

    /// <summary>Generation per phase (null when unknown) and the report times it was computed from.</summary>
    private sealed record GenerationShares(ImmutableDictionary<Phase, Watts>? Shares, ImmutableArray<DateTimeOffset> InputTimes)
    {
        public static GenerationShares Unknown { get; } = new(null, []);

        public static GenerationShares Known(ImmutableArray<Phase> phases, Func<Phase, double> share, IEnumerable<DateTimeOffset> times) =>
            new(phases.ToImmutableDictionary(p => p, p => new Watts(share(p))), [.. times]);

        public Watts? For(Phase phase) => Shares is not null && Shares.TryGetValue(phase, out var share) ? share : null;
    }
}
