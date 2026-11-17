using System.Collections.Immutable;

namespace Switchboard.Domain.Ev;

/// <summary>Why a SoC and range pair was not used as a consumption sample. Internal closed list (not a decision reason code); shown only in the log and the UI.</summary>
public enum EvSampleRejection
{
    /// <summary>Either value is absent or not a finite number.</summary>
    ValueMissing,

    /// <summary>The car is charging, or that is not known: SoC and range lag behind each other while energy flows in.</summary>
    Charging,

    /// <summary>The two values were reported more than <c>learn.pair_tolerance</c> apart: the car moved between them, so their ratio means nothing.</summary>
    ReportsDiffer,

    /// <summary>SoC outside 0 to 100.</summary>
    SocOutOfScale,

    /// <summary>SoC under <c>learn.min_soc</c>, or at or under the car's hybrid threshold: the buffer at the bottom of a PHEV battery drives no electric km.</summary>
    SocBelowMinimum,

    /// <summary>Range zero or negative: a depleted battery of a PHEV reports 0 electric km.</summary>
    RangeNotPositive,

    /// <summary>The same report was already sampled.</summary>
    AlreadySampled,

    /// <summary>The ratio is outside <c>learn.min_kwh_per_km</c> to <c>learn.max_kwh_per_km</c>.</summary>
    Implausible,
}

/// <summary>One accepted sample: the report time of the pair (the later of its two) and the consumption it gave.</summary>
public sealed record KwhPerKmSample(DateTimeOffset At, double KwhPerKm);

/// <summary>What the consumption is learned from (EV-04).</summary>
public enum KwhPerKmSource
{
    /// <summary>The car's SoC and electric range: the energy above the hybrid threshold over the range.</summary>
    SocAndRange,

    /// <summary>The car's own consumption figure (<c>ev.consumption</c>), taken as reported.</summary>
    Sensor,
}

/// <summary>What has been learned: the smoothed consumption (null until the first sample) and the newest samples, bounded by <c>learn.max_samples</c>.</summary>
/// <param name="HybridThresholdPercent">The hybrid threshold the samples were computed with; samples of another threshold measure another quantity. 0 for <see cref="KwhPerKmSource.Sensor"/>, which does not use it.</param>
/// <param name="Source">What the samples came from; samples of another source measure another quantity.</param>
public sealed record KwhPerKmState(double? Smoothed, ImmutableArray<KwhPerKmSample> Samples, double HybridThresholdPercent = 0, KwhPerKmSource Source = KwhPerKmSource.SocAndRange)
{
    public static KwhPerKmState Empty { get; } = new(null, []);
}

/// <summary>The car's own consumption figure as it came in one report, in kWh per km. A null value or time means the signal had none (or was not fresh).</summary>
public sealed record EvReportedConsumption(double? KwhPerKm, DateTimeOffset? ReportedAt);

/// <summary>
/// The car's electric SoC and range as they came in one report. A null value or time means the signal had none. "Charging" is true when the
/// car is charging or that is not known: a sample is taken only when it is known not to be.
/// </summary>
public sealed record EvConsumptionObservation(
    double? SocPercent,
    DateTimeOffset? SocReportedAt,
    double? RangeKm,
    DateTimeOffset? RangeReportedAt,
    bool Charging);

public sealed record LearnOutcome(KwhPerKmState State, double? Accepted, EvSampleRejection? Rejection);

/// <summary>
/// Learns kWh per km (EV-04, EV.md section 3 point 9) from one of two sources. Without <c>ev.consumption</c>, from the car's own SoC and electric range:
/// consumption = energy in the battery above the hybrid threshold ((SoC - threshold) x capacity) / electric range, since the range is zero at the threshold.
/// With it, from the car's own consumption figure (<see cref="LearnReported"/>). Samples learned with another threshold or from another source are dropped
/// (<see cref="Align"/>). Smoothed exponentially with <c>learn.smoothing</c>; until a sample is accepted the configured fallback stands. A SoC and a range
/// are one report when their times differ by at most <c>learn.pair_tolerance</c> (Home Assistant stamps the two entities of one car update apart); the pair
/// is deduplicated by the later of the two times. Pure: the caller keeps the state (it is persisted in ev.json).
/// </summary>
public static class KwhPerKmLearner
{
    public static LearnOutcome Learn(KwhPerKmState state, EvConsumptionObservation observation, double capacityKwh, double hybridThresholdPercent, EvLearnParameters learn)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(learn);
        state = Align(state, hybridThresholdPercent);

        var rejection = Check(state, observation, learn);
        if (rejection is { } reason)
        {
            return new LearnOutcome(state, null, reason);
        }

        var sample = (observation.SocPercent!.Value - hybridThresholdPercent) / 100 * capacityKwh / observation.RangeKm!.Value;
        if (!double.IsFinite(sample) || sample < learn.MinKwhPerKm || sample > learn.MaxKwhPerKm)
        {
            return new LearnOutcome(state, null, EvSampleRejection.Implausible);
        }

