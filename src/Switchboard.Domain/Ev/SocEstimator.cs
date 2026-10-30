namespace Switchboard.Domain.Ev;

/// <summary>A SoC reading as the signal layer delivers it: the value and the time of its report (<c>last_reported</c>, never <c>last_changed</c>).</summary>
public sealed record SocReading(double Percent, DateTimeOffset ReportedAt);

/// <summary>
/// Energy the charger drew since the previous step, and whether its power data is fresh. Stale data contributes nothing: it is not guessed.
/// <paramref name="CouldFlow"/> says whether energy could have flowed in this step (the car is plugged, or that is not known): stale charger data
/// is a gap only then, so an unplugged parked car whose charger entity blips keeps its estimate. The default is the conservative reading.
/// </summary>
public sealed record ChargerEnergy(double DeltaKwh, bool Fresh, bool CouldFlow = true);

/// <summary>
/// The estimator's memory: the last value the car <i>changed</i> to, when it reported it, when the car last reported anything (the same value again
/// included: it ages the estimate), the charger energy drawn since the anchor, and whether charger data went stale since the anchor.
/// </summary>
public sealed record SocEstimatorState(double? AnchorPercent, DateTimeOffset? AnchorAt, DateTimeOffset? LastReadingAt, double DrawnKwhSinceAnchor, bool ChargerGap = false)
{
    public static SocEstimatorState Empty { get; } = new(null, null, null, 0);
}

/// <summary>Why there is no estimate. Internal closed list.</summary>
public enum SocEstimateGap
{
    NoReading,
    TooOld,
    DriftExceeded,
    ChargerStale,

    /// <summary>The capacity is not a positive finite number: no percentage can be computed.</summary>
    InvalidCapacity,
}

public enum SocAnchorEvent
{
    None,

    /// <summary>The first reading since start.</summary>
    Anchored,

    /// <summary>The car reported a new value; the estimate restarts from it.</summary>
    Changed,

    /// <summary>The new value is further from the estimate than the drift limit allows: the car was charged or driven outside what the add-on saw.</summary>
    Jump,
}

/// <summary>The SoC to plan with, or none. Never the last value held past its welcome.</summary>
public sealed record SocEstimate(double? Percent, SocEstimateGap? Gap)
{
    public bool Available => Percent is not null;
}

public sealed record SocStep(SocEstimatorState State, SocEstimate Estimate, SocAnchorEvent Event);

/// <summary>
/// SoC between the car's readings (EV.md section 4 B10). Rules, each pinned by a test:
/// <list type="bullet">
/// <item>The anchor (value and energy) moves only when the car reports a <i>different</i> value: a car that repeats an old value while charging would
/// otherwise reset a correct estimate to a stale one. The same value again does refresh the age used for <c>ev.soc_max_age</c> (the integration is alive and
/// the car did not move), so an idle plugged car does not lose its estimate; <c>ev.drift_limit_kwh</c> still bounds the energy drawn since the anchor.</item>
/// <item>Between readings the estimate grows from the charger's energy (stored = drawn / (1 + loss)), only while the charger's data is fresh. A tick with
/// stale charger data after the anchor, while energy could flow (<see cref="ChargerEnergy.CouldFlow"/>), is a gap that may have hidden energy: the estimate stays unavailable until a changed reading resets it.</item>
/// <item>A changed reading restarts the estimate at the reported value with zero energy. The energy drawn between that reading's report time and now is
/// therefore dropped (a few minutes of charging at most; the reading is the truth, and the drift limit covers the rest).</item>
/// <item>Time: a report time in the future is read as now. Stored times are capped at now each step, so a clock that steps back cannot freeze the
/// estimator. The only reading ignored for its time is one reported <i>before</i> the last accepted report (delivered out of order).</item>
/// <item>The estimate is dropped, never held, once the last report is older than <c>soc_max_age</c> or more than <c>drift_limit_kwh</c> was drawn.</item>
/// </list>
/// Pure; the caller keeps the state.
/// </summary>
public static class SocEstimator
{
    public static SocStep Step(
        SocEstimatorState state,
        SocReading? reading,
        ChargerEnergy charger,
        DateTimeOffset now,
        double capacityKwh,
        EvParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(charger);
        ArgumentNullException.ThrowIfNull(parameters);

        if (!double.IsFinite(capacityKwh) || capacityKwh <= 0)
        {
            return new SocStep(state, new SocEstimate(null, SocEstimateGap.InvalidCapacity), SocAnchorEvent.None);
        }

        var current = state with
        {
            AnchorAt = Earlier(state.AnchorAt, now),
            LastReadingAt = Earlier(state.LastReadingAt, now),
        };

        if (current.AnchorPercent is not null)
        {
            var drawn = current.DrawnKwhSinceAnchor;
            if (charger.Fresh && double.IsFinite(charger.DeltaKwh) && charger.DeltaKwh > 0)
            {
                drawn += charger.DeltaKwh;
            }

            current = current with { DrawnKwhSinceAnchor = drawn, ChargerGap = current.ChargerGap || (charger.CouldFlow && !charger.Fresh) };
        }

        var before = Evaluate(current, now, capacityKwh, parameters);
        var anchorEvent = SocAnchorEvent.None;

        if (reading is { } r && double.IsFinite(r.Percent) && r.Percent is >= 0 and <= 100)
        {
            var reportedAt = r.ReportedAt > now ? now : r.ReportedAt;
            if (current.LastReadingAt is not { } last || reportedAt >= last)
            {
                if (current.AnchorPercent is not { } anchor)
                {
                    current = new SocEstimatorState(r.Percent, reportedAt, reportedAt, 0);
                    anchorEvent = SocAnchorEvent.Anchored;
                }
                else if (r.Percent != anchor)
                {
                    var driftPoints = parameters.DriftLimitKwh / capacityKwh * 100;
                    anchorEvent = before.Percent is { } estimated && Math.Abs(r.Percent - estimated) > driftPoints
                        ? SocAnchorEvent.Jump
                        : SocAnchorEvent.Changed;
                    current = new SocEstimatorState(r.Percent, reportedAt, reportedAt, 0);
                }
                else
                {
                    // The same value again: proof that the integration is alive, not that the battery is where it was. The anchor and the energy stay.
                    current = current with { LastReadingAt = reportedAt };
                }
            }
        }

        return new SocStep(current, Evaluate(current, now, capacityKwh, parameters), anchorEvent);
    }

    private static DateTimeOffset? Earlier(DateTimeOffset? time, DateTimeOffset now) => time is { } t && t > now ? now : time;

    private static SocEstimate Evaluate(SocEstimatorState state, DateTimeOffset now, double capacityKwh, EvParameters p)
    {
        if (state.AnchorPercent is not { } anchor || state.AnchorAt is not { } anchoredAt)
        {
            return new SocEstimate(null, SocEstimateGap.NoReading);
        }

        if (now - (state.LastReadingAt ?? anchoredAt) > p.SocMaxAge)
        {
            return new SocEstimate(null, SocEstimateGap.TooOld);
        }

        if (state.DrawnKwhSinceAnchor > p.DriftLimitKwh)
        {
            return new SocEstimate(null, SocEstimateGap.DriftExceeded);
        }

        if (state.ChargerGap)
        {
            return new SocEstimate(null, SocEstimateGap.ChargerStale);
        }

        var stored = state.DrawnKwhSinceAnchor / (1 + p.LossFraction);
        return new SocEstimate(Math.Min(100, anchor + stored / capacityKwh * 100), null);
    }
}
