using System.Collections.Immutable;
using Switchboard.Application.Configuration;
using Switchboard.Application.Guard;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Setup.SelfTest;

/// <summary>
/// The passive self-test's recorded evidence (CONN-05): a read-only listener of the guard runner,
/// like the presentation hub. When a declared single-phase relay changes its reported state (someone
/// else switched it: Switchboard switches nothing), it keeps the per-phase reports around that moment
/// and, once the window has passed, stores a raw observation with the meter mapping and the state
/// entity it was measured with. It never touches guard, dispatch or effector state, and runs on the
/// signal clock of the step it reads.
/// </summary>
public sealed class PhaseEvidenceRecorder
{
    private readonly PhaseEvidenceStore _store;
    private readonly ISetupRuntimeView _runtime;
    private readonly SetupDefaults _defaults;
    private readonly int _historyLength;
    private readonly object _gate = new();
    private readonly Dictionary<Phase, List<PowerPoint>> _power = [];
    private readonly Dictionary<Phase, List<ReportPoint>> _current = [];
    private readonly Dictionary<Phase, bool> _currentCautious = [];
    private readonly Dictionary<(DeviceName Device, int Stage), bool> _relays = [];
    private readonly List<(DeviceName Device, DateTimeOffset At)> _transitions = [];
    private readonly Dictionary<DeviceName, List<ReportPoint>> _drawCurrent = [];
    private readonly Dictionary<DeviceName, List<ReportPoint>> _drawPower = [];
    private readonly List<Pending> _pending = [];

    public PhaseEvidenceRecorder(PhaseEvidenceStore store, ISetupRuntimeView runtime, SetupDefaults defaults, int historyLength)
    {
        _store = store;
        _runtime = runtime;
        _defaults = defaults;
        _historyLength = historyLength;
    }

    /// <summary>Follows the guard runner. Call once.</summary>
    public void Attach(GuardRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        runner.Stepped += (_, step) => OnStepped(step);
    }

    public void OnStepped(GuardStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Inputs is not { } inputs)
        {
            return;
        }

