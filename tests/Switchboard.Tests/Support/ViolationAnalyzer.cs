using System.Collections.Immutable;
using System.Globalization;
using Switchboard.Application.Decisions;
using Switchboard.Application.Guard;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;

namespace Switchboard.Tests.Support;

/// <summary>
/// Checks a guard run against the tier table from the measured data alone (VER-04), independently
/// of how the guard decided:
/// <list type="bullet">
/// <item>Missed violation: for every band with a finite duration, each maximal interval of samples
/// with I/In above the band's lower bound that lasts longer than its allowed duration needs a shed
/// (switch off or reduce to minimum) of a device on that phase between the report before the rise
/// (where the guard conservatively starts counting) and the rise plus the allowed duration, and
/// then one more at every new sample while the current stays above, unless no sheddable step is
/// left or the phase exports (shedding would only raise an export current). In the open band
/// (0 s) every sheddable stage on the phase must be switched off at the entering sample.</item>
/// <item>No shed on a phase that was not over its limit: every tier or budget shed was taken with
/// the measured current above In or the budget below shed_one_below.</item>
/// <item>No silent action: every change of what protection holds a stage to, and every phase
/// entering safe state, has its decision record in the same step.</item>
/// </list>
/// "Sheddable" is computed here from the recorded device states and the recorded decisions: a stage
/// not already held off whose state is not known to be off (an unknown or stale state counts as on;
/// a modulating device stays sheddable until it is switched off).
/// </summary>
internal sealed class ViolationAnalyzer
{
    private readonly ProtectionSetup _setup;
    private readonly IReadOnlyList<ProtectionInputs> _observations;
    private readonly IReadOnlyList<Decision> _decisions;
    private readonly IReadOnlyList<GuardStep> _steps;
    private readonly ImmutableDictionary<Phase, List<Event>> _events;

    public ViolationAnalyzer(ProtectionSetup setup, IReadOnlyList<ProtectionInputs> observations, IReadOnlyList<Decision> decisions, IReadOnlyList<GuardStep> steps)
    {
        _setup = setup;
        _observations = observations;
        _decisions = [.. decisions.OrderBy(d => d.At)];
        _steps = steps;
        _events = setup.Phases.ToImmutableDictionary(p => p, Events);
    }

    /// <summary>Intervals that called for a shed which did not come in time, as readable lines.</summary>
    public List<string> MissedViolations()
    {
        var missed = new List<string>();
        var profile = _setup.Breaker;
        foreach (var (phase, events) in _events)
        {
            for (var band = 0; band < profile.Tiers.Bands.Length; band++)
            {
                if (profile.Band(band).Allowed is not { } allowed)
                {
                    continue;
                }

                foreach (var interval in Intervals(events, profile.LowerBound(band)))
                {
                    missed.AddRange(Check(phase, band, allowed, interval));
                }
            }
        }

        return missed;
    }

    /// <summary>Tier or budget sheds taken with the current at or below In and the budget not low.</summary>
    public List<string> ShedsWithoutOverload() =>
        [.. _decisions
            .Where(IsShed)
            .Where(d => d.Reason is ReasonCode.GuardTierExceeded or ReasonCode.GuardBudgetLow)
            .Where(d => d.Inputs.Phase is not { } p
                || !((p.Multiple ?? 0) > 1.0 || OverloadBudget.IsBelow(p.Budget, _setup.Budget.ShedOneBelow)))
            .Select(d => $"{d.At:O} {d.Device} {d.Action} {ReasonCodes.Name(d.Reason)} at {d.Inputs.Phase?.Multiple:0.###} x In, budget {d.Inputs.Phase?.Budget:0.###}")];

