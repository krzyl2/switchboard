using Switchboard.Domain.Units;

namespace Switchboard.Domain.Balance;

/// <summary>
/// Free power (BAL-02): what the house would export if Switchboard's own loads were off, as the mean of
/// <c>grid net export + Σ own load estimate</c> over the smoothing window. Samples are held until the
/// next one (the grid power is reported, not continuous), so the mean is weighted by time. A sample that
/// is not known, a gap longer than <c>max_age</c> and a clock step back each start the window afresh:
/// an estimate is never bridged over data that was not there. Pure, driven by the caller's clock.
/// </summary>
public sealed class FreePowerWindow
{
    private readonly List<(DateTimeOffset At, double Watts)> _samples = [];

    /// <param name="now">The injected clock.</param>
    /// <param name="sample">The instantaneous free power now, or null when any of its inputs is not known.</param>
    /// <returns>The windowed mean, or null while the estimate is unknown.</returns>
    public Watts? Observe(DateTimeOffset now, Watts? sample, HourlyBalanceParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (_samples.Count > 0 && (now < _samples[^1].At || now - _samples[^1].At > parameters.MaxAge))
        {
            _samples.Clear();
        }

        if (sample is not { } value || !double.IsFinite(value.Value))
        {
            _samples.Clear();
            return null;
        }

        if (_samples.Count > 0 && _samples[^1].At == now)
        {
            _samples[^1] = (now, value.Value);
        }
        else
        {
            _samples.Add((now, value.Value));
        }

        var start = now - parameters.SmoothingWindow;

        // A sample whose successor already started at or before the window start no longer counts.
        while (_samples.Count > 1 && _samples[1].At <= start)
        {
            _samples.RemoveAt(0);
        }

        double weighted = 0;
        double span = 0;
        for (var i = 0; i < _samples.Count; i++)
        {
            var from = _samples[i].At > start ? _samples[i].At : start;
            var to = i + 1 < _samples.Count ? _samples[i + 1].At : now;
            var seconds = (to - from).TotalSeconds;
            if (seconds > 0)
            {
                weighted += _samples[i].Watts * seconds;
                span += seconds;
            }
        }

        return new Watts(span > 0 ? weighted / span : value.Value);
    }

    public void Reset() => _samples.Clear();
}
