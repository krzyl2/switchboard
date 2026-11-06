using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Hashing;
using Switchboard.Domain.Time;

namespace Switchboard.Domain.Ev;

public enum EvGoalKind
{
    /// <summary>The next calendar trip: hard, due at the trip's start, ready <c>ready_margin</c> before it.</summary>
    Trip,

    /// <summary>The weekly (or requested) full charge: soft, due at the next local midnight, PV first and then the cheapest zone.</summary>
    WeeklyFull,

    /// <summary>The surplus cap: soft, due at the next local midnight, PV only, stops when met.</summary>
    Cap,

    /// <summary>The floor (the reserve): hard, PV and the cheapest zone, due at the next local <c>floor_by</c> without escalation, so the window's forecast PV is placed first and the rest in the earliest cheap hours; beside a trip ready before that, due when the car must be ready and escalating like the trip.</summary>
    Floor,
}

/// <summary>
/// One goal the module offers. <see cref="Request"/> is what the existing goal machinery consumes; <see cref="Spread"/> and
/// <see cref="QuietShortfall"/> are the planner flags of the spread design (EV.md 2a, B12) carried beside it until <c>GoalRequest</c> has fields for them.
/// </summary>
public sealed record EvGoalOffer(EvGoalKind Kind, GoalRequest Request, bool Spread, bool QuietShortfall);

/// <param name="Code">The primary assessment code; always set.</param>
/// <param name="Notes">Conditions on top of it (calendar unavailable, range unusable, forecast missing, no planner dispatch).</param>
/// <param name="Goals">The goals offered, with unique deadlines and incremental needs; empty when the module wants nothing.</param>
/// <param name="Frozen">The trip target frozen for the next trip (pass it back next time); null without a trip.</param>
/// <param name="WeeklyFullReached">The SoC reached the weekly full target for the first time this local week, on any day (or a requested full charge reached its own): the caller records <c>LastFullAt</c>.</param>
public sealed record EvComposition(
    EvAssessmentCode Code,
    ImmutableArray<EvAssessmentCode> Notes,
    ImmutableArray<EvGoalOffer> Goals,
    FrozenTarget? Frozen,
    bool WeeklyFullReached);

/// <summary>Everything the composer reads; the caller (the snapshot keeper) gathers it, so the composer needs no clock, no signals and no I/O.</summary>
public sealed record EvComposerInput
{
    public required DateTimeOffset Now { get; init; }

    /// <summary>The add-on's zone: weekend days and midnights are local.</summary>
    public required TimeZoneInfo Zone { get; init; }

    public required DeviceName Device { get; init; }

    public required double CapacityKwh { get; init; }

    public required EvParameters Parameters { get; init; }

    public required EvMode Mode { get; init; }

    /// <summary>A "don't charge now" is running (2026-11-06): the module offers what <see cref="EvMode.PvOnly"/> offers, PV surplus and no grid, until it ends.</summary>
    public bool Hold { get; init; }

    /// <summary>A manual goal exists for the device: the module offers nothing (B8).</summary>
    public bool ManualGoalActive { get; init; }

    /// <summary>True when plugged or when no plugged signal is configured; false when unplugged; null when the state is unknown or unavailable (no goal).</summary>
    public required bool? Plugged { get; init; }

    /// <summary>The SoC estimate; null when there is none (stale, drifted, no reading): no goals, never the last value.</summary>
    public required double? SocPercent { get; init; }

    /// <summary>The estimate the floor alone may plan with (<see cref="SocEstimate.FloorPercent"/>): while <see cref="SocPercent"/> is null only for its age, the floor is the one goal offered.</summary>
    public double? FloorSocPercent { get; init; }

    /// <summary>The consumption to plan with: learned, else the fallback.</summary>
    public required double KwhPerKm { get; init; }

    /// <summary>The next trip as parsed; a trip whose start has passed is ignored here (B4).</summary>
    public Trip? NextTrip { get; init; }

    public FrozenTarget? Frozen { get; init; }

    public DateTimeOffset? LastFullAt { get; init; }