        return Accept(state, Later(observation.SocReportedAt!.Value, observation.RangeReportedAt!.Value), sample, learn);
    }

    /// <summary>
    /// One report of the car's own consumption figure (<c>ev.consumption</c>, already in kWh per km). The figure is the car's average over its recent
    /// distance, so it holds on a leg driven on fuel as long as its unit counts the fuel too (the configuration's choice). Taken once per report time, within
    /// the plausibility bounds, smoothed as a SoC and range sample is; whether the car is charging does not matter, the figure does not move with the SoC.
    /// </summary>
    public static LearnOutcome LearnReported(KwhPerKmState state, EvReportedConsumption observation, EvLearnParameters learn)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(learn);
        state = Align(state, 0, KwhPerKmSource.Sensor);

        if (observation.KwhPerKm is not { } sample || !double.IsFinite(sample) || observation.ReportedAt is not { } at)
        {
            return new LearnOutcome(state, null, EvSampleRejection.ValueMissing);
        }

        if (state.Samples.Any(s => s.At == at))
        {
            return new LearnOutcome(state, null, EvSampleRejection.AlreadySampled);
        }

        if (sample < learn.MinKwhPerKm || sample > learn.MaxKwhPerKm)
        {
            return new LearnOutcome(state, null, EvSampleRejection.Implausible);
        }

        return Accept(state, at, sample, learn);
    }

    /// <summary>
    /// The state if it was learned from <paramref name="source"/> (and, from SoC and range, with <paramref name="hybridThresholdPercent"/>), else nothing
    /// learned: its samples measured another quantity.
    /// </summary>
    public static KwhPerKmState Align(KwhPerKmState state, double hybridThresholdPercent, KwhPerKmSource source = KwhPerKmSource.SocAndRange)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (source == KwhPerKmSource.Sensor)
        {
            return state.Source == KwhPerKmSource.Sensor ? state : KwhPerKmState.Empty with { Source = KwhPerKmSource.Sensor };
        }

        return state.Source == KwhPerKmSource.SocAndRange && state.HybridThresholdPercent.Equals(hybridThresholdPercent)
            ? state
            : KwhPerKmState.Empty with { HybridThresholdPercent = hybridThresholdPercent };
    }

    /// <summary>The consumption to plan with: the smoothed value while it is inside the plausibility bounds (configuration may have narrowed them since it was learned), else the fallback.</summary>
    public static double Current(KwhPerKmState state, double fallbackKwhPerKm, EvLearnParameters learn) =>
        CurrentSource(state, learn) is null ? fallbackKwhPerKm : state.Smoothed!.Value;

    /// <summary>Where <see cref="Current"/> comes from: the source of the learned value, or null when the fallback stands.</summary>
    public static KwhPerKmSource? CurrentSource(KwhPerKmState state, EvLearnParameters learn)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(learn);
        return state.Smoothed is { } value && double.IsFinite(value) && value >= learn.MinKwhPerKm && value <= learn.MaxKwhPerKm ? state.Source : null;
    }

    private static LearnOutcome Accept(KwhPerKmState state, DateTimeOffset at, double sample, EvLearnParameters learn)
    {
        var smoothed = state.Smoothed is { } previous ? previous + learn.Smoothing * (sample - previous) : sample;
        var samples = state.Samples.Add(new KwhPerKmSample(at, sample));
        if (samples.Length > learn.MaxSamples)
        {
            samples = samples.RemoveRange(0, samples.Length - learn.MaxSamples);
        }

        return new LearnOutcome(state with { Smoothed = smoothed, Samples = samples }, sample, null);
    }

    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    private static EvSampleRejection? Check(KwhPerKmState state, EvConsumptionObservation o, EvLearnParameters learn)
    {
        if (o.SocPercent is not { } soc || !double.IsFinite(soc) || o.RangeKm is not { } range || !double.IsFinite(range)
            || o.SocReportedAt is not { } socAt || o.RangeReportedAt is not { } rangeAt)
        {
            return EvSampleRejection.ValueMissing;
        }

        if (o.Charging)
        {
            return EvSampleRejection.Charging;
        }

        if ((socAt - rangeAt).Duration() > learn.PairTolerance)
        {
            return EvSampleRejection.ReportsDiffer;
        }

        if (soc is < 0 or > 100)
        {
            return EvSampleRejection.SocOutOfScale;
        }

        if (soc < learn.MinSoc || (state.HybridThresholdPercent > 0 && soc <= state.HybridThresholdPercent))
        {
            return EvSampleRejection.SocBelowMinimum;
        }

        if (range <= 0)
        {
            return EvSampleRejection.RangeNotPositive;
        }

        return state.Samples.Any(s => s.At == Later(socAt, rangeAt)) ? EvSampleRejection.AlreadySampled : null;
    }
}
