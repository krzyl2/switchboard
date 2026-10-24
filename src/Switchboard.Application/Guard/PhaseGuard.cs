using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;

namespace Switchboard.Application.Guard;

/// <summary>
/// Per-phase guard state, owned by <see cref="GuardEngine"/> and touched only by its single caller.
/// The budget runs on the meter's timeline: each new sample commits the interval since the
/// previous one at the larger of the two multiples (the rise may have happened right after the
/// previous report), and between samples the value is projected with the last multiple held, never
/// past that sample's max_age. With invalid data, or after a backward clock step, the budget holds
/// and the band timers keep their elapsed time; the next valid sample starts a new chain, so no
/// interval ever spans a gap.
/// </summary>
internal sealed class PhaseGuard
{
    private BandTimer[] _timers;
    private DateTimeOffset? _lastReportedAt;
    private DateTimeOffset? _pausedAt;

    public PhaseGuard(Phase phase, double budget, BreakerProfile profile)
    {
        Phase = phase;
        CommittedBudget = budget;
        Profile = profile;
        _timers = NewTimers(profile);
    }

    public Phase Phase { get; }

    public BreakerProfile Profile { get; private set; }

    public bool Valid { get; private set; }

    /// <summary>False until the first evaluation of this phase.</summary>
    public bool Seen { get; private set; }

    public ProtectionInvalidReason? InvalidReason { get; private set; }

    public CurrentReading? Reading { get; private set; }

    /// <summary>The latest valid sample's report time advanced at this evaluation.</summary>
    public bool NewSample { get; private set; }

    public double CommittedBudget { get; private set; }

    public DateTimeOffset? CommittedAt { get; private set; }

    public double HeldMultiple { get; private set; }

    public TimeSpan MaxAge { get; private set; }

    /// <summary>No interval may be committed until a new sample starts the next chain.</summary>
    public bool ChainBroken { get; private set; } = true;

    public DateTimeOffset? InvalidSince { get; private set; }

    // Event and reaction state, advanced by the engine.
    public int OrdinaryStreak { get; set; }

    public bool BudgetShedLatched { get; set; }

    public int ReleaseStreak { get; set; }

    public int ReserveOverStreak { get; set; }

    public int ReserveClearStreak { get; set; }

    public bool ReductionRequested { get; set; }

    public int BudgetLowStreak { get; set; }

    public int BudgetOkStreak { get; set; }

    public bool IncreasesStopped { get; set; }

    public bool ExportBlocked { get; set; }

    public double Multiple => Reading is { } reading ? Profile.MultipleOf(reading.Magnitude) : 0;

    /// <summary>A new tier table (configuration reload) restarts the band timers; the budget is thermal state and stays.</summary>
    public void Reconfigure(BreakerProfile profile)
    {
        if (!profile.Tiers.Bands.SequenceEqual(Profile.Tiers.Bands))
        {
            _timers = NewTimers(profile);
        }

        Profile = profile;
    }

    /// <summary>Budget at <paramref name="now"/>: projected with the held multiple up to the sample's validity.</summary>
    public double Budget(DateTimeOffset now, TimeSpan recovery)
    {
        if (!Valid || ChainBroken || CommittedAt is not { } at)
        {
            return CommittedBudget;
        }

        var until = now < at + MaxAge ? now : at + MaxAge;
        return OverloadBudget.Advance(CommittedBudget, HeldMultiple, Profile, until - at, recovery);
    }

    public bool Consuming => Valid && !ChainBroken && Profile.IsAboveContinuous(HeldMultiple);