    /// <summary>The user asked for a full charge (<see cref="EvMode.FullCharge"/> counts as asking): the weekly goal is offered on any day and regardless of the last full charge.</summary>
    public bool FullChargeRequested { get; init; }

    /// <summary>Whether a local date is a holiday (<c>economics.holidays</c>); a holiday counts as a weekend day.</summary>
    public required Func<DateOnly, bool> IsHoliday { get; init; }

    /// <summary>The cheapest tariff zone id (configuration, never a name in code); null when there is none.</summary>
    public string? CheapZone { get; init; }

    public bool CalendarUnavailable { get; init; }

    public bool RangeUnusable { get; init; }

    public bool ForecastMissing { get; init; }

    public bool DispatchOff { get; init; }

    /// <summary>How far the plan looks ahead (<c>planner.horizon</c>): the power bands are stated as instants that far.</summary>
    public TimeSpan PlanHorizon { get; init; }
}

/// <summary>
/// Turns the car's situation into at most four goals (EV-06, docs/EV.md sections 3, 4 and 2a). Pure.
/// <list type="bullet">
/// <item>Needs are incremental (B2): goals are absolute SoC targets against the same SoC, so each goal is asked only for the part above the goals with a
/// lower target; the sum is exactly (highest target - SoC) x capacity x (1 + loss), never more. The cap is not offered at all when a trip or weekly goal asks
/// for at least as much, so a soft PV-only goal never owns energy a hard goal is due.</item>
/// <item>Deadlines are unique (B8): the trip is its start, the weekly and cap goals the next local midnight, the floor the next local <c>floor_by</c> (never midnight) or the trip's ready moment when that comes first. Goals that meet at one deadline
/// merge into the one that ranks first (trip, weekly, cap): a soft one merges its need into a soft winner and is dropped under the trip.</item>
/// <item>The revision names only what the goal is (B3): never the target, the SoC or the deadline of a daily goal.</item>
/// <item>A trip is offered only while its start is ahead (B4); the margin is the goal's, the deadline the trip start (B2b).</item>
/// <item>When every need is zero the highest-target goal is still offered, met, so that its <c>on_met: stop</c> keeps the device from falling back to the v2 opportunist.</item>
/// </list>
/// </summary>
public static class EvComposer
{
    private sealed record Candidate(
        EvGoalKind Kind,
        double TargetPercent,
        DateTimeOffset? Deadline,
        bool Hard,
        SourceSet? Sources,
        bool Escalate,
        TimeSpan Margin,
        bool Spread,
        string? TripKey)
    {
        public double NeedKwh { get; set; }
    }

