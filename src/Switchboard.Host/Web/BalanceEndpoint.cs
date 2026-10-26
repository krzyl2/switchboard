using Microsoft.AspNetCore.Mvc;
using Switchboard.Application.Balance;

namespace Switchboard.Host.Web;

/// <summary>
/// GET api/balance (BAL-07): the balance of the running local hour in Wh, positive = net export, or why
/// it is unknown (unavailable, stale, no_baseline, counter_reset, clock_step_back).
/// </summary>
/// <param name="Configured">False when no energy counters are mapped; every other field is then empty.</param>
/// <param name="Wh">Null while the balance is unknown.</param>
/// <param name="Reason">none while known.</param>
public sealed record BalanceResponse(bool Configured, double? Wh, DateTimeOffset? HourStart, double? SecondsToNextHour, string Reason);

internal static class BalanceEndpoint
{
    public static IEndpointRouteBuilder MapBalanceApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/balance", ([FromServices] IBalanceSource source) => Results.Json(Build(source.Current)));
        return endpoints;
    }

    internal static BalanceResponse Build(HourlyBalanceStatus? status) => status is null
        ? new BalanceResponse(false, null, null, null, ApiText.Snake(Domain.Balance.BalanceUnknown.None))
        : new BalanceResponse(
            true,
            status.Reading.Wh is { } wh ? Math.Round(wh, 3) : null,
            status.Reading.HourStart,
            Math.Round(status.UntilNextHour.TotalSeconds, 1),
            ApiText.Snake(status.Reading.Reason));
}
