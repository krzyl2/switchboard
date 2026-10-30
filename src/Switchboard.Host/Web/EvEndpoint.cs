using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Switchboard.Application.Configuration;
using Switchboard.Application.Ev;
using Switchboard.Application.Signals;
using Switchboard.Domain.Ev;
using Switchboard.Domain.Planning;

namespace Switchboard.Host.Web;

/// <summary>
/// GET api/ev (EV-01, docs/EV.md S1; read-only, ingress only, no new port; nothing here changes a decision): whether an <c>ev:</c> section is
/// in force, what it says and the parameters it will run with, and the configuration issues under <c>ev</c> and <c>control.ev</c>. From S2 on it also
/// shows how each EV input was read (B7), the next trip the calendar names, the events it ignored and the add-on zone; there is still no plan or assessment:
/// the module is not built yet, and the answer must not look as if it were.
/// </summary>
/// <param name="Enabled">True when the configuration is valid and holds a usable <c>ev:</c> section (a section whose device has its own goal is not usable).</param>
/// <param name="Config">The section as read; null when not enabled.</param>
/// <param name="Parameters">The <c>control.ev</c> parameters in force; null when there is no valid configuration.</param>
/// <param name="Issues">Configuration issues under <c>ev</c> and <c>control.ev</c>, errors first.</param>
/// <param name="AddOnZone">The zone the add-on reads the calendar's naive local times in (the one of the slot grid); null while the answer is built without one.</param>
/// <param name="Inputs">How each EV input was read (EV-02, B7): read time, freshness, value. Null without an <c>ev:</c> section.</param>
/// <param name="Trips">The next trip, the events that were ignored and the warnings (EV-03). Null without an <c>ev:</c> section.</param>
/// <param name="Assessment">What the module offers now and why (EV-06, EV-07); null without an <c>ev:</c> section or before the first computation.</param>
public sealed record EvResponse(
    DateTimeOffset At,
    bool Enabled,
    EvConfigDto? Config,
    EvParametersDto? Parameters,
    IReadOnlyList<ConfigurationIssue> Issues,
    string? AddOnZone = null,
    EvInputsDto? Inputs = null,
    EvTripsDto? Trips = null,
    EvAssessmentDto? Assessment = null,
    EvManualDto? Manual = null);

/// <summary>The user's side of the module (EV-09): what the user set, read from memory at once (the assessment follows on the module's next computation).</summary>
/// <param name="Mode">auto, pv_only or full_charge. "Off" is the device's own force_off override, not a mode.</param>
public sealed record EvManualDto(string Mode, EvChargeNowDto ChargeNow);

/// <param name="Active">A charge-now is running.</param>
/// <param name="TargetPct">The SoC it runs to; null when none is running.</param>
/// <param name="Until">When it ends at the latest (it also ends at its target or when the car is unplugged); null when none is running.</param>
public sealed record EvChargeNowDto(bool Active, double? TargetPct, DateTimeOffset? Until);

/// <summary>What POST api/ev/charge-now started.</summary>
public sealed record EvChargeNowResponse(bool Active, double TargetPct, DateTimeOffset Until);

/// <summary>One goal the module offers; the percentage is the SoC the goal runs to, the need is the energy still to draw (loss included).</summary>
/// <param name="Kind">trip, weekly_full, cap or floor.</param>
/// <param name="Sources">The classes the goal may be scheduled on; null means the device's.</param>
/// <param name="Spread">The plan spreads the energy over the window instead of running at the ceiling.</param>
public sealed record EvOfferedGoalDto(string Kind, double TargetPercent, double RemainingKwh, DateTimeOffset? Deadline, bool Hard, string? Sources, bool Spread);