    public static EvComposition Compose(EvComposerInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var p = input.Parameters;
        var notes = ImmutableArray.CreateBuilder<EvAssessmentCode>();
        if (input.DispatchOff)
        {
            notes.Add(EvAssessmentCode.WithoutPlannerDispatch);
        }

        if (input.CalendarUnavailable)
        {
            notes.Add(EvAssessmentCode.CalendarUnavailable);
        }

        if (input.RangeUnusable)
        {
            notes.Add(EvAssessmentCode.RangeUnusable);
        }

        if (input.ForecastMissing)
        {
            notes.Add(EvAssessmentCode.ForecastMissing);
        }

        EvComposition Nothing(EvAssessmentCode code) => new(code, notes.ToImmutable(), [], input.Frozen, false);

        if (input.ManualGoalActive)
        {
            return Nothing(EvAssessmentCode.Manual);
        }

        if (input.Plugged != true)
        {
            return Nothing(EvAssessmentCode.NotPlugged);
        }

        // A "don't charge now" is PV only for its time (2026-11-06): surplus may still charge, the grid may not.
        var pvOnly = input.Mode == EvMode.PvOnly || input.Hold;

        // Only the age of the estimate keeps the floor from planning (2026-11-06): it may use the older value, and it is then the one goal offered.
        var aged = input.SocPercent is null && !pvOnly && input.FloorSocPercent is not null;
        if ((aged ? input.FloorSocPercent : input.SocPercent) is not { } rawSoc || !double.IsFinite(rawSoc) || input.CapacityKwh <= 0 || !double.IsFinite(input.CapacityKwh))
        {
            return Nothing(EvAssessmentCode.SocStale);
        }

        var soc = Math.Clamp(rawSoc, 0, 100);
        var fullRequested = input.FullChargeRequested || input.Mode == EvMode.FullCharge;
        var midnight = NextLocalMidnight(input.Now, input.Zone);
        var floorBy = NextLocal(p.FloorBy, input.Now, input.Zone);
        var cheap = input.CheapZone;
        var cheapSources = cheap is null ? SourceSet.PvOnly : SourceSet.PvAnd([new ZoneId(cheap)]);
        var candidates = new List<Candidate>();
        FrozenTarget? frozen = input.Frozen;

        // The weekly full charge counts on any day (2026-11-06): the first time in a local week the estimate reaches its target, whatever the day; a requested
        // full charge counts when it reaches its own target, so the mode can end. An aged estimate records nothing.
        var fullTarget = fullRequested ? Math.Max(p.WeeklyFull.TargetPercent, 100) : p.WeeklyFull.TargetPercent;
        var weeklyReached = !aged
            && (fullRequested || (!p.WeeklyFull.Days.IsDefaultOrEmpty && p.WeeklyFull.TargetPercent > 0 && !FullThisWeek(input)))
            && GoalMath.IsMet(TargetSocRule.EnergyToDrawKwh(soc, fullTarget, input.CapacityKwh, p.LossFraction));

        if (aged)
        {
            notes.Add(EvAssessmentCode.SocStale);
            candidates.Add(new Candidate(EvGoalKind.Floor, p.FloorPercent, floorBy, true, cheapSources, false, TimeSpan.Zero, false, null));
        }
        else if (pvOnly)
        {
            candidates.Add(CapCandidate(p, midnight));
        }
        else
        {
            if (input.NextTrip is { } trip && trip.Start > input.Now && double.IsFinite(trip.Km) && trip.Km >= 0
                && input.KwhPerKm > 0 && double.IsFinite(input.KwhPerKm))
            {
                var key = TripKey(trip);
                var fresh = TargetSocRule.ForTrip(trip.Km, input.KwhPerKm, input.CapacityKwh, p);
                frozen = TargetSocRule.Freeze(input.Frozen, key, fresh, input.Now, p);
                // PV and the cheapest zone, like the floor, whatever the charger's own sources say (2026-11-06: live, the charger is pv_only so the grid comes
                // only through the plan); a dearer zone only through escalation, for what the departure still needs.
                candidates.Add(new Candidate(
                    EvGoalKind.Trip, frozen.Target.Percent, trip.Start, true, cheapSources, true, p.ReadyMargin, p.Spread, key));
            }

            if (fullRequested || WeeklyFullWanted(input))
            {
                candidates.Add(new Candidate(EvGoalKind.WeeklyFull, fullTarget, midnight, false, cheapSources, false, TimeSpan.Zero, p.Spread, null));
            }

            // The cap is omitted when a trip or weekly goal asks for at least as much (EV.md B2): a soft PV-only goal must not own energy a hard goal is due.
            if (!candidates.Any(c => c.Kind is EvGoalKind.Trip or EvGoalKind.WeeklyFull && c.TargetPercent >= p.CapPercent))
            {
                candidates.Add(CapCandidate(p, midnight));
            }

            // The floor is the reserve, due at the next floor_by (2026-11-06): the planner places the window's forecast PV first and the rest in the earliest
            // cheap hours, so the night charges only what the sun is not expected to bring. A trip ready before that moves it there (start less ready margin),
            // so the floor takes the first points and the trip asks only for the points above it; those points were the trip's, so the floor then escalates
            // like the trip. A ready moment already passed, or one that meets another goal's deadline and would merge into it, leaves no floor deadline before
            // the departure, so the trip carries the whole way from the SoC.
            var tripGoal = candidates.FirstOrDefault(c => c.Kind == EvGoalKind.Trip);
            var ready = tripGoal?.Deadline - tripGoal?.Margin;
            var beforeTrip = ready is { } readyAt && readyAt < floorBy;
            var floorDue = beforeTrip ? ready!.Value : floorBy;
            if (floorDue > input.Now && !candidates.Any(c => c.Deadline == floorDue))
            {
                candidates.Add(new Candidate(EvGoalKind.Floor, p.FloorPercent, floorDue, true, cheapSources, beforeTrip, TimeSpan.Zero, false, null));
            }
        }

        // Incremental needs: ascending by target, the floor first among equals (it is held before anything else is due), then a tie goes to the goal that ranks
        // first (it takes the increment, the others get zero).
        var ordered = candidates.OrderBy(c => c.TargetPercent).ThenBy(c => c.Kind == EvGoalKind.Floor ? 0 : 1).ThenBy(c => c.Kind).ToList();
        var basePercent = soc;
        foreach (var c in ordered)
        {
            c.NeedKwh = TargetSocRule.EnergyToDrawKwh(basePercent, c.TargetPercent, input.CapacityKwh, p.LossFraction);
            basePercent = Math.Max(basePercent, c.TargetPercent);
        }

        var needs = candidates.ToDictionary(c => c.Kind, c => c.NeedKwh);
        var code = AssessmentCode(pvOnly ? EvMode.PvOnly : input.Mode, needs);

        // Unique deadlines: merge at one deadline into the winner (trip, weekly, cap order).
        var kept = new List<Candidate>();
        foreach (var group in candidates.GroupBy(c => c.Deadline))
        {
            var winner = group.OrderBy(c => c.Kind).First();
            if (group.Key is not null && winner.Kind != EvGoalKind.Trip)
            {
                winner.NeedKwh = group.Sum(c => c.NeedKwh);
            }

            kept.Add(winner);
        }

        var emitted = kept.Where(c => !GoalMath.IsMet(c.NeedKwh)).ToList();
        if (emitted.Count == 0)
        {
            // Everything met: keep the highest target (the one that ranks first among equals) so its stop holds the device off.
            emitted.Add(kept.OrderByDescending(c => c.TargetPercent).ThenBy(c => c.Kind).First());
        }

        var caps = PowerBands.Windows(input.Parameters.PowerBands, input.Now, input.Now + input.PlanHorizon, input.Zone)
            .Select(w => new PowerCap(w.From, w.To, w.MaxW))
            .ToImmutableArray();
        var goals = emitted
            .OrderBy(c => c.Deadline is null ? 1 : 0).ThenBy(c => c.Deadline).ThenBy(c => c.Kind)
            .Select(c => ToOffer(c, input, soc, caps))
            .ToImmutableArray();
        return new EvComposition(code, notes.ToImmutable(), goals, frozen, weeklyReached);
    }