    /// <summary>Applies this evaluation's data; returns true when a band timer calls for a step now.</summary>
    public bool Update(bool valid, CurrentReading? reading, ProtectionInvalidReason? reason, TimeSpan maxAge, TimeSpan recovery, DateTimeOffset now)
    {
        Seen = true;
        MaxAge = maxAge;
        if (!valid || reading is null)
        {
            if (Valid)
            {
                Pause(now, recovery);
                InvalidSince = now;
            }

            InvalidSince ??= now;
            Valid = false;
            InvalidReason = reason;
            NewSample = false;
            _lastReportedAt = null;
            return false;
        }

        NewSample = _lastReportedAt is not { } last || reading.ReportedAt > last;
        var multiple = Profile.MultipleOf(reading.Magnitude);
        if (ChainBroken)
        {
            if (NewSample && reading.ReportedAt <= now)
            {
                // A new chain: the budget held over the gap; timers resume where they paused. A
                // sample reported before the pause (re-delivered after a reconnect) starts it now,
                // so no interval ever covers the time without valid data.
                var start = _pausedAt is { } paused && reading.ReportedAt < paused ? now : reading.ReportedAt;
                CommittedAt = start;
                HeldMultiple = multiple;
                ChainBroken = false;
                _lastReportedAt = reading.ReportedAt;
                foreach (var (timer, band) in Timers())
                {
                    if (multiple > Profile.LowerBound(band))
                    {
                        timer.RunningSince = start;
                    }
                    else
                    {
                        timer.Reset();
                    }
                }
            }
            else
            {
                // A sample stamped after now is from before a backward clock step: wait for a real one.
                NewSample = false;
            }
        }
        else if (NewSample)
        {
            var previous = CommittedAt!.Value;
            CommittedBudget = OverloadBudget.Advance(CommittedBudget, Math.Max(HeldMultiple, multiple), Profile, reading.ReportedAt - previous, recovery);
            CommittedAt = reading.ReportedAt;
            HeldMultiple = multiple;
            _lastReportedAt = reading.ReportedAt;
            foreach (var (timer, band) in Timers())
            {
                if (multiple <= Profile.LowerBound(band))
                {
                    timer.Reset();
                }
                else if (timer.RunningSince is null)
                {
                    // Conservative start: the rise may have happened right after the previous report.
                    timer.RunningSince = previous;
                }
            }
        }

        Valid = true;
        InvalidReason = null;
        InvalidSince = null;
        Reading = reading;
        return TimerCallsForStep(now);
    }

    /// <summary>The clock stepped back: hold the budget at its last value and pause the timers until a new sample.</summary>
    public void OnClockStepBack(DateTimeOffset last, TimeSpan recovery)
    {
        if (Valid && !ChainBroken)
        {
            Pause(last, recovery);
        }

        // Times from before the step are not comparable with what comes after it.
        _pausedAt = null;
        _lastReportedAt = null;
        ChainBroken = true;
    }

    /// <summary>The earliest band-timer expiry still to come, if any.</summary>
    public DateTimeOffset? NextTimerExpiry()
    {
        DateTimeOffset? next = null;
        if (!Valid || ChainBroken)
        {
            return null;
        }

        foreach (var (timer, band) in Timers())
        {
            if (timer.RunningSince is { } since && !timer.Fired)
            {
                var expiry = since + (Profile.Band(band).Allowed!.Value - timer.Elapsed);
                next = next is null || expiry < next ? expiry : next;
            }
        }

        return next;
    }

    /// <summary>True while any band timer has run past its allowed duration (the phase is shedding).</summary>
    public bool AnyTimerExpired => Timers().Any(t => t.Timer.Fired);

    private bool TimerCallsForStep(DateTimeOffset now)
    {
        if (ChainBroken)
        {
            return false;
        }

        var step = false;
        foreach (var (timer, band) in Timers())
        {
            if (timer.RunningSince is not { } since)
            {
                continue;
            }

            var elapsed = timer.Elapsed + (now - since);
            if (elapsed < Profile.Band(band).Allowed!.Value)
            {
                continue;
            }

            // One step at the instant the allowed duration runs out, then one per new sample while above.
            if (!timer.Fired)
            {
                timer.Fired = true;
                step = true;
            }
            else if (NewSample)
            {
                step = true;
            }
        }

        return step;
    }

    private void Pause(DateTimeOffset now, TimeSpan recovery)
    {
        if (CommittedAt is { } at && !ChainBroken)
        {
            var until = now < at + MaxAge ? now : at + MaxAge;
            _pausedAt = until;
            CommittedBudget = OverloadBudget.Advance(CommittedBudget, HeldMultiple, Profile, until - at, recovery);
            foreach (var (timer, _) in Timers())
            {
                if (timer.RunningSince is { } since)
                {
                    timer.Elapsed += until > since ? until - since : TimeSpan.Zero;
                    timer.RunningSince = null;
                }
            }
        }

        ChainBroken = true;
    }

    // Only bands with a finite duration have a timer; unlimited bands never expire.
    private IEnumerable<(BandTimer Timer, int Band)> Timers()
    {
        for (var band = 0; band < _timers.Length; band++)
        {
            if (Profile.Band(band).Allowed is not null)
            {
                yield return (_timers[band], band);
            }
        }
    }

    private static BandTimer[] NewTimers(BreakerProfile profile) => [.. profile.Tiers.Bands.Select(_ => new BandTimer())];

    /// <summary>How long the measured current has been above a band's lower bound, across pauses.</summary>
    private sealed class BandTimer
    {
        public DateTimeOffset? RunningSince { get; set; }

        public TimeSpan Elapsed { get; set; }

        public bool Fired { get; set; }

        public void Reset()
        {
            RunningSince = null;
            Elapsed = TimeSpan.Zero;
            Fired = false;
        }
    }
}
