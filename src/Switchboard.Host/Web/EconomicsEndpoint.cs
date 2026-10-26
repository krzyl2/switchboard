using System.Collections.Immutable;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Switchboard.Application.Configuration;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Options;
using Switchboard.Application.Presentation;
using Switchboard.Application.Signals;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Signals;

namespace Switchboard.Host.Web;

/// <summary>
/// GET api/economics (ECO-01, ECO-02, ECO-04; read-only, nothing here changes a decision): the tariff in force now
/// on the local clock, the next zone change, the settlement as configured and, per device, the value now and its sources.
/// </summary>
/// <param name="Configured">False without an <c>economics:</c> section (or when the document is invalid); every other field is then empty.</param>
/// <param name="Currency">The label as written in the file, never a symbol of ours.</param>
/// <param name="HolidaysListStale">The holiday list has no date of the current local year (or is empty while the zones use holidays).</param>
/// <param name="Issues">Configuration issues under <c>economics</c>, errors first.</param>
/// <param name="Devices">Per device: the effective value now and the sources; present whenever the configuration is valid, with or without an <c>economics:</c> section (then every value is the static one).</param>
/// <param name="Savings">Savings accounting (ECO-11); null without an <c>economics:</c> section (the dashboard keeps saying savings need a tariff).</param>
public sealed record EconomicsResponse(
    DateTimeOffset At,
    bool Configured,
    string? Currency,
    TariffNowDto? Tariff,
    SettlementDto? Settlement,
    bool HolidaysListStale,
    IReadOnlyList<ConfigurationIssue> Issues,
    IReadOnlyList<EconomicsDeviceDto>? Devices = null,
    SavingsDto? Savings = null);

/// <summary>
/// What the controlled loads saved, by the definition of 14-ECONOMICS (Savings): against the loads being off, per settlement period, summed over
/// the periods whose prices were known. An estimate, not a bill: nothing here is a settlement document and none of it is back-filled.
/// </summary>
/// <param name="Basis">What the figures are measured against, in words (the UI shows it beside the number).</param>
/// <param name="Currency">The label as written in the file, never a symbol of ours.</param>
/// <param name="Model">The settlement model the periods follow: hourly_netting (local hours) or net_billing (statistics buckets).</param>
/// <param name="Since">The start of the first counted period; nothing earlier is counted and nothing is back-filled; null while no period is counted.</param>
/// <param name="Today">The local day of now (23, 24 or 25 hours), counted periods only.</param>
/// <param name="Range">The requested from/to, in the way api/statistics takes it; null when none was asked for.</param>
/// <param name="Retained">Everything the retained statistics hold, from <paramref name="Since"/> to now.</param>
/// <param name="Running">The period in progress, accounted so far; reported apart and in none of the totals.</param>
public sealed record SavingsDto(string Basis, string? Currency, string Model, DateTimeOffset? Since, SavingsTotalDto Today, SavingsTotalDto? Range, SavingsTotalDto Retained, SavingsPeriodDto? Running);

/// <param name="Periods">Counted periods.</param>
/// <param name="FallbackPeriods">Counted periods with a price at its dynamic fallback: flagged, not left out.</param>
/// <param name="UnpricedPeriods">Periods after <c>since</c> without all the prices they need: not counted.</param>
/// <param name="IncompletePeriods">Hours after <c>since</c> that were not observed throughout: not counted.</param>
/// <param name="ValueGained">Sum of controlled energy times effective value, time-weighted.</param>
/// <param name="ExtraCost">What the periods' settlement lost by the loads running: Settle(b + E) - Settle(b), never negative.</param>
/// <param name="Savings">ValueGained minus ExtraCost; a negative figure is shown (it means the sources or the value are mis-set).</param>
/// <param name="Fallback">Some counted period had a price at fallback.</param>
public sealed record SavingsTotalDto(
    DateTimeOffset From,
    DateTimeOffset To,
    int Periods,
    int FallbackPeriods,
    int UnpricedPeriods,
    int IncompletePeriods,
    double ValueGained,
    double ExtraCost,
    double Savings,
    bool Fallback,
    IReadOnlyList<SavingsDeviceDto> Devices);