    private static Candidate CapCandidate(EvParameters p, DateTimeOffset midnight) =>
        new(EvGoalKind.Cap, p.CapPercent, midnight, false, SourceSet.PvOnly, false, TimeSpan.Zero, false, null);

    private static EvAssessmentCode AssessmentCode(EvMode mode, Dictionary<EvGoalKind, double> needs)
    {
        if (mode == EvMode.PvOnly)
        {
            return EvAssessmentCode.PvOnly;
        }

        if (mode == EvMode.FullCharge)
        {
            return EvAssessmentCode.Manual;
        }

        return needs.ContainsKey(EvGoalKind.Trip) ? EvAssessmentCode.TripDeadline
            : needs.TryGetValue(EvGoalKind.WeeklyFull, out var weekly) && !GoalMath.IsMet(weekly) ? EvAssessmentCode.WeeklyFull
            : needs.TryGetValue(EvGoalKind.Floor, out var floor) && !GoalMath.IsMet(floor) ? EvAssessmentCode.CheapZone
            : needs.TryGetValue(EvGoalKind.Cap, out var cap) && !GoalMath.IsMet(cap) ? EvAssessmentCode.Surplus
            : EvAssessmentCode.FloorHold;
    }

    private static EvGoalOffer ToOffer(Candidate c, EvComposerInput input, double soc, ImmutableArray<PowerCap> caps)
    {
        var request = new GoalRequest(
            input.Device,
            new SocTarget(c.TargetPercent, soc),
            c.NeedKwh,
            c.Deadline,
            c.Margin,
            c.Hard,
            c.Sources,
            c.Escalate,
            GoalOnMet.Stop,
            GoalOrigin.Module,
            Revision(c, input),
            PowerCaps: caps);
        // Only the cap is quiet (B12: a PV-only goal a cloudy evening cannot fill); a weekly full charge left short is a real shortfall.
        return new EvGoalOffer(c.Kind, request, c.Spread, QuietShortfall: c.Kind == EvGoalKind.Cap);
    }

