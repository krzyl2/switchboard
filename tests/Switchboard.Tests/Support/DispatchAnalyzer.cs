using System.Globalization;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;

namespace Switchboard.Tests.Support;

/// <summary>
/// Checks a run of guard, dispatch and effector against the dispatch rules from what the run
/// recorded (VER-04), independently of how dispatch decided:
/// <list type="bullet">
/// <item>Every increase (a start, a stage or setpoint up) had its ceiling plus the start hysteresis
/// left on each constraint when its turn came, the guard allowed increases there (an override: its
/// own allowance), no safe state or kill switch was active, and no settling window was open on its
/// phases.</item>
/// <item>No identical intent follows itself, and no start→stop→start of a device happens unless the
/// measured headroom moved by more than the hysteresis band between the decisions (chatter).</item>
/// <item>No caused violation: the as-if current (measured plus every positive in-flight change, never
/// minus relief) never stays above a tier band longer than its allowed duration where the measured
/// current alone does not, without a reduction on that phase in time. Missed violations stay with
/// <see cref="ViolationAnalyzer"/>, on the measured current alone.</item>
/// </list>
/// It also reports the shadow numbers the maintainer reviews: would-start, would-stop and deferred
/// counts per device, time with the intention different from the reported state, foreign load
/// events per phase, and relief-hidden time (measured above In while the estimate is not).
/// </summary>
internal sealed class DispatchAnalyzer
{
    private readonly IReadOnlyList<GuardStep> _steps;
    private readonly IReadOnlyList<Decision> _decisions;
    private readonly DispatchSetup _setup;
    private readonly ProtectionSetup _protection;
    private readonly Dictionary<string, int> _stepOf = [];

    public DispatchAnalyzer(IReadOnlyList<GuardStep> steps, IReadOnlyList<Decision> decisions, DispatchSetup setup, ProtectionSetup protection)
    {
        _steps = steps;
        _decisions = [.. decisions.OrderBy(d => d.At)];
        _setup = setup;
        _protection = protection;
        for (var i = 0; i < steps.Count; i++)
        {
            foreach (var decision in steps[i].Decisions)
            {
                _stepOf[decision.Id] = i;
            }
        }
    }

    /// <summary>A target change of a device and whether it raised the level, with the step that recorded it.</summary>
    public sealed record Change(Decision Decision, DeviceSpec Spec, bool Increase, int Step);

    /// <summary>Every recorded target change of a device by dispatch or an override (shadow: what would have been switched), in order.</summary>
    public IReadOnlyList<Change> Changes() => _changes ??= FindChanges();

    private List<Change>? _changes;

    private List<Change> FindChanges()
    {
        var changes = new List<Change>();
        foreach (var decision in _decisions.Where(d => d.Scope == DecisionScope.Device && d.Device is not null && d.Origin is DecisionOrigin.Dispatch or DecisionOrigin.Override))
        {
            var spec = _setup.Device(decision.Device!.Value)?.Spec;
            var step = _stepOf.GetValueOrDefault(decision.Id, -1);
            if (spec is null || decision.Target is not { } target || step < 0)
            {
                continue;
            }

            // A relay change says its direction; a setpoint is compared with what was intended (or
            // reported) just before, since the device may have been switched by someone else.
            bool? increase = decision.Action switch
            {
                DecisionAction.SwitchOn => true,
                DecisionAction.SwitchOff or DecisionAction.ReduceToMinimum => false,
                DecisionAction.SetSetpoint => DeviceLevels.Total(spec, target) > DeviceLevels.Total(spec, Basis(step, spec)),
                _ => null,
            };
            if (increase is { } up)
            {
                changes.Add(new Change(decision, spec, up, step));
            }
        }

        return changes;
    }

    /// <summary>What a device was intended at (or reported, while nothing was intended) before the step.</summary>
    private DeviceLevel Basis(int step, DeviceSpec spec)
    {
        var before = step > 0 ? _steps[step - 1].DispatchStatus?.Devices.FirstOrDefault(d => d.Id == spec.Id) : null;
        return before?.Intended ?? before?.Reported ?? _steps[step].Evaluation.Envelope.Device(spec.Id)?.Reported ?? DeviceLevels.Off(spec);
    }

