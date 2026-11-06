using System.Text.RegularExpressions;
using Switchboard.Application.Configuration;
using Switchboard.Application.Ev;
using Switchboard.Application.Signals;
using Switchboard.Domain.Ev;

namespace Switchboard.Application.Reports;

/// <summary>The module's assessment as GET api/ev reports it: a failed computation (nothing offered), or a snapshot and whether it is past its validity.</summary>
/// <param name="Snapshot">The snapshot; null when the computation failed.</param>
/// <param name="Expired">The snapshot is past <c>ValidUntil</c> (the guard no longer sees its goals); always true for a failed computation.</param>
public sealed record EvAssessmentReport(bool Failed, EvSnapshot? Snapshot, bool Expired);

/// <summary>The user's side of the module as GET api/ev reports it: the mode, and the charge-now and "don't charge now" while they still run (null once expired or when none was started).</summary>
public sealed record EvManualReport(EvMode Mode, EvChargeNow? Running, EvHold? Hold = null);

/// <summary>
/// What GET api/ev says, as the application decided it and before it is worded: the configuration it stands on, the configuration issues (the calendar zone
/// mismatch included), how the inputs were read, the trip assessment, the module's assessment and the user's controls. The web layer only maps it to its response.
/// </summary>
/// <param name="Configuration">The valid configuration; null without one.</param>
/// <param name="Issues">Configuration issues under <c>ev</c> and <c>control.ev</c>, errors first, then the calendar zone mismatch when there is one.</param>
/// <param name="AddOnZone">The add-on's zone id; null while the report is composed without one.</param>
/// <param name="Inputs">The EV inputs; null without an <c>ev:</c> section or inputs.</param>
/// <param name="Trips">The calendar event judged now; null where <paramref name="Inputs"/> is.</param>
/// <param name="Assessment">Null without an <c>ev:</c> section or before the first computation.</param>
/// <param name="Manual">Null without an <c>ev:</c> section or without the manual state.</param>
public sealed partial record EvReport(
    DateTimeOffset At,
    SwitchboardConfiguration? Configuration,
    IReadOnlyList<ConfigurationIssue> Issues,
    string? AddOnZone,
    EvInputs? Inputs,
    TripAssessment? Trips,
    EvAssessmentReport? Assessment,
    EvManualReport? Manual)
{
    /// <param name="inputs">The EV inputs of the latest store cycle; null composes the report from the configuration alone.</param>
    /// <param name="zone">The add-on's zone, the one the calendar's naive local times are read in; null leaves the zone out.</param>
    /// <param name="published">The module's latest snapshot; null (or nothing computed yet) leaves the assessment out.</param>
    public static EvReport Compose(ConfigurationResult? result, DateTimeOffset now, EvInputs? inputs = null, TimeZoneInfo? zone = null, EvPublished? published = null, EvManual? manual = null)
    {
        var configuration = result?.Configuration;
        var ev = configuration?.Ev;
        var issues = IssuesOf(result);
        TripAssessment? trips = null;
        if (ev is not null && inputs is not null)
        {
            trips = TripParser.Assess(inputs.CalendarEvent, now, configuration!.Control.Ev.MaxTripKm);
            if (zone is not null && ZoneDisagrees(inputs.CalendarZoneName, zone, inputs.CalendarEvent?.Start ?? now))
            {
                issues = [.. issues, ZoneIssue(inputs.CalendarZoneName!, zone)];
            }
        }

        return new EvReport(
            now,
            configuration,
            issues,
            zone?.Id,
            ev is not null && inputs is not null ? inputs : null,
            trips,
            ev is null ? null : AssessmentOf(published, now),
            ev is null || manual is null ? null : ManualOf(manual, now));
    }

    /// <summary>
    /// True when the calendar entity states a zone of its own that gives a different offset from the add-on's at <paramref name="at"/>. Two names of one zone
    /// (an IANA id and a Windows id) agree; a name this host does not know is a disagreement, because the times cannot be shown to be right.
    /// </summary>
    public static bool ZoneDisagrees(string? stated, TimeZoneInfo addOn, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(stated) || string.Equals(stated, addOn.Id, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(stated).GetUtcOffset(at) != addOn.GetUtcOffset(at);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return true;
        }
    }

    private static ConfigurationIssue ZoneIssue(string stated, TimeZoneInfo addOn) =>
        new(
            ConfigurationIssueCodes.EvCalendarZoneMismatch,
            "ev.calendar",
            null,
            null,
            $"The calendar entity says its times are in '{stated}', the add-on reads them in '{addOn.Id}'; a trip would start at the wrong hour. Set the add-on's time zone to the calendar's.");

    private static EvManualReport ManualOf(EvManual manual, DateTimeOffset now) =>
        new(manual.Mode, manual.ChargeNow is { } running && now < running.ExpiresAt ? running : null, manual.Hold is { } hold && now < hold.ExpiresAt ? hold : null);

    private static EvAssessmentReport? AssessmentOf(EvPublished? published, DateTimeOffset now)
    {
        if (published is null)
        {
            return null;
        }

        if (published.Failed)
        {
            return new EvAssessmentReport(true, null, true);
        }

        return published.Snapshot is { } snapshot ? new EvAssessmentReport(false, snapshot, now > snapshot.ValidUntil) : null;
    }

    private static IReadOnlyList<ConfigurationIssue> IssuesOf(ConfigurationResult? result) =>
        result is null
            ? []
            : [.. result.Errors.Concat(result.Warnings).Where(i => EvPath().IsMatch(i.Path))];

    [GeneratedRegex(@"^(ev|control\.ev)([.\[]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex EvPath();
}

/// <summary>Reads the EV report from the live sources; the one place GET api/ev gets its answer from.</summary>
public sealed class EvReader(IEvInputSource inputs, IEvSnapshotSource snapshots, IEvControl control, SignalClock clock)
{
    /// <param name="configuration">The configuration in force (valid or not); null before the first load.</param>
    public EvReport Read(ConfigurationResult? configuration) =>
        EvReport.Compose(configuration, clock.Time.GetUtcNow(), inputs.Current.Inputs, clock.Time.LocalTimeZone, snapshots.Current, control.Manual);
}