    /// <summary>Limit changes and safe-state entries without a decision record in the same step.</summary>
    public List<string> SilentActions()
    {
        var silent = new List<string>();
        for (var i = 1; i < _steps.Count; i++)
        {
            var before = _steps[i - 1].Evaluation.Status;
            var step = _steps[i];
            var after = step.Evaluation.Status;
            foreach (var device in after.Devices)
            {
                if (before.Devices.FirstOrDefault(d => d.Id == device.Id) is not { } previous || previous.Stages.Length != device.Stages.Length)
                {
                    continue;
                }

                foreach (var stage in device.Stages)
                {
                    var was = previous.Stages[stage.Stage - 1].Limit;
                    if (was == stage.Limit)
                    {
                        continue;
                    }

                    var action = stage.Limit switch
                    {
                        GuardTarget.Off => DecisionAction.SwitchOff,
                        GuardTarget.AtMinimum => DecisionAction.ReduceToMinimum,
                        _ => DecisionAction.Release,
                    };
                    if (!step.Decisions.Any(d => d.Device == device.Id && (d.Stage ?? 1) == stage.Stage && d.Action == action))
                    {
                        silent.Add($"{step.Evaluation.At:O} {device.Id} stage {stage.Stage}: {was?.ToString() ?? "none"} -> {stage.Limit?.ToString() ?? "none"} without a decision");
                    }
                }
            }

            foreach (var phase in after.Phases.Where(p => p.State == PhaseGuardState.SafeState))
            {
                var entered = before.Phases.FirstOrDefault(p => p.Phase == phase.Phase) is not { State: PhaseGuardState.SafeState };
                var recorded = step.Decisions.Any(d => d.Action == DecisionAction.SafeStateEntered && (d.Phase == phase.Phase || d.Scope == DecisionScope.System))
                    || before.SafeState.Scope == SafeStateScope.All;
                if (entered && !recorded)
                {
                    silent.Add($"{step.Evaluation.At:O} {phase.Phase} entered safe state without a decision");
                }
            }
        }

        return silent;
    }

    /// <summary>Would-shed decisions (tier and budget) per phase and band of the measured current.</summary>
    public SortedDictionary<string, int> WouldShedCounts() =>
        new(_decisions
            .Where(IsShed)
            .Where(d => d.Reason is ReasonCode.GuardTierExceeded or ReasonCode.GuardBudgetLow)
            .GroupBy(d => $"{d.Phase} band {d.Inputs.Phase?.Band} {ReasonCodes.Name(d.Reason)} {d.Action}")
            .ToDictionary(g => g.Key, g => g.Count()), StringComparer.Ordinal);

    /// <summary>The lowest budget the guard reported per phase, with when.</summary>
    public SortedDictionary<Phase, (double Budget, DateTimeOffset At)> BudgetMinimum()
    {
        var minimum = new SortedDictionary<Phase, (double Budget, DateTimeOffset At)>();
        foreach (var step in _steps)
        {
            foreach (var phase in step.Evaluation.Status.Phases)
            {
                if (!minimum.TryGetValue(phase.Phase, out var lowest) || phase.Budget < lowest.Budget)
                {
                    minimum[phase.Phase] = (phase.Budget, step.Evaluation.At);
                }
            }
        }

        return minimum;
    }

    private static bool IsShed(Decision decision) =>
        decision.Scope == DecisionScope.Device && decision.Action is DecisionAction.SwitchOff or DecisionAction.ReduceToMinimum;

    private IEnumerable<string> Check(Phase phase, int band, TimeSpan allowed, Interval interval)
    {
        var label = string.Create(CultureInfo.InvariantCulture, $"{phase} band {band} ({_setup.Breaker.LowerBound(band):0.##} x In, {allowed.TotalSeconds:0} s) from {interval.Start:O} to {interval.End:O}");
        if (allowed == TimeSpan.Zero)
        {
            if (interval.Samples[0].Exporting)
            {
                yield break;
            }

            foreach (var (device, stage) in Sheddable(phase, interval.Start, inclusive: false))
            {
                if (!_decisions.Any(d => d.Device == device && (d.Stage ?? 1) == stage && d.Action == DecisionAction.SwitchOff && d.At >= interval.Previous && d.At <= interval.Start))
                {
                    yield return $"{label}: {device} stage {stage} was not switched off at the entering sample";
                }
            }

            yield break;
        }

        if (interval.End - interval.Start <= allowed)
        {
            yield break;
        }

        var deadline = interval.Start + allowed;
        var first = _decisions.Any(d => IsShed(d) && IsOn(d.Device, phase) && d.At >= interval.Previous && d.At <= deadline);
        if (!first && !Exporting(interval, deadline) && Sheddable(phase, deadline, inclusive: true).Count > 0)
        {
            yield return $"{label}: no shed by {deadline:O}";
        }

        foreach (var sample in interval.Samples.Where(s => s.At > deadline))
        {
            if (sample.Exporting || Sheddable(phase, sample.At, inclusive: false).Count == 0)
            {
                continue;
            }

            if (!_decisions.Any(d => IsShed(d) && IsOn(d.Device, phase) && d.At == sample.At))
            {
                yield return $"{label}: no further shed at the sample of {sample.At:O}";
            }
        }
    }

