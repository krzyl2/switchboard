using Microsoft.AspNetCore.Mvc;
using Switchboard.Application.Presentation;
using Switchboard.Application.Signals;
using Switchboard.Domain.Decisions;

namespace Switchboard.Host.Web;

/// <summary>
/// Shape of GET api/power (UI-07 … UI-11): the power picture of the last control step for the
/// dashboard and the flow view. Watts, import and consumption positive; a figure that is not known
/// carries no number and says why (<c>unknown</c>), never 0. <c>at</c> is the signal clock now
/// (data time in development replay), so the browser can show ages and the local hour from it.
/// </summary>
public sealed record PowerResponse(
    DateTimeOffset At,
    DateTimeOffset? EvaluatedAt,
    double CycleS,
    bool DataValid,
    string? InvalidReason,
    BalanceDto Total,
    IReadOnlyList<PhasePowerDto> Phases,
    IReadOnlyList<DevicePowerDto> Devices,
    IReadOnlyList<BannerDto> Banners);

/// <param name="W">The figure in W; null when it is not known.</param>
/// <param name="Unknown">Why it is not known (not_declared: nothing of the kind exists, counted as 0 in the balance).</param>
/// <param name="ReportedAt">The report time of its oldest measured input; null for a model value.</param>
public sealed record QuantityDto(double? W, string? Unknown, DateTimeOffset? ReportedAt);

/// <param name="Uncontrolled">The house's own consumption: load − controlled − storage (the only residual).</param>
public sealed record BalanceDto(
    QuantityDto Generation,
    string? GenerationNote,
    QuantityDto NetImport,
    QuantityDto Load,
    QuantityDto Controlled,
    IReadOnlyList<string> UnknownDevices,
    QuantityDto Storage,
    QuantityDto Uncontrolled,
    bool ModelExceedsLoad);

public sealed record PhasePowerDto(
    string Phase,
    double? CurrentA,
    string? Direction,
    DateTimeOffset? CurrentReportedAt,
    double ReserveThresholdA,
    double RatingA,
    double ContinuousA,
    double VoltageV,
    bool VoltageMeasured,
    BalanceDto Balance,
    double? ForeignStepW,
    double? ForeignBaselineW,
    DateTimeOffset? ForeignDetectedAt,
    IReadOnlyList<BarSegmentDto> Bar,
    bool BarScaled,
    bool OverReserve);

/// <param name="Kind">base, controlled, foreign, export or unattributed.</param>
public sealed record BarSegmentDto(string Kind, double A, double? W);

public sealed record DevicePowerDto(string Id, IReadOnlyList<DevicePhasePowerDto> Phases, double? TotalW, string Source);

/// <param name="Source">measured, rated_model or unknown.</param>
public sealed record DevicePhasePowerDto(string Phase, double? PowerW, string Source, DateTimeOffset? ReportedAt);

/// <param name="CurrentA">Measured, import positive.</param>
/// <param name="EffectiveCurrentA">The estimate with the changes already decided (the guard's own comparison); null without valid data.</param>
/// <param name="Reaction">The tier reaction the guard acts on: shed_all_on_phase from the measured band, the rest from the estimate's.</param>
/// <param name="Timing">now, next_sample, next_boundary, by_boundary (an upper bound) or cannot_reduce_exporting.</param>
public sealed record BannerDto(
    string Phase,
    double CurrentA,
    double? EffectiveCurrentA,
    double ThresholdA,
    double? ForeignStepW,
    DateTimeOffset? ForeignDetectedAt,
    string? Reaction,
    bool ReductionRequested,
    string Timing,
    DateTimeOffset? DueAt,
    IReadOnlyList<PlannedChangeDto> Planned);

public sealed record PlannedChangeDto(string Device, LevelDto? From, LevelDto To, string? Origin, string? Reason, DateTimeOffset? DueAt);

internal static class PowerEndpoint
{
    public static IEndpointRouteBuilder MapPowerApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/power", ([FromServices] IPresentationSource presentation, [FromServices] SignalClock clock) =>
            TypedResults.Json(Build(presentation.Power, clock.Time.GetUtcNow())));
        return endpoints;
    }

    internal static PowerResponse Build(PowerView? view, DateTimeOffset now)
    {
        if (view is null)
        {
            var missing = new QuantityDto(null, ApiText.Snake(PowerUnknownReason.ConfigurationInvalid), null);
            return new PowerResponse(now, null, 0, false, "configuration_invalid", new BalanceDto(missing, null, missing, missing, missing, [], missing, missing, false), [], [], []);
        }

        return new PowerResponse(
            now,
            view.EvaluatedAt,
            view.Cycle.TotalSeconds,
            view.DataValid,
            ApiText.SnakeOrNull(view.InvalidReason),
            Balance(view.Total),
            [.. view.Phases.Select(p => new PhasePowerDto(
                p.Phase.ToString(),
                p.Current?.Value,
                ApiText.SnakeOrNull(p.Direction),
                p.CurrentReportedAt,
                p.ReserveThreshold.Value,
                p.Rating.Value,
                p.Continuous.Value,
                p.Voltage.Value,
                p.VoltageMeasured,
                Balance(p.Balance),
                p.ForeignStep?.Value,
                p.ForeignBaseline?.Value,
                p.ForeignDetectedAt,
                [.. p.Bar.Select(s => new BarSegmentDto(ApiText.Snake(s.Kind), s.Current.Value, s.Power?.Value))],
                p.BarScaled,
                p.OverReserve))],
            [.. view.Devices.Select(d => new DevicePowerDto(
                d.Device.Value,
                [.. d.Phases.Select(p => new DevicePhasePowerDto(p.Phase.ToString(), p.Power?.Value, ApiText.Snake(p.Source), p.ReportedAt))],
                d.Total?.Value,
                ApiText.Snake(d.Source)))],
            [.. view.Banners.Select(b => new BannerDto(
                b.Phase.ToString(),
                b.Current.Value,
                b.EffectiveCurrent?.Value,
                b.Threshold.Value,
                b.ForeignStep?.Value,
                b.ForeignDetectedAt,
                ApiText.SnakeOrNull(b.Reaction),
                b.ReductionRequested,
                ApiText.Snake(b.Timing),
                b.DueAt,
                [.. b.Planned.Select(c => new PlannedChangeDto(
                    c.Device.Value,
                    ApiText.Level(c.From),
                    ApiText.Level(c.To)!,
                    ApiText.SnakeOrNull(c.Origin),
                    c.Reason is { } reason ? ReasonCodes.Name(reason) : null,
                    c.DueAt))]))]);
    }

    private static BalanceDto Balance(BalanceParts parts) => new(
        Quantity(parts.Generation),
        ApiText.SnakeOrNull(parts.GenerationNote),
        Quantity(parts.NetImport),
        Quantity(parts.Load),
        Quantity(parts.Controlled),
        [.. parts.UnknownDevices.Select(d => d.Value).Order(StringComparer.Ordinal)],
        Quantity(parts.Storage),
        Quantity(parts.Uncontrolled),
        parts.ModelExceedsLoad);

    private static QuantityDto Quantity(Quantity quantity) => new(quantity.Value, ApiText.SnakeOrNull(quantity.Unknown), quantity.ReportedAt);
}
