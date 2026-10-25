using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Switchboard.Application.Options;
using Switchboard.Application.Presentation;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;

namespace Switchboard.Host.Web;

/// <summary>
/// Shape of GET api/statistics: energy integrated per UTC bucket on the signal clock (never read
/// from the Recorder), the last hour's sums, and the counters since the statistics began. The
/// browser groups buckets into its own local day (DST: 23- and 25-hour days) and may ask for just
/// that range with <c>from</c> and <c>to</c> (UTC instants); the server stays timezone-free.
/// </summary>
public sealed record StatisticsResponse(
    DateTimeOffset At,
    double BucketMinutes,
    PresentationFilesDto StateFile,
    bool VoltageMeasured,
    DateTimeOffset? IntegratedTo,
    IReadOnlyList<StatisticsDeviceDto> Devices,
    IReadOnlyList<BucketDto> Buckets,
    RangeDto? Range,
    RangeDto LastHour,
    LifetimeDto Lifetime);

/// <summary>How a presentation file was found at start (loaded, fresh, reset_corrupt) and whether it can be written.</summary>
public sealed record PresentationFilesDto(string Status, string? Warning, string? WriteError);

public sealed record StatisticsDeviceDto(string Id, string? Name, double ValuePerKwh);

public sealed record BucketDto(DateTimeOffset Start, TotalsDto Totals);

/// <param name="ObservedS">Time the whole power picture was known; energy is integrated over it only.</param>
/// <param name="VoltageLosses">Phase-voltage losses on some phases while the others stayed up: possible breaker trips.</param>
/// <param name="SupplyOutages">Losses on every mapped phase at once: a supply outage, not a trip.</param>
public sealed record TotalsDto(
    double ObservedS,
    double UnobservedS,
    double GridImportKwh,
    double GridExportKwh,
    double GenerationKwh,
    double LoadKwh,
    double UncontrolledKwh,
    double StorageChargeKwh,
    double StorageDischargeKwh,
    PvDto Pv,
    IReadOnlyList<DeviceEnergyDto> Devices,
    int Sheds,
    int WouldSheds,
    int VoltageLosses,
    int SupplyOutages,
    double VoltageObservedS);

public sealed record PvDto(double HouseKwh, double StorageKwh, double ExportKwh, double UnattributedKwh);

/// <param name="ModelKwh">From the rated model (a relay's rating while it reports on): an estimate.</param>
/// <param name="Value">Its energy at its value per kWh (no currency; v1 has no tariff).</param>
public sealed record DeviceEnergyDto(string Id, double MeasuredKwh, double ModelKwh, double UnknownS, double PvKwh, double Value);

/// <param name="From">The start of the range (the last hour: the start of the bucket an hour before now, "since HH:MM"; never after <paramref name="To"/>).</param>
public sealed record RangeDto(DateTimeOffset From, DateTimeOffset To, int Buckets, TotalsDto Totals);

public sealed record LifetimeDto(DateTimeOffset? Since, long Sheds, long WouldSheds, long VoltageLosses, long SupplyOutages, double VoltageObservedS);