/// <param name="EnergyKwh">The controlled energy of the device over the counted periods (measured plus rated-model estimate).</param>
/// <param name="ExtraCost">The device's share of the extra cost, proportional to its energy in each period.</param>
public sealed record SavingsDeviceDto(string Id, double EnergyKwh, double ValueGained, double ExtraCost, double Savings);

/// <param name="Start">UTC instant the period started; for an hour, the start of the local hour.</param>
public sealed record SavingsPeriodDto(DateTimeOffset Start, DateTimeOffset End, double ValueGained, double ExtraCost, double Savings, bool Fallback, IReadOnlyList<SavingsDeviceDto> Devices);

/// <param name="ValuePerKwh">The static value as written; also the fallback.</param>
/// <param name="ValueFollows">static, import_price or export_price (static when the followed price has no spec).</param>
/// <param name="VEff">The effective value now, currency per kWh.</param>
/// <param name="ValueBasis">static, followed, or fallback (the followed price is not available now).</param>
/// <param name="Sources">The sources after narrowing to the tariff's zones; null when the device declares none (0.9 behaviour).</param>
/// <param name="SourcesDeclared">The sources as written; null when none.</param>
/// <param name="Class">The energy class that allowed the device in the last dispatch step (UI-12): pv (surplus), zone (the active tariff zone), none (neither); null until a step has run. Read-only: the reason is the device's own in api/dispatch.</param>
public sealed record EconomicsDeviceDto(string Id, double ValuePerKwh, string ValueFollows, double ValueFactor, double VEff, string ValueBasis, string? Sources, string? SourcesDeclared, string? Class = null);

/// <param name="Kind">flat, zoned or dynamic.</param>
/// <param name="Zone">The zone id now (of the add while a dynamic price is live, of the fallback otherwise); null for a flat price.</param>
/// <param name="ImportPrice">Currency per kWh now.</param>
/// <param name="PriceBasis">flat, zone, live or fallback.</param>
/// <param name="NextChangeAt">The first minute whose zone differs from now; null for a flat price or when it does not change within a year.</param>
/// <param name="NextZone">The zone from then on.</param>
/// <param name="Signal">A dynamic price: the price signal it follows, its state and age; null for a static price.</param>
/// <param name="FallbackInForce">A dynamic price is at its static fallback now (the signal is not usable).</param>
public sealed record TariffNowDto(string Kind, string? Zone, double? ImportPrice, string? PriceBasis, DateTimeOffset? NextChangeAt, string? NextZone, PriceSignalDto? Signal = null, bool FallbackInForce = false);

/// <summary>The price signal a dynamic price follows, as the signal layer reports it now (never an entity ID).</summary>
/// <param name="Name">The logical signal name.</param>
/// <param name="State">fresh, stale, rejected or unavailable; only fresh is used, any other state means the fallback.</param>
/// <param name="Detail">Why, as in api/signals (max_age_exceeded, unit_mismatch, state_unknown, ...).</param>
/// <param name="PerKwh">The signal value in currency per kWh (before add); null while it is not usable.</param>
/// <param name="AgeS">Seconds since the value in use was reported (last_reported); null when none arrived.</param>
public sealed record PriceSignalDto(string Name, string State, string Detail, double? PerKwh, double? AgeS);

/// <param name="Model">hourly_netting or net_billing, as configured; dispatch uses it in its decisions (shadow, like everything).</param>
public sealed record SettlementDto(string Model, ExportPriceDto? ExportPrice);

/// <param name="Kind">flat, zoned or dynamic.</param>
public sealed record ExportPriceDto(string Kind, string? Zone, double? PerKwh, string? PriceBasis, PriceSignalDto? Signal = null, bool FallbackInForce = false);

