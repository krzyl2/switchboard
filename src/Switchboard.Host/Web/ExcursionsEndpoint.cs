using Microsoft.AspNetCore.Mvc;
using Switchboard.Application.Presentation;
using Switchboard.Application.Signals;
using Switchboard.Domain.Decisions;

namespace Switchboard.Host.Web;

/// <summary>
/// Shape of GET api/excursions: per phase the last (or ongoing) and the worst excursion above the
/// reserve threshold within the retention window, for the Limits chart: the current samples (signed,
/// import positive) and the decisions taken around it.
/// </summary>
public sealed record ExcursionsResponse(DateTimeOffset At, PresentationFilesDto StateFile, IReadOnlyList<PhaseExcursionsDto> Phases);

public sealed record PhaseExcursionsDto(string Phase, ExcursionDto? Last, ExcursionDto? Worst);

/// <param name="Decimated">The samples were thinned to the cap: per time slot the lowest and highest, the peak and the decision instants always kept.</param>
public sealed record ExcursionDto(
    bool Active,
    DateTimeOffset Start,
    DateTimeOffset? End,
    double PeakA,
    DateTimeOffset PeakAt,
    double ReserveThresholdA,
    double RatingA,
    double ContinuousA,
    bool Decimated,
    IReadOnlyList<ExcursionSampleDto> Samples,
    IReadOnlyList<ExcursionDecisionDto> Decisions);

public sealed record ExcursionSampleDto(DateTimeOffset At, double A);

public sealed record ExcursionDecisionDto(string Id, DateTimeOffset At, string Action, string Reason, string? Device, bool Commanded);

internal static class ExcursionsEndpoint
{
    public static IEndpointRouteBuilder MapExcursionsApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/excursions", ([FromServices] IPresentationSource presentation, [FromServices] SignalClock clock) =>
            TypedResults.Json(Build(presentation.Excursions, presentation.Report, clock.Time.GetUtcNow())));
        return endpoints;
    }

    internal static ExcursionsResponse Build(ExcursionsSnapshot snapshot, PresentationFileReport report, DateTimeOffset now) => new(
        now,
        new PresentationFilesDto(ApiText.Snake(report.Excursions), report.ExcursionsWarning, report.WriteError),
        [.. snapshot.Phases.Select(p => new PhaseExcursionsDto(p.Phase.ToString(), Excursion(p.Last), Excursion(p.Worst)))]);

    private static ExcursionDto? Excursion(Excursion? excursion) => excursion is null
        ? null
        : new ExcursionDto(
            excursion.Active,
            excursion.Start,
            excursion.End,
            excursion.Peak.Value,
            excursion.PeakAt,
            excursion.ReserveThreshold.Value,
            excursion.Rating.Value,
            excursion.Continuous.Value,
            excursion.Decimated,
            [.. excursion.Samples.Select(s => new ExcursionSampleDto(s.At, s.Current.Value))],
            [.. excursion.Decisions.Select(d => new ExcursionDecisionDto(d.Id, d.At, ApiText.Snake(d.Action), ReasonCodes.Name(d.Reason), d.Device?.Value, d.Commanded))]);
}