/// <summary>
/// The module's assessment (a closed list of its own, see <c>EvAssessmentCode</c>; <c>ReasonCode</c> is untouched). A failed computation says so and offers nothing;
/// a snapshot past <paramref name="ValidUntil"/> is <paramref name="Expired"/>: the guard no longer sees its goals.
/// </summary>
/// <param name="Failed">The last computation threw: the module offers no goals (the car is v2) until it recovers.</param>
/// <param name="Reason">The primary code (<c>ev_trip_deadline</c>, ...); null when the computation failed.</param>
/// <param name="Notes">Conditions on top of the reason: calendar unavailable, range unusable, forecast missing, no planner dispatch.</param>
/// <param name="Mode">auto, pv_only or full_charge.</param>
/// <param name="SocGap">Why there is no SoC estimate (no_reading, too_old, drift_exceeded, charger_stale, invalid_capacity).</param>
/// <param name="KwhPerKm">The consumption the target was computed with.</param>
public sealed record EvAssessmentDto(
    bool Failed,
    string? Reason,
    IReadOnlyList<string> Notes,
    DateTimeOffset? ComputedAt,
    DateTimeOffset? ValidUntil,
    bool Expired,
    string? Mode,
    double? SocPercent,
    string? SocGap,
    double? KwhPerKm,
    EvTripDto? NextTrip,
    DateTimeOffset? LastFullAt,
    IReadOnlyList<EvOfferedGoalDto> Goals);

/// <param name="Configured">False when the section declares no such input.</param>
/// <param name="State">The signal's state (fresh, stale, rejected, unavailable, unmapped).</param>
/// <param name="Detail">Why, in the closed list of signal details.</param>
/// <param name="ReadAt">Home Assistant's last report of the value in use (<c>last_reported</c>).</param>
/// <param name="AgeS">Seconds since <paramref name="ReadAt"/>.</param>
/// <param name="MaxAgeS">The age the input is judged by; also set for a state that reports on change, where it is not applied.</param>
public sealed record EvReadDto(bool Configured, string State, string Detail, DateTimeOffset? ReadAt, double? AgeS, double? MaxAgeS);

/// <summary>The EV inputs; a value is present only while its input is fresh.</summary>
/// <param name="PluggedState">The plugged entity's own state text, for display.</param>
public sealed record EvInputsDto(
    EvReadDto Soc,
    double? SocPercent,
    EvReadDto Range,
    double? RangeKm,
    EvReadDto Plugged,
    bool? IsPlugged,
    string? PluggedState,
    EvReadDto Calendar);

public sealed record EvTripDto(string Title, DateTimeOffset Start, double Km);

public sealed record EvUnparsedDto(string Title, DateTimeOffset Start, string Reason, double? Km);

public sealed record EvTripWarningDto(string Kind, string Title, DateTimeOffset Start);

/// <param name="NextTrip">The one trip the plan will be made for; null when there is none (no calendar, no event, or the event was ignored).</param>
/// <param name="Unparsed">Ignored events: all-day, no <c>km:</c>, <c>km:</c> out of bounds.</param>
/// <param name="Warnings">An ignored event still ahead may hide the trip behind it: the calendar entity shows one event.</param>
/// <param name="CalendarZone">The zone the calendar entity states for its times, when it states one.</param>
public sealed record EvTripsDto(EvTripDto? NextTrip, IReadOnlyList<EvUnparsedDto> Unparsed, IReadOnlyList<EvTripWarningDto> Warnings, string? CalendarZone);

/// <param name="Device">The device id that charges the car.</param>
/// <param name="SocSignal">The logical name of the SoC signal (never an entity ID).</param>
/// <param name="CheapZone">The configured cheap zone id; null means the lowest price in the price book.</param>
/// <param name="PlannerDispatch">planner.dispatch as configured (off, narrow or full); null without a planner: section.</param>
/// <param name="RangeSignal">The logical name of the electric-range signal; null when not declared.</param>
/// <param name="PluggedSignal">The logical name of the plugged-state signal; null when not declared.</param>
/// <param name="PluggedStates">The states of the plugged signal that mean plugged in.</param>
/// <param name="CalendarSignal">The logical name of the calendar signal; null when not declared.</param>
public sealed record EvConfigDto(
    string Device,
    double CapacityKwh,
    string SocSignal,
    string? CheapZone,
    string? PlannerDispatch,
    string? RangeSignal = null,
    string? PluggedSignal = null,
    IReadOnlyList<string>? PluggedStates = null,
    string? CalendarSignal = null);