internal static class StatisticsEndpoint
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    public static IEndpointRouteBuilder MapStatisticsApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/statistics", (
            HttpRequest request,
            [FromServices] IPresentationSource presentation,
            [FromServices] SignalClock clock,
            [FromServices] IOptions<StatisticsOptions> options) =>
        {
            if (!TryRange(request.Query, options.Value.Retention, out var range))
            {
                return Results.Problem(
                    $"Range: from and to are both given or both left out, ISO 8601 instants with an offset, from before to, at most {options.Value.Retention} apart.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            return Results.Json(Build(presentation.Statistics, presentation.Report, clock.Time.GetUtcNow(), range));
        });
        return endpoints;
    }

    internal static StatisticsResponse Build(StatisticsSnapshot snapshot, PresentationFileReport report, DateTimeOffset now, (DateTimeOffset From, DateTimeOffset To)? range)
    {
        // After a clock step back (NTP, or a development replay restarted on the same data folder)
        // buckets can start after now: they belong to a stretch the clock has not reached again, and
        // counted now they would show more observed time than has elapsed. They return once it has.
        var all = snapshot.Buckets.Values.Where(b => b.Start <= now).ToList();
        var shown = range is { } r ? all.Where(b => b.Start >= r.From && b.Start < r.To).ToList() : all;

        // By time, never by count: no bucket exists for downtime. The hour is at least 60 min of the
        // bucket grid; time without data shows as not observed, never as an earlier hour.
        var hourStart = EnergyAccumulator.BucketOf(now - Hour, snapshot.BucketLength);
        var last = all.Where(b => b.Start >= hourStart).ToList();
        return new StatisticsResponse(
            now,
            snapshot.BucketLength.TotalMinutes,
            new PresentationFilesDto(ApiText.Snake(report.Statistics), report.StatisticsWarning, report.WriteError),
            snapshot.VoltageMeasured,
            snapshot.IntegratedTo,
            [.. snapshot.Devices.Select(d => new StatisticsDeviceDto(d.Id.Value, d.Name, d.ValuePerKwh))],
            [.. shown.Select(b => new BucketDto(b.Start, Totals(b)))],
            range is { } requested ? Range(requested.From, requested.To, shown) : null,
            Range(hourStart, now, last),
            new LifetimeDto(snapshot.Lifetime.Since, snapshot.Lifetime.Sheds, snapshot.Lifetime.WouldSheds, snapshot.Lifetime.VoltageLosses, snapshot.Lifetime.SupplyOutages, snapshot.Lifetime.VoltageObserved.TotalSeconds));
    }

    /// <summary>Both or neither of from and to; instants with an offset; from before to; no longer than the retention.</summary>
    internal static bool TryRange(IQueryCollection query, TimeSpan retention, out (DateTimeOffset From, DateTimeOffset To)? range)
    {
        range = null;
        if (query.Any(p => p.Value.Count > 1) || query.Keys.Any(k => k is not ("from" or "to")))
        {
            return false;
        }

        var hasFrom = query.TryGetValue("from", out var fromText);
        var hasTo = query.TryGetValue("to", out var toText);
        if (!hasFrom && !hasTo)
        {
            return true;
        }

        if (!(hasFrom && hasTo && Instant(fromText.ToString()) is { } from && Instant(toText.ToString()) is { } to) || from >= to || to - from > retention)
        {
            return false;
        }

        range = (from, to);
        return true;
    }

    /// <summary>An ISO 8601 instant that names its offset (Z or ±hh:mm): a time without one would be read in the server's zone.</summary>
    private static DateTimeOffset? Instant(string text) =>
        text.Length >= 20 && (text.EndsWith('Z') || text[^6] is '+' or '-')
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : null;

    private static RangeDto Range(DateTimeOffset from, DateTimeOffset to, List<EnergyBucket> buckets) =>
        new(from, to, buckets.Count, Totals(buckets.Aggregate(EnergyBucket.Empty(from), (sum, b) => sum.Add(b))));

    private static TotalsDto Totals(EnergyBucket b) => new(
        b.Observed.TotalSeconds,
        b.Unobserved.TotalSeconds,
        b.GridImport.Value,
        b.GridExport.Value,
        b.Generation.Value,
        b.Load.Value,
        b.Uncontrolled.Value,
        b.StorageCharge.Value,
        b.StorageDischarge.Value,
        new PvDto(b.Pv.House.Value, b.Pv.Storage.Value, b.Pv.Export.Value, b.Pv.Unattributed.Value),
        [.. b.Devices.OrderBy(d => d.Key).Select(d => Device(d.Key, d.Value))],
        b.Sheds,
        b.WouldSheds,
        b.VoltageLosses,
        b.SupplyOutages,
        b.VoltageObserved.TotalSeconds);

    private static DeviceEnergyDto Device(DeviceName id, DeviceEnergy e) =>
        new(id.Value, e.Measured.Value, e.Model.Value, e.Unknown.TotalSeconds, e.Pv.Value, e.Value);
}