    /// <summary>FNV-1a over what the goal is: no target, no SoC, no deadline of a daily goal (B3); a trip is its key.</summary>
    private static long Revision(Candidate c, EvComposerInput input)
    {
        var text = new StringBuilder();
        void Add(string? part) => text.Append(part).Append('\n');
        Add(input.Device.Value);
        Add(c.Kind.ToString());
        Add(c.TripKey);
        Add(c.Hard ? "hard" : "soft");
        Add(c.Sources?.ToText());
        Add(c.Escalate ? "escalate" : "stay");
        Add(GoalOnMet.Stop.ToString());
        Add(input.CapacityKwh.ToString("R", CultureInfo.InvariantCulture));
        Add(input.Parameters.LossFraction.ToString("R", CultureInfo.InvariantCulture));
        Add(c.Margin.ToString("c", CultureInfo.InvariantCulture));
        Add(c.Spread ? "spread" : "flat");

        return Fnv1a.Signed(text.ToString());
    }

    private static bool WeeklyFullWanted(EvComposerInput input)
    {
        if (input.FullChargeRequested)
        {
            return true;
        }

        var p = input.Parameters;
        var local = TimeZoneInfo.ConvertTime(input.Now, input.Zone);
        var today = DateOnly.FromDateTime(local.DateTime);
        if (p.WeeklyFull.Days.IsDefaultOrEmpty || !TargetSocRule.WeeklyFullAllowed(input.Now, input.Zone, input.IsHoliday(today), p))
        {
            return false;
        }

        return !FullThisWeek(input);
    }

    /// <summary>A full charge was recorded in the current local week.</summary>
    private static bool FullThisWeek(EvComposerInput input)
    {
        if (input.LastFullAt is not { } last)
        {
            return false;
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(input.Now, input.Zone).DateTime);
        var lastDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(last, input.Zone).DateTime);
        return WeekStart(lastDate) == WeekStart(today);
    }

    // Monday of the local week: "once a week" is the calendar week of the add-on's zone.
    private static DateOnly WeekStart(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    // Cut to what ev.json keeps (EvStateFile.MaxTripKeyLength is 200): start to the second, then the title, without splitting a surrogate pair.
    private static string TripKey(Trip trip)
    {
        const int titleLimit = 64;
        var title = trip.Title ?? string.Empty;
        if (title.Length > titleLimit)
        {
            title = char.IsHighSurrogate(title[titleLimit - 1]) ? title[..(titleLimit - 1)] : title[..titleLimit];
        }

        return trip.Start.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + "|" + title;
    }

    /// <summary>
    /// The next 00:00 in <paramref name="zone"/> after <paramref name="now"/>. A midnight that does not exist moves one hour on, one that happens twice
    /// is the first (the series rule); never throws.
    /// </summary>
    public static DateTimeOffset NextLocalMidnight(DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return LocalTime.At(TimeZoneInfo.ConvertTime(now, zone).Date.AddDays(1), zone);
    }

    /// <summary>The next occurrence of a local time of day strictly after <paramref name="now"/> (a missing time moves past the gap, a repeated one is the first).</summary>
    public static DateTimeOffset NextLocal(TimeOnly at, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var date = TimeZoneInfo.ConvertTime(now, zone).Date;
        for (var day = 0; day < 3; day++)
        {
            var candidate = LocalTime.At(date.AddDays(day) + at.ToTimeSpan(), zone);
            if (candidate > now)
            {
                return candidate;
            }
        }

        // Unreachable: three consecutive local days always hold an instant after now.
        return LocalTime.At(date.AddDays(3) + at.ToTimeSpan(), zone);
    }
}