    private static bool Exporting(Interval interval, DateTimeOffset at) =>
        interval.Samples.LastOrDefault(s => s.At <= at) is { Exporting: true };

    private bool IsOn(DeviceName? device, Phase phase) => device is { } name && _setup.Device(name)?.IsOn(phase) == true;

    /// <summary>Stages on the phase a shed could still act on at <paramref name="at"/> (decisions taken at it counted when inclusive).</summary>
    private List<(DeviceName Device, int Stage)> Sheddable(Phase phase, DateTimeOffset at, bool inclusive)
    {
        var states = _observations.LastOrDefault(o => o.TakenAt <= at)?.DeviceStates ?? [];
        var result = new List<(DeviceName, int)>();
        foreach (var device in _setup.Devices.Where(d => d.IsOn(phase) && !d.NoShed))
        {
            foreach (var stage in device.Stages.Where(s => s.Phases.Contains(phase)))
            {
                var last = _decisions.LastOrDefault(d => d.Device == device.Id && (d.Stage ?? 1) == stage.Index && d.Scope == DecisionScope.Device && (inclusive ? d.At <= at : d.At < at));
                if (last?.Action == DecisionAction.SwitchOff)
                {
                    continue;
                }

                var sample = states.IsDefault ? null : states.FirstOrDefault(s => s.Device == device.Id && s.Stage == stage.Index);
                var applied = sample is { Fresh: true } ? sample.Applied : null;
                var knownOff = applied switch
                {
                    RelayApplied relay => !relay.On,
                    SetpointApplied setpoint => setpoint.Setpoint.Value <= 0,
                    _ => false,
                };
                if (!knownOff)
                {
                    result.Add((device.Id, stage.Index));
                }
            }
        }

        return result;
    }

    /// <summary>Maximal runs of valid samples above <paramref name="lowerBound"/>; invalid data ends a run.</summary>
    private IEnumerable<Interval> Intervals(List<Event> events, double lowerBound)
    {
        List<Sample>? open = null;
        DateTimeOffset previous = default;
        DateTimeOffset? lastValid = null;
        foreach (var e in events)
        {
            if (e.Sample is not { } sample)
            {
                if (open is not null)
                {
                    yield return new Interval(previous, open[0].At, e.At, open);
                    open = null;
                }

                lastValid = null;
                continue;
            }

            var above = _setup.Breaker.MultipleOf(new Switchboard.Domain.Units.Amperes(sample.Magnitude)) > lowerBound;
            if (above)
            {
                if (open is null)
                {
                    open = [];
                    previous = lastValid ?? sample.At;
                }

                open.Add(sample);
            }
            else if (open is not null)
            {
                yield return new Interval(previous, open[0].At, sample.At, open);
                open = null;
            }

            lastValid = sample.At;
        }

        if (open is not null)
        {
            yield return new Interval(previous, open[0].At, events[^1].At, open);
        }
    }

    /// <summary>One phase's samples (one per report time, with the direction finally resolved for it) and invalid stretches.</summary>
    private List<Event> Events(Phase phase)
    {
        var events = new List<Event>();
        var byReport = new Dictionary<DateTimeOffset, int>();
        foreach (var observation in _observations)
        {
            var entry = observation.For(phase);
            if (entry is { DataValid: true, Current: { } current })
            {
                var sample = new Sample(current.ReportedAt, current.Magnitude.Value, current.Direction == FlowDirection.Export);
                if (byReport.TryGetValue(current.ReportedAt, out var index))
                {
                    events[index] = new Event(current.ReportedAt, sample);
                }
                else
                {
                    byReport[current.ReportedAt] = events.Count;
                    events.Add(new Event(current.ReportedAt, sample));
                }
            }
            else if (events.Count == 0 || events[^1].Sample is not null)
            {
                events.Add(new Event(observation.TakenAt, null));
            }
        }

        return [.. events.OrderBy(e => e.At)];
    }

    private sealed record Sample(DateTimeOffset At, double Magnitude, bool Exporting);

    private sealed record Event(DateTimeOffset At, Sample? Sample);

    private sealed record Interval(DateTimeOffset Previous, DateTimeOffset Start, DateTimeOffset End, List<Sample> Samples);
}
