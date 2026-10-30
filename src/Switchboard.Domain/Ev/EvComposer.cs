using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Goals;

namespace Switchboard.Domain.Ev;

public enum EvGoalKind
{
    /// <summary>The next calendar trip: hard, due at the trip's start, ready <c>ready_margin</c> before it.</summary>
    Trip,

    /// <summary>The weekly (or requested) full charge: soft, due at the next local midnight, PV first and then the cheapest zone.</summary>
    WeeklyFull,

    /// <summary>The surplus cap: soft, due at the next local midnight, PV only, stops when met.</summary>
    Cap,

    /// <summary>The floor: hard, no deadline, the cheapest zone only, no escalation.</summary>
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
/// <param name="WeeklyFullReached">The SoC is at the weekly full target on a day that counts: the caller records <c>LastFullAt</c>.</param>
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

    /// <summary>A manual goal exists for the device: the module offers nothing (B8).</summary>
    public bool ManualGoalActive { get; init; }

    /// <summary>True when plugged or when no plugged signal is configured; false when unplugged; null when the state is unknown or unavailable (no goal).</summary>
    public required bool? Plugged { get; init; }

    /// <summary>The SoC estimate; null when there is none (stale, drifted, no reading): no goals, never the last value.</summary>
    public required double? SocPercent { get; init; }

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

    /// <summary>The device's own <c>sources</c>, used for the trip goal; null when it declares none.</summary>
    public SourceSet? DeviceSources { get; init; }

    /// <summary>The cheapest tariff zone id (configuration, never a name in code); null when there is none.</summary>
    public string? CheapZone { get; init; }

    public bool CalendarUnavailable { get; init; }

    public bool RangeUnusable { get; init; }

    public bool ForecastMissing { get; init; }

    public bool DispatchOff { get; init; }
}

/// <summary>
/// Turns the car's situation into at most four goals (EV-06, docs/EV.md sections 3, 4 and 2a). Pure.
/// <list type="bullet">
/// <item>Needs are incremental (B2): goals are absolute SoC targets against the same SoC, so each goal is asked only for the part above the goals with a
/// lower target; the sum is exactly (highest target - SoC) x capacity x (1 + loss), never more. The cap is not offered at all when a trip or weekly goal asks
/// for at least as much, so a soft PV-only goal never owns energy a hard goal is due.</item>
/// <item>Deadlines are unique (B8): the trip is its start, the weekly and cap goals the next local midnight, the floor none. Goals that meet at one deadline
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

        if (input.SocPercent is not { } rawSoc || !double.IsFinite(rawSoc) || input.CapacityKwh <= 0 || !double.IsFinite(input.CapacityKwh))
        {
            return Nothing(EvAssessmentCode.SocStale);
        }

        var soc = Math.Clamp(rawSoc, 0, 100);
        var fullRequested = input.FullChargeRequested || input.Mode == EvMode.FullCharge;
        var midnight = NextLocalMidnight(input.Now, input.Zone);
        var cheap = input.CheapZone;
        var cheapSources = cheap is null ? SourceSet.PvOnly : new SourceSet(true, false, [cheap]);
        var candidates = new List<Candidate>();
        FrozenTarget? frozen = input.Frozen;
        var weeklyReached = false;

        if (input.Mode == EvMode.PvOnly)
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
                candidates.Add(new Candidate(
                    EvGoalKind.Trip, frozen.Target.Percent, trip.Start, true, input.DeviceSources, true, p.ReadyMargin, p.Spread, key));
            }

            if (fullRequested || WeeklyFullWanted(input))
            {
                var target = fullRequested ? Math.Max(p.WeeklyFull.TargetPercent, 100) : p.WeeklyFull.TargetPercent;
                weeklyReached = soc >= target - GoalMath.MetEpsilonKwh;
                candidates.Add(new Candidate(EvGoalKind.WeeklyFull, target, midnight, false, cheapSources, false, TimeSpan.Zero, p.Spread, null));
            }

            // The cap is omitted when a trip or weekly goal asks for at least as much (EV.md B2): a soft PV-only goal must not own energy a hard goal is due.
            if (!candidates.Any(c => c.Kind is EvGoalKind.Trip or EvGoalKind.WeeklyFull && c.TargetPercent >= p.CapPercent))
            {
                candidates.Add(CapCandidate(p, midnight));
            }

            // The floor has no deadline: under a trip that asks for at least as much it would take the first points of the trip's energy and the planner would
            // place them after the departure (the car leaves short), so the trip carries the whole way from the SoC.
            if (!candidates.Any(c => c.Kind == EvGoalKind.Trip && c.TargetPercent >= p.FloorPercent))
            {
                candidates.Add(new Candidate(EvGoalKind.Floor, p.FloorPercent, null, true, cheapSources, false, TimeSpan.Zero, false, null));
            }
        }

        // Incremental needs: ascending by target, a tie going to the goal that ranks first (it takes the increment, the others get zero).
        var ordered = candidates.OrderBy(c => c.TargetPercent).ThenBy(c => c.Kind).ToList();
        var basePercent = soc;
        foreach (var c in ordered)
        {
            c.NeedKwh = TargetSocRule.EnergyToDrawKwh(basePercent, c.TargetPercent, input.CapacityKwh, p.LossFraction);
            basePercent = Math.Max(basePercent, c.TargetPercent);
        }

        var needs = candidates.ToDictionary(c => c.Kind, c => c.NeedKwh);
        var code = AssessmentCode(input.Mode, needs);

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

        var goals = emitted
            .OrderBy(c => c.Deadline is null ? 1 : 0).ThenBy(c => c.Deadline).ThenBy(c => c.Kind)
            .Select(c => ToOffer(c, input, soc))
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

    private static EvGoalOffer ToOffer(Candidate c, EvComposerInput input, double soc)
    {
        var request = new GoalRequest(
            input.Device,
            GoalKind.Soc,
            c.NeedKwh,
            TargetKwh: null,
            TargetSoc: c.TargetPercent,
            CurrentSoc: soc,
            DeliveredKwh: null,
            c.Deadline,
            c.Margin,
            c.Hard,
            c.Sources,
            c.Escalate,
            GoalOnMet.Stop,
            Since: null,
            GoalOrigin.Module,
            Revision(c, input));
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

        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var b in Encoding.UTF8.GetBytes(text.ToString()))
        {
            hash = (hash ^ b) * prime;
        }

        return unchecked((long)hash);
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

        if (input.LastFullAt is { } last)
        {
            var lastDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(last, input.Zone).DateTime);
            if (WeekStart(lastDate) == WeekStart(today))
            {
                return false;
            }
        }

        return true;
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
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var naive = DateTime.SpecifyKind(local.Date.AddDays(1), DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(naive))
        {
            naive = naive.AddHours(1);
        }

        var offset = zone.IsAmbiguousTime(naive) ? zone.GetAmbiguousTimeOffsets(naive).Max() : zone.GetUtcOffset(naive);
        return new DateTimeOffset(naive, offset);
    }
}