    /// <summary>Increases with less than their ceiling plus the start hysteresis left on a constraint when their turn came.</summary>
    public List<string> IncreasesIntoTooLittleHeadroom()
    {
        var problems = new List<string>();
        foreach (var change in Changes().Where(c => c.Increase && c.Decision.Origin == DecisionOrigin.Dispatch && c.Decision.Inputs.Dispatch is not null))
        {
            foreach (var headroom in change.Decision.Inputs.Dispatch!.Headroom.Where(h => h.Remaining is not null))
            {
                var ceiling = DeviceLevels.Ceiling(change.Spec, change.Decision.Target!, headroom.Key.Phase);
                if (ceiling.Value > headroom.Remaining!.Value.Value - _setup.StartHysteresis.Value + 1e-9)
                {
                    problems.Add(string.Create(CultureInfo.InvariantCulture, $"{change.Decision.At:O} {change.Spec.Id} increased into {headroom.Remaining!.Value.Value:0.00} A on {headroom.Key} for a {ceiling.Value:0.00} A ceiling"));
                }
            }
        }

        return problems;
    }

    /// <summary>Increases the guard did not allow at that step, or taken in safe state, with the kill switch on, or with a settling window open on their phases.</summary>
    public List<string> IncreasesAgainstTheGuardOrAWindow()
    {
        var problems = new List<string>();
        foreach (var change in Changes().Where(c => c.Increase))
        {
            var step = _steps[change.Step];
            var status = step.Evaluation.Status;
            var label = $"{change.Decision.At:O} {change.Spec.Id} {change.Decision.Origin} increase";
            if (status.SafeState.Active || status.KillSwitch.Engaged)
            {
                problems.Add($"{label} under {(status.KillSwitch.Engaged ? "the kill switch" : "safe state")}");
            }

            foreach (var key in Constraints(change.Spec))
            {
                var entry = step.Evaluation.Envelope.For(key);
                var allowed = change.Decision.Origin == DecisionOrigin.Override ? entry?.OverrideAllowed : entry?.IncreasesAllowed;
                if (allowed != true)
                {
                    problems.Add($"{label} on {key} where the guard allowed none");
                }
            }

            if (change.Step > 0 && _steps[change.Step - 1].DispatchStatus is { } before)
            {
                foreach (var open in before.Devices.Where(d => d.Settling == SettlingStatus.Open && d.Phases.Intersect(change.Spec.Phases).Any()))
                {
                    problems.Add($"{label} while the settling window of {open.Id} was open");
                }
            }
        }

        return problems;
    }

    /// <summary>
    /// Two consecutive decisions about one device saying the same thing (MODE-03, the shadow model),
    /// unless someone else switched the device in between (its reported state changed): a new
    /// intention after that is a new decision, not a repeat.
    /// </summary>
    public List<string> RepeatedIntents()
    {
        var problems = new List<string>();
        foreach (var device in _decisions.Where(d => d.Scope == DecisionScope.Device).GroupBy(d => (d.Device, d.Stage)))
        {
            Decision? previous = null;
            foreach (var decision in device)
            {
                if (previous is not null && previous.Action == decision.Action && previous.Reason == decision.Reason && previous.RefusedBy == decision.RefusedBy && Equals(previous.Target, decision.Target) && previous.Origin == decision.Origin
                    && !ReportedChanged(decision.Device!.Value, previous, decision))
                {
                    problems.Add($"{decision.At:O} {decision.Device} repeated {decision.Action} {ReasonCodes.Name(decision.Reason)} (first at {previous.At:O}, target {decision.Target})");
                }

                previous = decision;
            }
        }

        return problems;
    }

    /// <summary>
    /// Start→stop→start of one device by dispatch while the measured headroom (to the reserve
    /// threshold, from the measured current: independent of the load model) moved by no more than
    /// H_start − H_stop between the decisions. A stop by protection is not chatter.
    /// </summary>
    public List<string> Chatter()
    {
        var band = _setup.StartHysteresis.Value - _setup.StopHysteresis.Value;
        var problems = new List<string>();
        foreach (var device in Changes().Where(c => c.Decision.Origin == DecisionOrigin.Dispatch).GroupBy(c => c.Spec.Id))
        {
            var list = device.ToList();
            for (var i = 2; i < list.Count; i++)
            {
                var (first, stop, again) = (list[i - 2], list[i - 1], list[i]);
                if (!first.Increase || stop.Increase || !again.Increase)
                {
                    continue;
                }

                if (MeasuredHeadroom(first) is not { } a || MeasuredHeadroom(stop) is not { } b || MeasuredHeadroom(again) is not { } c)
                {
                    continue;
                }

                if (Math.Abs(b - a) <= band && Math.Abs(c - b) <= band)
                {
                    problems.Add(string.Create(CultureInfo.InvariantCulture, $"{device.Key} chattered: up {first.Decision.At:O} ({a:0.00} A), down {stop.Decision.At:O} ({b:0.00} A), up {again.Decision.At:O} ({c:0.00} A)"));
                }
            }
        }

        return problems;
    }

