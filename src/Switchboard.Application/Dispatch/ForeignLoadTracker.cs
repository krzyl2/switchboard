using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Dispatch;

/// <summary>
/// Foreign load detection of 04 §4, per phase: foreign = house load − our devices, followed by a
/// slow baseline (an exponential moving average with baseline_time_constant) and a fast residual
/// (foreign − baseline). A residual rising above step_threshold_w is one foreign load event; the
/// detector re-arms once the residual is back below rearm_fraction × the threshold, so one kettle
/// is one event however long it boils. Only new samples (a newer phase power report) advance it.
/// </summary>
public sealed class ForeignLoadTracker
{
    private readonly Dictionary<Phase, PhaseState> _phases = [];

    /// <summary>The latest foreign load and baseline of a phase (null while unknown).</summary>
    public (Watts? Foreign, Watts? Baseline) Last(Phase phase) =>
        _phases.TryGetValue(phase, out var state) ? (state.Foreign is { } f ? new Watts(f) : null, state.Baseline is { } b ? new Watts(b) : null) : (null, null);

    /// <summary>Counts one sample; returns true when it is a foreign load step.</summary>
    /// <param name="foreign">House load minus our devices; null when either is unknown (the baseline then waits).</param>
    public bool Observe(Phase phase, Watts? foreign, DateTimeOffset? reportedAt, ForeignLoadSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        if (!_phases.TryGetValue(phase, out var state))
        {
            state = new PhaseState();
            _phases[phase] = state;
        }

        if (foreign is not { } value || reportedAt is not { } at)
        {
            state.Foreign = null;
            return false;
        }

        if (state.LastAt is { } last && at <= last)
        {
            return false;
        }

        state.Foreign = value.Value;
        if (state.Baseline is not { } baseline || state.LastAt is not { } previous)
        {
            state.Baseline = value.Value;
            state.LastAt = at;
            return false;
        }

        var residual = value.Value - baseline;
        var threshold = setup.StepThreshold.Value;
        var step = state.Armed && residual > threshold;
        if (step)
        {
            state.Armed = false;
        }
        else if (!state.Armed && residual < setup.RearmFraction * threshold)
        {
            state.Armed = true;
        }

        var alpha = 1 - Math.Exp(-(at - previous).Ticks / (double)setup.BaselineTimeConstant.Ticks);
        state.Baseline = baseline + ((value.Value - baseline) * alpha);
        state.LastAt = at;
        return step;
    }

    /// <summary>A new configuration: baselines start again.</summary>
    public void Reset() => _phases.Clear();

    private sealed class PhaseState
    {
        public double? Baseline { get; set; }

        public double? Foreign { get; set; }

        public DateTimeOffset? LastAt { get; set; }

        public bool Armed { get; set; } = true;
    }
}
