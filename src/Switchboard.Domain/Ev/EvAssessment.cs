namespace Switchboard.Domain.Ev;

/// <summary>
/// The EV module's own closed assessment list (docs/EV.md section 3 point 5), like <c>PlanNote</c>: why the module wants what it wants. It is not a
/// reason code; <c>ReasonCode</c> stays at list version 7 and dispatch decisions about the car keep their v7 codes. Every assessment state, including
/// "nothing to do", carries exactly one primary code.
/// </summary>
public enum EvAssessmentCode
{
    /// <summary>A trip with a deadline drives the plan.</summary>
    TripDeadline,

    /// <summary>Only PV surplus charges the car (the cap goal).</summary>
    Surplus,

    /// <summary>The floor is below the target and is being charged from the cheapest zone.</summary>
    CheapZone,

    /// <summary>The floor holds; nothing is owed to the car.</summary>
    FloorHold,

    /// <summary>The weekly (or requested) full charge is wanted.</summary>
    WeeklyFull,

    /// <summary>The car is not plugged in, or the plugged state is unavailable: no goal, nothing reserved.</summary>
    NotPlugged,

    /// <summary>No usable PV forecast (note).</summary>
    ForecastMissing,

    /// <summary>A manual goal for the device suppresses the module's goals.</summary>
    Manual,

    /// <summary>The SoC estimate is stale or unavailable: goals are inactive, never the last value.</summary>
    SocStale,

    /// <summary>The calendar is unavailable; the last parsed trip is kept until its start (note).</summary>
    CalendarUnavailable,

    /// <summary>The range is stale, zero or unknown; the last smoothed or fallback consumption is used (note).</summary>
    RangeUnusable,

    /// <summary>Mode "PV only": no grid and no floor.</summary>
    PvOnly,

    /// <summary>Mode "Off": the user's force-off, never set by the module.</summary>
    Off,

    /// <summary>The module computes a preview only because <c>planner.dispatch</c> is off (note).</summary>
    WithoutPlannerDispatch,
}

public static class EvAssessmentCodes
{
    /// <summary>The closed list in its documented order.</summary>
    public static IReadOnlyList<EvAssessmentCode> All { get; } = Enum.GetValues<EvAssessmentCode>();

    /// <summary>The wire text (<c>ev_trip_deadline</c>, ...), the same in the API and the UI.</summary>
    public static string Text(EvAssessmentCode code) => code switch
    {
        EvAssessmentCode.TripDeadline => "ev_trip_deadline",
        EvAssessmentCode.Surplus => "ev_surplus",
        EvAssessmentCode.CheapZone => "ev_cheap_zone",
        EvAssessmentCode.FloorHold => "ev_floor_hold",
        EvAssessmentCode.WeeklyFull => "ev_weekly_full",
        EvAssessmentCode.NotPlugged => "ev_not_plugged",
        EvAssessmentCode.ForecastMissing => "ev_forecast_missing",
        EvAssessmentCode.Manual => "ev_manual",
        EvAssessmentCode.SocStale => "ev_soc_stale",
        EvAssessmentCode.CalendarUnavailable => "ev_calendar_unavailable",
        EvAssessmentCode.RangeUnusable => "ev_range_unusable",
        EvAssessmentCode.PvOnly => "ev_pv_only",
        EvAssessmentCode.Off => "ev_off",
        EvAssessmentCode.WithoutPlannerDispatch => "ev_without_planner_dispatch",
        _ => throw new ArgumentOutOfRangeException(nameof(code)),
    };
}