    /// <summary>
    /// Tier intervals of the as-if current (measured plus positive in-flight changes, never minus
    /// relief) that the measured current alone does not have, longer than the band allows, with no
    /// reduction of a device on that phase within the allowed duration.
    /// </summary>
    public List<string> CausedViolations()
    {
        var problems = new List<string>();
        var breaker = _protection.Breaker;
        foreach (var phase in _protection.Phases)
        {
            for (var band = 0; band < breaker.Tiers.Bands.Length; band++)
            {
                if (breaker.Band(band).Allowed is not { } allowed)
                {
                    continue;
                }

                var lower = breaker.LowerBound(band);
                DateTimeOffset? since = null;
                foreach (var step in _steps)
                {
                    var (measured, asIf) = Multiples(step, phase);
                    var caused = asIf > lower && !(measured > lower);
                    if (caused && since is null)
                    {
                        since = step.Evaluation.At;
                    }
                    else if (!caused && since is { } start)
                    {
                        Check(phase, band, lower, allowed, start, step.Evaluation.At, problems);
                        since = null;
                    }
                }

                if (since is { } open)
                {
                    Check(phase, band, lower, allowed, open, _steps[^1].Evaluation.At, problems);
                }
            }
        }

        return problems;
    }

    /// <summary>Time with the measured current above In while the estimate is not, per phase and per device whose relief made the difference.</summary>
    public SortedDictionary<string, TimeSpan> ReliefHidden()
    {
        var hidden = new SortedDictionary<string, TimeSpan>(StringComparer.Ordinal);
        for (var i = 1; i < _steps.Count; i++)
        {
            var step = _steps[i - 1];
            var span = _steps[i].Evaluation.At - step.Evaluation.At;
            foreach (var phase in step.Evaluation.Status.Phases.Where(p => p.DataValid && p.Multiple > 1.0 && p.EffectiveCurrent is { } e && Math.Abs(e.Value) <= _protection.Breaker.Rating.Value))
            {
                hidden[phase.Phase.ToString()] = hidden.GetValueOrDefault(phase.Phase.ToString()) + span;
                foreach (var device in step.Evaluation.Envelope.Devices.Where(d => d.InFlight.GetValueOrDefault(ConstraintKey.Main(phase.Phase)).Value < 0))
                {
                    hidden[device.Device.Value] = hidden.GetValueOrDefault(device.Device.Value) + span;
                }
            }
        }

        return hidden;
    }

    /// <summary>Per device: would-starts and would-stops (dispatch, override), deferrals, protective sheds.</summary>
    public SortedDictionary<string, (int Up, int Down, int Deferred, int Shed)> Counts()
    {
        var counts = new SortedDictionary<string, (int Up, int Down, int Deferred, int Shed)>(StringComparer.Ordinal);
        foreach (var spec in _setup.Devices.Select(d => d.Spec))
        {
            counts[spec.Id.Value] = (0, 0, 0, 0);
        }

        foreach (var change in Changes())
        {
            var (up, down, deferred, shed) = counts[change.Spec.Id.Value];
            counts[change.Spec.Id.Value] = change.Increase ? (up + 1, down, deferred, shed) : (up, down + 1, deferred, shed);
        }

        foreach (var decision in _decisions.Where(d => d.Device is { } name && counts.ContainsKey(name.Value)))
        {
            var (up, down, deferred, shed) = counts[decision.Device!.Value.Value];
            if (decision.Action == DecisionAction.Deferred)
            {
                counts[decision.Device!.Value.Value] = (up, down, deferred + 1, shed);
            }
            else if (decision.Origin is null or DecisionOrigin.Guard && decision.Action is DecisionAction.SwitchOff or DecisionAction.ReduceToMinimum)
            {
                counts[decision.Device!.Value.Value] = (up, down, deferred, shed + 1);
            }
        }

        return counts;
    }

    /// <summary>Foreign load events per phase (04 §4).</summary>
    public SortedDictionary<string, int> ForeignLoadEvents() =>
        new(_decisions.Where(d => d.Action == DecisionAction.ForeignLoadDetected).GroupBy(d => d.Phase?.ToString() ?? "none").ToDictionary(g => g.Key, g => g.Count()), StringComparer.Ordinal);