        var configuration = _runtime.Configuration;
        var now = step.Evaluation.At;
        lock (_gate)
        {
            if (configuration is null || inputs.Protection.Setup is null)
            {
                _pending.Clear();
                return;
            }

            Collect(inputs, now);
            foreach (var pending in _pending.ToList())
            {
                var device = configuration.Devices.FirstOrDefault(d => d.Id == pending.Device);
                if (device is null || device.Type != DeviceType.OnOff || device.Stages.Length != 1 || device.Stages[0].Phases.Length != 1)
                {
                    _pending.Remove(pending);
                    continue;
                }

                if (Observe(pending, device, configuration, now) is { } observation)
                {
                    _store.Add(observation);
                    _pending.Remove(pending);
                }
            }
        }
    }

    private void Collect(ControlInputs inputs, DateTimeOffset now)
    {
        foreach (var phase in inputs.Dispatch.Phases)
        {
            if (phase.NetImport is { } net && phase.PowerReportedAt is { } at)
            {
                Append(_power, phase.Phase, new PowerPoint(at, net.Value, phase.Load?.Value), p => p.At);
            }
        }

        foreach (var phase in inputs.Protection.Phases)
        {
            if (phase.Current is { } reading)
            {
                // In cautious mode the value is a magnitude counted as import: an unsigned reading.
                var cautious = reading.DirectionSource is not DirectionSource.SignedSignal;
                Append(_current, phase.Phase, new ReportPoint(reading.ReportedAt, reading.Signed.Value), p => p.At);
                _currentCautious[phase.Phase] = cautious;
            }
        }

        foreach (var draw in inputs.Protection.DeviceDraws.Where(d => d.Current is not null && d.ReportedAt is not null))
        {
            Append(_drawCurrent, draw.Device, new ReportPoint(draw.ReportedAt!.Value, draw.Current!.Value.Value), p => p.At);
        }

        foreach (var draw in inputs.Dispatch.DevicePowers.Where(d => d.Power is not null && d.ReportedAt is not null))
        {
            Append(_drawPower, draw.Device, new ReportPoint(draw.ReportedAt!.Value, draw.Power!.Value.Value), p => p.At);
        }

        foreach (var sample in inputs.Protection.DeviceStates)
        {
            if (sample.Stage < 1 || !sample.Fresh || sample.Applied is not RelayApplied relay)
            {
                continue;
            }

            var key = (sample.Device, sample.Stage);
            if (_relays.TryGetValue(key, out var was) && was != relay.On)
            {
                var at = sample.ReportedAt ?? now;
                _transitions.Add((sample.Device, at));
                if (_transitions.Count > _historyLength)
                {
                    _transitions.RemoveRange(0, _transitions.Count - _historyLength);
                }

                _pending.Add(new Pending(sample.Device, at, relay.On ? 1 : -1));
                if (_pending.Count > _historyLength)
                {
                    _pending.RemoveRange(0, _pending.Count - _historyLength);
                }
            }

            _relays[key] = relay.On;
        }
    }

    /// <summary>The observation of one switching once its window has passed; null while it is still open.</summary>
    private PhaseObservation? Observe(Pending pending, DeviceDefinition device, SwitchboardConfiguration configuration, DateTimeOffset now)
    {
        var stage = device.Stages[0];
        var phases = configuration.Connection.Phases;
        var powerSignals = phases.Select(p => configuration.Signals.FirstOrDefault(s => s.Role == SignalRole.PhasePower && s.Phase == p)).ToList();
        var usePower = powerSignals.All(s => s is not null);
        var signals = usePower
            ? powerSignals!
            : phases.Select(p => configuration.Signals.FirstOrDefault(s => s.Role == SignalRole.PhaseCurrent && s.Phase == p)).ToList();
        var meter = phases.Zip(signals)
            .Where(x => x.Second is not null)
            .ToImmutableDictionary(x => x.First, x => new MeterTerm(x.Second!.Entity, x.Second.Unit ?? string.Empty, MeterValues.SignName(x.Second.Sign)));
        var lag = configuration.Control.Signals.CurrentLagPolls;
        var expectedSignal = signals[phases.IndexOf(stage.Phases[0])];
        TimeSpan? refresh = expectedSignal is not null && _runtime.DetectedRefresh.TryGetValue(expectedSignal.Entity, out var detected) ? detected : null;
        var window = StepWindow.Window(_defaults.SelfTest.Isolation, lag, refresh);
        var unit = usePower ? "W" : "A";
        if (window is not { } span)
        {
            // Without a detected refresh the window is unknown: inconclusive, never a guess.
            return now - pending.At < _defaults.SelfTest.Isolation
                ? null
                : new PhaseObservation(pending.At, pending.Direction, stage.StateEntity, ObservationSource.Recorded, unit, false, false, meter, ImmutableDictionary<Phase, Domain.Connection.PhaseStepMeasurement>.Empty, true, "refresh_not_detected");
        }

        if (now < pending.At + span)
        {
            return null;
        }

        var houseLoad = usePower && phases.All(p => _power.TryGetValue(p, out var series)
            && series.Where(x => x.At > pending.At - span - span && x.At <= pending.At + span).All(x => x.Load is not null));
        var measurements = ImmutableDictionary.CreateBuilder<Phase, Domain.Connection.PhaseStepMeasurement>();
        var unsigned = false;
        foreach (var (phase, signal) in phases.Zip(signals))
        {
            var sigma = signal?.Sigma ?? configuration.Control.Signals.DefaultSigma.GetValueOrDefault(usePower ? SignalKind.Power : SignalKind.Current);
            IReadOnlyList<ReportPoint> series = usePower
                ? _power.TryGetValue(phase, out var power) ? [.. power.Select(x => new ReportPoint(x.At, houseLoad ? x.Load ?? x.Net : x.Net))] : []
                : _current.TryGetValue(phase, out var current) ? current : [];
            unsigned |= !usePower && _currentCautious.GetValueOrDefault(phase);
            measurements[phase] = StepWindow.Measure(series, pending.At, span, lag, sigma);
        }

        var parameters = _defaults.SelfTest.Classifier;
        var isolated = !_transitions.Any(t => t.Device != pending.Device && t.At > pending.At - span && t.At <= pending.At + span)
            && !configuration.Devices.Where(d => d.Id != pending.Device).Any(d =>
                (_drawCurrent.TryGetValue(d.Id, out var amps) && StepWindow.Changed(amps, pending.At, span, Quiet(parameters, stage.RatedCurrent.Value, configuration.Control.Signals.DefaultSigma.GetValueOrDefault(SignalKind.Current))))
                || (_drawPower.TryGetValue(d.Id, out var watts) && StepWindow.Changed(watts, pending.At, span, Quiet(parameters, stage.RatedPower?.Value ?? 0, configuration.Control.Signals.DefaultSigma.GetValueOrDefault(SignalKind.Power)))));
        return new PhaseObservation(pending.At, pending.Direction, stage.StateEntity, ObservationSource.Recorded, unit, unsigned, houseLoad, meter, measurements.ToImmutable(), isolated, null);
    }

    /// <summary>T_quiet of the device under test, in the unit of a draw: a smaller draw change is left to the per-phase checks.</summary>
    private static double Quiet(Domain.Connection.PhaseStepParameters parameters, double rating, double sigma) =>
        Math.Max(parameters.QuietMaxFraction * rating, parameters.SigmaMultiplier * sigma);

    private void Append<TKey, TPoint>(Dictionary<TKey, List<TPoint>> series, TKey key, TPoint point, Func<TPoint, DateTimeOffset> at)
        where TKey : notnull
    {
        if (!series.TryGetValue(key, out var list))
        {
            list = [];
            series[key] = list;
        }

        if (list.Count > 0 && at(point) <= at(list[^1]))
        {
            return;
        }

        list.Add(point);
        if (list.Count > _historyLength)
        {
            list.RemoveRange(0, list.Count - _historyLength);
        }
    }

    /// <summary>One phase power report: net import, and the house load when generation was fresh.</summary>
    private readonly record struct PowerPoint(DateTimeOffset At, double Net, double? Load);

    private sealed record Pending(DeviceName Device, DateTimeOffset At, int Direction);
}