internal static class EconomicsEndpoint
{
    public static IEndpointRouteBuilder MapEconomicsApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/economics", (
            HttpRequest request,
            [FromServices] ISignalDiagnosticsSource diagnostics,
            [FromServices] SignalClock clock,
            [FromServices] IPresentationSource presentation,
            [FromServices] IOptions<StatisticsOptions> options,
            [FromServices] IDispatchStatusSource dispatch) =>
        {
            if (!StatisticsEndpoint.TryRange(request.Query, options.Value.Retention, out var range))
            {
                return Results.Problem(
                    $"Range: from and to are both given or both left out, ISO 8601 instants with an offset, from before to, at most {options.Value.Retention} apart.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            return Results.Json(Build(diagnostics.Current.Configuration, clock.Time.GetUtcNow(), clock.Time.LocalTimeZone, diagnostics.Current.Snapshot.Prices, presentation.Statistics, range, dispatch.Current));
        });
        return endpoints;
    }

    /// <summary>What savings are measured against, as the figures are defined (14-ECONOMICS, Savings): the English text the UI localises.</summary>
    internal const string SavingsBasis = "against the controlled loads being off";

    /// <param name="prices">The price signals of the latest store cycle; a dynamic price is read from them (without, it is at its fallback).</param>
    /// <param name="statistics">The retained statistics savings are accounted from; without them (and without an <c>economics:</c> section) there are no savings.</param>
    /// <param name="range">The requested range of the savings (UTC instants), or null.</param>
    /// <param name="dispatch">The last dispatch step, for the energy class that allowed each device; without it the class is null.</param>
    internal static EconomicsResponse Build(ConfigurationResult? result, DateTimeOffset now, TimeZoneInfo zone, PriceInputs? prices = null, StatisticsSnapshot? statistics = null, (DateTimeOffset From, DateTimeOffset To)? range = null, DispatchStatus? dispatch = null)
    {
        var issues = Issues(result);
        if (result?.Configuration?.Economics is not { } economics)
        {
            return new EconomicsResponse(now, false, null, null, null, false, issues, Devices(result?.Configuration, PriceBook.None, null));
        }

        var holidays = economics.Holidays;
        var localYear = TimeZoneInfo.ConvertTime(now, zone).Year;
        var usesHolidays = PriceSpecs.Schedules(economics.Tariff).Concat(PriceSpecs.Schedules(economics.Settlement?.ExportPrice))
            .SelectMany(z => z.Zones).SelectMany(z => z.Rules).SelectMany(r => r.Days)
            .Any(d => d is DayClass.Workday or DayClass.Holiday);
        var stale = holidays.Dates.Count == 0 ? usesHolidays : holidays.HasNoDateIn(localYear);

        return new EconomicsResponse(
            now,
            true,
            economics.Currency,
            economics.Tariff is { } spec ? Tariff(new Tariff(spec, holidays, zone), spec, now, prices) : null,
            economics.Settlement is { } settlement
                ? new SettlementDto(ApiText.Snake(settlement.Model), settlement.ExportPrice is { } export ? Export(new Tariff(export, holidays, zone), export, now, prices) : null)
                : null,
            stale,
            issues,
            Devices(
                result.Configuration,
                PriceBook.At(
                    now,
                    economics.Tariff is { } import ? new Tariff(import, holidays, zone) : null,
                    economics.Settlement?.ExportPrice is { } exportSpec ? new Tariff(exportSpec, holidays, zone) : null,
                    prices),
                dispatch?.Economics),
            statistics is null ? null : Savings(result.Configuration, economics, statistics, now, zone, range));
    }

    private static SavingsDto Savings(SwitchboardConfiguration configuration, EconomicsDefinition economics, StatisticsSnapshot statistics, DateTimeOffset now, TimeZoneInfo zone, (DateTimeOffset From, DateTimeOffset To)? range)
    {
        var model = DispatchSetupFactory.Build(configuration).Economics!.Settlement;
        var periods = SavingsAccounting.Periods(statistics, model, zone, now);
        var since = SavingsAccounting.Since(periods);
        var (dayFrom, dayTo) = LocalDay(now, zone);
        return new SavingsDto(
            SavingsBasis,
            economics.Currency,
            ApiText.Snake(model.Id),
            since,
            Total(SavingsAccounting.Total(periods, dayFrom, dayTo)),
            range is { } r ? Total(SavingsAccounting.Total(periods, r.From, r.To)) : null,
            Total(SavingsAccounting.Total(periods, since ?? now, now)),
            periods.LastOrDefault() is { Status: PeriodStatus.Running } running
                ? new SavingsPeriodDto(running.Start, running.End, running.ValueGained, running.ExtraCost, running.Savings, running.Fallback, Devices(running.Devices))
                : null);
    }

    private static SavingsTotalDto Total(SavingsTotal total) =>
        new(total.From, total.To, total.Periods, total.FallbackPeriods, total.UnpricedPeriods, total.IncompletePeriods, total.ValueGained, total.ExtraCost, total.Savings, total.FallbackPeriods > 0, Devices(total.Devices));

    private static IReadOnlyList<SavingsDeviceDto> Devices(ImmutableArray<DeviceSavings> devices) =>
        [.. devices.Select(d => new SavingsDeviceDto(d.Device.Value, d.Energy.Value, d.ValueGained, d.ExtraCost, d.Savings))];

    /// <summary>The local day of <paramref name="now"/> as UTC instants: 23, 24 or 25 hours long on the days the zone changes.</summary>
    private static (DateTimeOffset From, DateTimeOffset To) LocalDay(DateTimeOffset now, TimeZoneInfo zone)
    {
        var date = TimeZoneInfo.ConvertTime(now, zone).Date;
        return (Midnight(date, zone), Midnight(date.AddDays(1), zone));
    }

    private static DateTimeOffset Midnight(DateTime date, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static IReadOnlyList<EconomicsDeviceDto> Devices(SwitchboardConfiguration? configuration, PriceBook prices, EconomicsView? step) =>
        configuration is null
            ? []
            : [.. configuration.Devices.Select(d =>
            {
                var value = EffectiveValue.At(d.Value, prices);
                return new EconomicsDeviceDto(
                    d.Id.Value,
                    d.ValuePerKwh,
                    ApiText.Snake(d.ValueFollows),
                    d.ValueFactor,
                    value.PerKwh,
                    ApiText.Snake(value.Basis),
                    d.Sources?.ToText(),
                    d.SourcesDeclared?.ToText(),
                    step is null ? null : ApiText.SnakeOrNull(step.Devices.FirstOrDefault(v => v.Device == d.Id)?.Class) ?? "none");
            })];

    private static TariffNowDto Tariff(Tariff tariff, PriceSpec spec, DateTimeOffset now, PriceInputs? prices)
    {
        var price = tariff.PriceAt(now, prices);
        var next = tariff.NextChange(now, prices);
        return new TariffNowDto(
            Kind(spec),
            tariff.ZoneAt(now, prices)?.Id,
            price?.PerKwh,
            price is { } p ? ApiText.Snake(p.Basis) : null,
            next?.At,
            next?.Zone,
            SignalOf(spec, prices, now),
            price?.Basis == PriceBasis.Fallback);
    }

    private static ExportPriceDto Export(Tariff tariff, PriceSpec spec, DateTimeOffset now, PriceInputs? prices)
    {
        var price = tariff.PriceAt(now, prices);
        return new ExportPriceDto(Kind(spec), price?.Zone, price?.PerKwh, price is { } p ? ApiText.Snake(p.Basis) : null, SignalOf(spec, prices, now), price?.Basis == PriceBasis.Fallback);
    }

    private static PriceSignalDto? SignalOf(PriceSpec spec, PriceInputs? prices, DateTimeOffset now)
    {
        if (spec is not DynamicPrice dynamic)
        {
            return null;
        }

        return prices?.Sample(dynamic.Signal) is { } sample
            ? new PriceSignalDto(
                dynamic.Signal.Value,
                ApiText.Snake(sample.State),
                ApiText.Snake(sample.Detail),
                sample.Usable ? sample.PerKwh : null,
                sample.ReportedAt is { } reported ? Math.Round((now - reported).TotalSeconds, 3) : null)
            : new PriceSignalDto(dynamic.Signal.Value, ApiText.Snake(SignalState.Unavailable), ApiText.Snake(SignalDetail.AwaitingFirstSample), null, null);
    }

    private static string Kind(PriceSpec spec) => spec switch
    {
        ZonedPrice => "zoned",
        DynamicPrice => "dynamic",
        _ => "flat",
    };

    private static IReadOnlyList<ConfigurationIssue> Issues(ConfigurationResult? result) =>
        result is null
            ? []
            : [.. result.Errors.Concat(result.Warnings).Where(i => i.Path == EconomicsPath || i.Path.StartsWith(EconomicsPath + ".", StringComparison.Ordinal) || i.Path.StartsWith(EconomicsPath + "[", StringComparison.Ordinal))];

    private const string EconomicsPath = "economics";
}
