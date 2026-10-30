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

    /// <summary>SoC under <c>learn.min_soc</c>: the buffer at the bottom of a PHEV battery is not representative.</summary>
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

/// <summary>What has been learned: the smoothed consumption (null until the first sample) and the newest samples, bounded by <c>learn.max_samples</c>.</summary>
public sealed record KwhPerKmState(double? Smoothed, ImmutableArray<KwhPerKmSample> Samples)
{
    public static KwhPerKmState Empty { get; } = new(null, []);
}

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
/// Learns kWh per km from the car's own SoC and electric range (EV-04, EV.md section 3 point 9): consumption = energy in the battery
/// (SoC x capacity) / electric range. Smoothed exponentially with <c>learn.smoothing</c>; until a sample is accepted the configured fallback
/// stands. A SoC and a range are one report when their times differ by at most <c>learn.pair_tolerance</c> (Home Assistant stamps the two entities of one
/// car update apart); the pair is deduplicated by the later of the two times. Pure: the caller keeps the state (it is persisted in ev.json).
/// </summary>
public static class KwhPerKmLearner
{
    public static LearnOutcome Learn(KwhPerKmState state, EvConsumptionObservation observation, double capacityKwh, EvLearnParameters learn)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(learn);

        var rejection = Check(state, observation, learn);
        if (rejection is { } reason)
        {
            return new LearnOutcome(state, null, reason);
        }

        var sample = observation.SocPercent!.Value / 100 * capacityKwh / observation.RangeKm!.Value;
        if (!double.IsFinite(sample) || sample < learn.MinKwhPerKm || sample > learn.MaxKwhPerKm)
        {
            return new LearnOutcome(state, null, EvSampleRejection.Implausible);
        }

        var smoothed = state.Smoothed is { } previous ? previous + learn.Smoothing * (sample - previous) : sample;
        var samples = state.Samples.Add(new KwhPerKmSample(Later(observation.SocReportedAt!.Value, observation.RangeReportedAt!.Value), sample));
        if (samples.Length > learn.MaxSamples)
        {
            samples = samples.RemoveRange(0, samples.Length - learn.MaxSamples);
        }

        return new LearnOutcome(new KwhPerKmState(smoothed, samples), sample, null);
    }

    /// <summary>The consumption to plan with: the smoothed value while it is inside the plausibility bounds (configuration may have narrowed them since it was learned), else the fallback.</summary>
    public static double Current(KwhPerKmState state, double fallbackKwhPerKm, EvLearnParameters learn)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(learn);
        return state.Smoothed is { } value && double.IsFinite(value) && value >= learn.MinKwhPerKm && value <= learn.MaxKwhPerKm
            ? value
            : fallbackKwhPerKm;
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

        if (soc < learn.MinSoc)
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