    /// <summary>Time each device's intention differed from its reported state.</summary>
    public SortedDictionary<string, TimeSpan> IntendedDiffersFromReported()
    {
        var totals = new SortedDictionary<string, TimeSpan>(StringComparer.Ordinal);
        for (var i = 1; i < _steps.Count; i++)
        {
            var span = _steps[i].Evaluation.At - _steps[i - 1].Evaluation.At;
            foreach (var device in _steps[i - 1].DispatchStatus?.Devices ?? [])
            {
                if (!device.Matches)
                {
                    totals[device.Id.Value] = totals.GetValueOrDefault(device.Id.Value) + span;
                }
            }
        }

        return totals;
    }

    /// <summary>The report the maintainer reads after a replay, one line per number.</summary>
    public IEnumerable<string> Report()
    {
        foreach (var (device, (up, down, deferred, shed)) in Counts())
        {
            yield return $"device {device}: {up} would-start/increase, {down} would-stop/decrease, {deferred} deferred, {shed} protective sheds";
        }

        foreach (var (device, time) in IntendedDiffersFromReported())
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"intended != reported: {device}: {time.TotalHours:0.00} h");
        }

        foreach (var (phase, count) in ForeignLoadEvents())
        {
            yield return $"foreign load events {phase}: {count}";
        }

        foreach (var (subject, time) in ReliefHidden())
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"relief-hidden (measured above In, estimate not): {subject}: {time.TotalMinutes:0.0} min");
        }

        foreach (var record in _steps[^1].DispatchStatus?.Devices.SelectMany(d => d.History.Select(h => (d.Id, h))) ?? [])
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"history {record.Id} {record.h.Kind} at {record.h.At:O}: expected {record.h.Expected:0.##} {record.h.Unit}, measured {record.h.Measured:0.##}, {record.h.Verdict}");
        }
    }

    private void Check(Phase phase, int band, double lower, TimeSpan allowed, DateTimeOffset start, DateTimeOffset end, List<string> problems)
    {
        if (end - start <= allowed)
        {
            return;
        }

        var reduced = _decisions.Any(d => d.At >= start && d.At <= start + allowed && d.Device is { } name && _protection.Device(name)?.IsOn(phase) == true
            && (d.Action is DecisionAction.SwitchOff or DecisionAction.ReduceToMinimum || (d.Action == DecisionAction.SetSetpoint && Changes().Any(c => c.Decision.Id == d.Id && !c.Increase))));
        if (!reduced)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{phase} band {band} (above {lower:0.##} x In for {allowed.TotalSeconds:0} s) only with dispatch's would-starts from {start:O} to {end:O}, and nothing reduced"));
        }
    }

    /// <summary>The measured multiple and the as-if multiple (measured plus positive in-flight changes) of a phase at a step.</summary>
    private (double? Measured, double? AsIf) Multiples(GuardStep step, Phase phase)
    {
        var status = step.Evaluation.Status.Phases.FirstOrDefault(p => p.Phase == phase);
        if (status is not { DataValid: true, Current: { } current })
        {
            return (null, null);
        }

        var added = step.Evaluation.Envelope.Devices.Sum(d => Math.Max(0, d.InFlight.GetValueOrDefault(ConstraintKey.Main(phase)).Value));
        var rating = _protection.Breaker.Rating.Value;
        return (Math.Abs(current.Value) / rating, Math.Abs(current.Value + added) / rating);
    }

    /// <summary>The device's reported level changed between the steps of two decisions.</summary>
    private bool ReportedChanged(DeviceName device, Decision first, Decision second)
    {
        if (!_stepOf.TryGetValue(first.Id, out var from) || !_stepOf.TryGetValue(second.Id, out var to))
        {
            return false;
        }

        var reported = _steps[from].Evaluation.Envelope.Device(device)?.Reported;
        return Enumerable.Range(from, to - from + 1).Any(i => !Equals(_steps[i].Evaluation.Envelope.Device(device)?.Reported, reported));
    }

    /// <summary>The smallest measured headroom to the reserve threshold on the device's phases at the step of a change.</summary>
    private double? MeasuredHeadroom(Change change)
    {
        var phases = _steps[change.Step].Evaluation.Status.Phases.Where(p => change.Spec.Phases.Contains(p.Phase)).ToList();
        return phases.Count == 0 || phases.Any(p => p.Headroom is null) ? null : phases.Min(p => p.Headroom!.Value.Value);
    }

    private IEnumerable<ConstraintKey> Constraints(DeviceSpec spec) =>
        spec.Phases.Select(ConstraintKey.Main).Concat(spec.CircuitGroup is { } group ? spec.Phases.Select(p => new ConstraintKey(group, p)) : []);
}