/// <summary>The <c>control.ev</c> parameters; durations in seconds.</summary>
public sealed record EvParametersDto(
    double FloorPercent,
    double FloorAfterPercent,
    double ReservePoints,
    double CapPercent,
    IReadOnlyList<string> WeeklyFullDays,
    double WeeklyFullTargetPercent,
    double ReadyMarginS,
    double LossFraction,
    double FallbackKwhPerKm,
    double LearnSmoothing,
    double LearnMinSocPercent,
    double LearnMinKwhPerKm,
    double LearnMaxKwhPerKm,
    int LearnMaxSamples,
    double LearnPairToleranceS,
    double MaxTripKm,
    double SocMaxAgeS,
    double DriftLimitKwh,
    double TargetChangeThresholdPoints,
    double ChargeNowTargetPercent,
    double ChargeNowDurationS,
    bool Spread,
    double SnapshotMaxAgeS);

internal static partial class EvEndpoint
{
    public static IEndpointRouteBuilder MapEvApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/ev", (
            [FromServices] ISignalDiagnosticsSource diagnostics,
            [FromServices] IEvInputSource inputs,
            [FromServices] IEvSnapshotSource snapshots,
            [FromServices] IEvControl control,
            [FromServices] SignalClock clock) => Results.Json(Build(diagnostics.Current.Configuration, clock.Time.GetUtcNow(), inputs.Current.Inputs, clock.Time.LocalTimeZone, snapshots.Current, control.Manual)));

        // The user's controls (EV-09). Same protection as POST api/goals: ingress only, a JSON body (a cross-site form cannot send one), a bounded strict body.
        // They change only the add-on's own state; no entity is written.
        endpoints.MapPost("/api/ev/mode", async (
            HttpRequest request,
            [FromServices] IEvControl control,
            CancellationToken cancellationToken) =>
        {
            if (!request.HasJsonContentType())
            {
                return Refused(StatusCodes.Status400BadRequest, BadRequest, "Send Content-Type: application/json with the body {\"mode\": \"auto\" | \"pv_only\" | \"full_charge\"}.");
            }

            var body = await ReadObjectAsync(request, cancellationToken).ConfigureAwait(false);
            if (body is null || body.Value.EnumerateObject().Count() != 1 || !body.Value.TryGetProperty("mode", out var text) || text.ValueKind != JsonValueKind.String
                || Enum.GetValues<EvMode>().Where(m => ApiText.Snake(m) == text.GetString()).Select(m => (EvMode?)m).SingleOrDefault() is not { } mode)
            {
                return Refused(StatusCodes.Status400BadRequest, BadRequest, "Send exactly {\"mode\": \"auto\" | \"pv_only\" | \"full_charge\"}. Off is the device's own force_off override.");
            }

            return control.SetMode(mode) switch
            {
                EvControlResult.NotConfigured => Refused(StatusCodes.Status404NotFound, NotConfigured, "There is no ev: section in the valid configuration."),
                _ => Results.Json(new EvManualMode(ApiText.Snake(control.Manual.Mode))),
            };
        });

        endpoints.MapPost("/api/ev/charge-now", async (
            HttpRequest request,
            [FromServices] IEvControl control,
            CancellationToken cancellationToken) =>
        {
            if (!request.HasJsonContentType())
            {
                return Refused(StatusCodes.Status400BadRequest, BadRequest, "Send Content-Type: application/json with the body {} or {\"targetPct\": number, \"durationMinutes\": number}.");
            }

            var body = await ReadObjectAsync(request, cancellationToken).ConfigureAwait(false);
            if (body is null || !TryOptionalNumber(body.Value, out var target, out var minutes))
            {
                return Refused(StatusCodes.Status400BadRequest, BadRequest, "Send {} or an object with only targetPct (percent) and durationMinutes (minutes), both numbers.");
            }

            // A duration too long for a TimeSpan is still a positive one: it is refused by the horizon check (deadline_beyond_horizon), not called "not above zero".
            TimeSpan? duration = minutes switch
            {
                null => null,
                { } m when double.IsFinite(m) && m > 0 => m <= TimeSpan.MaxValue.TotalMinutes / 2 ? TimeSpan.FromMinutes(m) : TimeSpan.MaxValue / 2,
                _ => TimeSpan.Zero,
            };
            switch (control.StartChargeNow(target, duration))
            {
                case EvControlResult.NotConfigured:
                    return Refused(StatusCodes.Status404NotFound, NotConfigured, "There is no ev: section in the valid configuration.");
                case EvControlResult.NeedsFullDispatch:
                    return Refused(StatusCodes.Status409Conflict, NeedsFullDispatch, "Charge now needs planner.dispatch: full, because only then does the plan place goals; use the device's force_on override instead.");
                case EvControlResult.NotPlugged:
                    return Refused(StatusCodes.Status409Conflict, NotPlugged, "The car is not plugged in.");
                case EvControlResult.SocUnknown:
                    return Refused(StatusCodes.Status409Conflict, SocUnknown, "There is no usable state-of-charge estimate for the car, so the energy cannot be planned.");
                case EvControlResult.AlreadyAtTarget:
                    return Refused(StatusCodes.Status409Conflict, AlreadyAtTarget, "The car is already at or above that target.");
                case EvControlResult.TargetOutOfRange:
                    return Refused(StatusCodes.Status422UnprocessableEntity, TargetOutOfRange, "The target must be above 0 and at most 100 percent.");
                case EvControlResult.DurationOutOfRange:
                    return Refused(StatusCodes.Status422UnprocessableEntity, DurationOutOfRange, "The duration must be above zero.");
                case EvControlResult.DeadlineBeyondHorizon:
                    return Refused(StatusCodes.Status422UnprocessableEntity, DeadlineBeyondHorizon, "The charge would end beyond the planning horizon (control.planner.horizon); ask for a shorter duration.");
            }

            var running = control.Manual.ChargeNow;
            return running is null
                ? Refused(StatusCodes.Status409Conflict, AlreadyAtTarget, "The charge ended as it began.")
                : Results.Json(new EvChargeNowResponse(true, running.TargetPercent, running.ExpiresAt));
        });

        endpoints.MapDelete("/api/ev/charge-now", ([FromServices] IEvControl control) => control.CancelChargeNow() switch
        {
            EvControlResult.NotConfigured => Refused(StatusCodes.Status404NotFound, NotConfigured, "There is no ev: section in the valid configuration."),
            EvControlResult.NoChargeNow => Refused(StatusCodes.Status404NotFound, NoChargeNow, "No charge-now is running."),
            _ => Results.Json(new { ended = true }),
        });
        return endpoints;
    }

    /// <summary>Far above the two short keys; a larger body is not an EV request.</summary>
    internal const int MaxBodyBytes = 512;

    public const string BadRequest = "bad_request";
    public const string NotConfigured = "not_configured";
    public const string NeedsFullDispatch = "needs_full_dispatch";
    public const string NotPlugged = "not_plugged";
    public const string SocUnknown = "soc_unknown";
    public const string AlreadyAtTarget = "already_at_target";
    public const string TargetOutOfRange = "target_out_of_range";
    public const string DurationOutOfRange = "duration_out_of_range";
    public const string DeadlineBeyondHorizon = "deadline_beyond_horizon";
    public const string NoChargeNow = "no_charge_now";

    private static IResult Refused(int status, string code, string message) => Results.Json(new GoalRefusal(code, message), statusCode: status);

    /// <summary>What POST api/ev/mode answers: the mode now in force.</summary>
    public sealed record EvManualMode(string Mode);

    /// <summary>A bounded JSON object body (an empty body is not one); null when it is anything else.</summary>
    private static async Task<JsonElement?> ReadObjectAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is > MaxBodyBytes)
        {
            return null;
        }

        var buffer = new byte[MaxBodyBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await request.Body.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        if (read is 0 or > MaxBodyBytes)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, read), new JsonDocumentOptions { MaxDepth = 4 });
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Only <c>targetPct</c> and <c>durationMinutes</c> may appear, each a finite number; absent means "take the configured value".</summary>
    private static bool TryOptionalNumber(JsonElement body, out double? target, out double? minutes)
    {
        target = null;
        minutes = null;
        foreach (var property in body.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetDouble(out var value) || !double.IsFinite(value))
            {
                return false;
            }

            switch (property.Name)
            {
                case "targetPct" when target is null:
                    target = value;
                    break;
                case "durationMinutes" when minutes is null:
                    minutes = value;
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    /// <param name="inputs">The EV inputs of the latest store cycle; null builds the answer from the configuration alone.</param>
    /// <param name="zone">The add-on's zone, the one the calendar's naive local times are read in; null leaves the zone out.</param>
    /// <param name="published">The module's latest snapshot; null (or nothing computed yet) leaves the assessment out.</param>
    internal static EvResponse Build(ConfigurationResult? result, DateTimeOffset now, EvInputs? inputs = null, TimeZoneInfo? zone = null, EvPublished? published = null, EvManual? manual = null)
    {
        var configuration = result?.Configuration;
        var ev = configuration?.Ev;
        var issues = Issues(result);
        EvInputsDto? inputsDto = null;
        EvTripsDto? trips = null;
        if (ev is not null && inputs is not null)
        {
            inputsDto = Inputs(inputs, now);
            var assessment = TripParser.Assess(inputs.CalendarEvent, now, configuration!.Control.Ev.MaxTripKm);
            trips = new EvTripsDto(
                assessment.Next is { } next ? new EvTripDto(next.Title, next.Start, next.Km) : null,
                [.. assessment.Unparsed.Select(u => new EvUnparsedDto(u.Title, u.Start, ApiText.Snake(u.Reason), u.Km))],
                [.. assessment.Warnings.Select(w => new EvTripWarningDto(ApiText.Snake(w.Kind), w.Title, w.Start))],
                inputs.CalendarZoneName);
            if (zone is not null && ZoneDisagrees(inputs.CalendarZoneName, zone, inputs.CalendarEvent?.Start ?? now))
            {
                issues = [.. issues, ZoneIssue(inputs.CalendarZoneName!, zone)];
            }
        }

        return new EvResponse(
            now,
            ev is not null,
            ev is null
                ? null
                : new EvConfigDto(
                    ev.Device.Value,
                    ev.CapacityKwh,
                    ev.Soc.Value,
                    ev.CheapZone,
                    configuration?.Planner is { } planner ? ApiText.Snake(planner.Dispatch) : null,
                    ev.Range?.Value,
                    ev.Plugged?.Value,
                    ev.Plugged is null ? null : ev.PluggedStates,
                    ev.Calendar?.Value),
            configuration is null ? null : Parameters(configuration.Control.Ev),
            issues,
            zone?.Id,
            inputsDto,
            trips,
            ev is null ? null : Assessment(published, now),
            ev is null || manual is null ? null : Manual(manual, now));
    }

    private static EvManualDto Manual(EvManual manual, DateTimeOffset now) =>
        new(
            ApiText.Snake(manual.Mode),
            manual.ChargeNow is { } running && now < running.ExpiresAt
                ? new EvChargeNowDto(true, running.TargetPercent, running.ExpiresAt)
                : new EvChargeNowDto(false, null, null));

    private static EvAssessmentDto? Assessment(EvPublished? published, DateTimeOffset now)
    {
        if (published is null)
        {
            return null;
        }

        if (published.Failed)
        {
            return new EvAssessmentDto(true, null, [], null, null, true, null, null, null, null, null, null, []);
        }

        if (published.Snapshot is not { } snapshot)
        {
            return null;
        }

        var composition = snapshot.Composition;
        return new EvAssessmentDto(
            false,
            EvAssessmentCodes.Text(composition.Code),
            [.. composition.Notes.Select(EvAssessmentCodes.Text)],
            snapshot.ComputedAt,
            snapshot.ValidUntil,
            now > snapshot.ValidUntil,
            ApiText.Snake(snapshot.Mode),
            snapshot.SocPercent,
            snapshot.SocGap is { } gap ? ApiText.Snake(gap) : null,
            snapshot.KwhPerKm,
            snapshot.NextTrip is { } trip ? new EvTripDto(trip.Title, trip.Start, trip.Km) : null,
            snapshot.LastFullAt,
            [.. composition.Goals.Select(g => new EvOfferedGoalDto(
                ApiText.Snake(g.Kind),
                g.Request.TargetSoc ?? 0,
                g.Request.RemainingKwh,
                g.Request.Deadline,
                g.Request.Hard,
                g.Request.Sources?.ToText(),
                g.Spread))]);
    }

    private static EvInputsDto Inputs(EvInputs inputs, DateTimeOffset now) =>
        new(Read(inputs.Soc, now), inputs.SocPercent, Read(inputs.Range, now), inputs.RangeKm, Read(inputs.Plugged, now), inputs.IsPlugged, inputs.PluggedState, Read(inputs.Calendar, now));

    private static EvReadDto Read(EvRead read, DateTimeOffset now) =>
        new(
            read.Configured,
            ApiText.Snake(read.State),
            ApiText.Snake(read.Detail),
            read.ReadAt,
            read.ReadAt is { } at ? Math.Round((now - at).TotalSeconds, 3) : null,
            read.MaxAge?.TotalSeconds);

    /// <summary>
    /// True when the calendar entity states a zone of its own that gives a different offset from the add-on's at <paramref name="at"/>. Two names of one zone
    /// (an IANA id and a Windows id) agree; a name this host does not know is a disagreement, because the times cannot be shown to be right.
    /// </summary>
    internal static bool ZoneDisagrees(string? stated, TimeZoneInfo addOn, DateTimeOffset at)
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

    private static EvParametersDto Parameters(EvParameters p) =>
        new(
            p.FloorPercent,
            p.FloorAfterPercent,
            p.ReservePoints,
            p.CapPercent,
            [.. p.WeeklyFull.Days.Select(d => d.ToString().ToLowerInvariant())],
            p.WeeklyFull.TargetPercent,
            p.ReadyMargin.TotalSeconds,
            p.LossFraction,
            p.FallbackKwhPerKm,
            p.Learn.Smoothing,
            p.Learn.MinSoc,
            p.Learn.MinKwhPerKm,
            p.Learn.MaxKwhPerKm,
            p.Learn.MaxSamples,
            p.Learn.PairTolerance.TotalSeconds,
            p.MaxTripKm,
            p.SocMaxAge.TotalSeconds,
            p.DriftLimitKwh,
            p.TargetChangeThresholdPoints,
            p.ChargeNow.TargetPercent,
            p.ChargeNow.Duration.TotalSeconds,
            p.Spread,
            p.SnapshotMaxAge.TotalSeconds);

    private static IReadOnlyList<ConfigurationIssue> Issues(ConfigurationResult? result) =>
        result is null
            ? []
            : [.. result.Errors.Concat(result.Warnings).Where(i => EvPath().IsMatch(i.Path))];

    [GeneratedRegex(@"^(ev|control\.ev)([.\[]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex EvPath();
}
