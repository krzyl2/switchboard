using Microsoft.AspNetCore.Mvc;
using Switchboard.Application.Balance;
using Switchboard.Application.Dispatch;

namespace Switchboard.Host.Web;

/// <summary>
/// GET api/balance (BAL-01, BAL-07): the balance of the running local hour in Wh, positive = net export,
/// or why it is unknown (unavailable, stale, no_baseline, counter_reset, clock_step_back), and what the
/// allowance rule made of it in the last dispatch step: free power and own load in W, the projection at
/// the next :00 in Wh and the target band.
/// </summary>
/// <param name="Configured">False when no energy counters are mapped; every other field is then empty.</param>
/// <param name="Wh">Null while the balance is unknown.</param>
/// <param name="Reason">none while known.</param>
/// <param name="Known">The rule had balance, free power and own load: the projection and band below are real.</param>
/// <param name="UnknownReason">none while known; else the balance's own reason, or balance_not_used (reading too old for the step or not of the running hour), free_power_unknown, own_load_unknown, no_dispatch_step.</param>
/// <param name="FreeW">Free power: grid net export plus own loads added back, smoothed.</param>
/// <param name="LoadW">What Switchboard's own devices draw (planned level for a gated device).</param>
/// <param name="ProjectionWh">The balance at the next :00 if the load stays as it is.</param>
/// <param name="RuleSecondsToNextHour">The time to :00 the rule used in its step; zero when the balance was not of the running hour.</param>
/// <param name="RuleAt">The dispatch step these rule values are from.</param>
public sealed record BalanceResponse(
    bool Configured,
    double? Wh,
    DateTimeOffset? HourStart,
    double? SecondsToNextHour,
    string Reason,
    bool Known,
    string UnknownReason,
    double? FreeW,
    double? LoadW,
    double? ProjectionWh,
    BalanceBandDto? Band,
    double? RuleSecondsToNextHour,
    DateTimeOffset? RuleAt);

/// <param name="Burning">Inside the burn window: the band's upper edge ramps down to the floor.</param>
public sealed record BalanceBandDto(double LoWh, double HiWh, bool Burning);

internal static class BalanceEndpoint
{
    public static IEndpointRouteBuilder MapBalanceApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/balance", ([FromServices] IBalanceSource source, [FromServices] IDispatchStatusSource dispatch) => Results.Json(Build(source.Current, dispatch.Current)));
        return endpoints;
    }

    internal static BalanceResponse Build(HourlyBalanceStatus? status, DispatchStatus? dispatch = null)
    {
        if (status is null)
        {
            return new BalanceResponse(false, null, null, null, ApiText.Snake(Domain.Balance.BalanceUnknown.None), false, ApiText.Snake(Domain.Balance.BalanceUnknown.None), null, null, null, null, null, null);
        }

        var reason = ApiText.Snake(status.Reading.Reason);
        var view = dispatch?.Balance;
        var known = view is { Known: true };
        return new BalanceResponse(
            true,
            status.Reading.Wh is { } wh ? Math.Round(wh, 3) : null,
            status.Reading.HourStart,
            Math.Round(status.UntilNextHour.TotalSeconds, 1),
            reason,
            known,
            known ? "none" : UnknownReason(status, view, reason),
            Round(view?.Free?.Value),
            Round(view?.Load?.Value),
            Round(view?.ProjectionWh),
            view?.Band is { } band ? new BalanceBandDto(Math.Round(band.LoWh, 3), Math.Round(band.HiWh, 3), band.Burning) : null,
            view is null ? null : Math.Round(view.UntilNext.TotalSeconds, 1),
            view is null ? null : dispatch!.At);
    }

    /// <summary>The reasons beside the tracker's own that the rule's figures can be unknown for, as api/balance spells them.</summary>
    internal static readonly string[] RuleUnknownReasons = [NoDispatchStep, BalanceNotUsed, FreePowerUnknown, OwnLoadUnknown];

    private const string NoDispatchStep = "no_dispatch_step";
    private const string BalanceNotUsed = "balance_not_used";
    private const string FreePowerUnknown = "free_power_unknown";
    private const string OwnLoadUnknown = "own_load_unknown";

    private static string UnknownReason(HourlyBalanceStatus status, BalanceView? view, string reason) =>
        status.Reading.Wh is null ? reason
        : view is null ? NoDispatchStep
        : view.BalanceWh is null ? BalanceNotUsed
        : view.Free is null ? FreePowerUnknown
        : OwnLoadUnknown;

    private static double? Round(double? value) => value is { } v ? Math.Round(v, 3) : null;
}
