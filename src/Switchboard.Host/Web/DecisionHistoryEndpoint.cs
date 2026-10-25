using System.Collections.Immutable;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Switchboard.Application.Decisions;
using Switchboard.Application.Options;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;

namespace Switchboard.Host.Web;

/// <summary>
/// GET api/decisions/history (MODE-04): the retained decision log, newest first, beyond the
/// in-memory window. <c>next</c> is an opaque cursor; <c>retainedFrom</c> is the UTC date of the
/// oldest retained daily file. The server stays timezone-free: the browser sends local-day bounds
/// as UTC instants in <c>from</c> and <c>to</c>.
/// </summary>
/// <param name="Partial">The search budget ended the page before it was full; <c>next</c> continues it.</param>
/// <param name="LogWritable">False while the decision log cannot be written (newer decisions are then held in memory only).</param>
public sealed record DecisionHistoryResponse(IReadOnlyList<DecisionDto> Items, string? Next, string? RetainedFrom, int RetentionDays, bool Partial, bool LogWritable);

internal static class DecisionHistoryEndpoint
{
    public static IEndpointRouteBuilder MapDecisionHistoryApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/decisions/history", (
            HttpRequest request,
            [FromServices] IDecisionHistory history,
            [FromServices] IDecisionRecorder recorder,
            [FromServices] IOptions<PersistenceOptions> options) =>
        {
            if (Parse(request.Query, options.Value.DecisionMaxPageSize) is not { } query)
            {
                return Results.Problem(
                    $"Filters: from and to (ISO 8601 instants), device, phase (L1, L2, L3), reason (a reason code of list version {ReasonCodes.ListVersion}), kind, limit (1 to {options.Value.DecisionMaxPageSize}), cursor (from 'next').",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                var page = history.Query(query);
                var writable = recorder.Status.Writable;

                // While /data cannot be written (04 §8) the newest decisions exist in memory only:
                // the newest page shows them too, so current sheds never disappear behind the files.
                var items = !writable && query.Cursor is null
                    ? WithUnwritten(page.Items, recorder.Query(new DecisionQuery(query.Device, query.Phase, query.Reason, query.Limit, null) { Kind = query.Kind }).Items, query)
                    : page.Items;
                return Results.Json(new DecisionHistoryResponse(
                    [.. items.Select(DecisionsEndpoint.Dto)],
                    page.Next,
                    page.RetainedFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    page.RetentionDays,
                    page.Partial,
                    writable));
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });
        return endpoints;
    }

    /// <summary>
    /// The newest page with the decisions held in memory that are newer than its newest one (not
    /// written yet), deduplicated by id and inside the query's time range; older pages are file
    /// positions and are never merged.
    /// </summary>
    internal static ImmutableArray<Decision> WithUnwritten(ImmutableArray<Decision> page, IReadOnlyList<Decision> memory, DecisionHistoryQuery query)
    {
        var newest = page.IsEmpty ? (DateTimeOffset?)null : page[0].At;
        var ids = page.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var unwritten = memory
            .Where(d => (newest is not { } at || d.At > at) && !ids.Contains(d.Id)
                && (query.From is not { } from || d.At >= from) && (query.To is not { } to || d.At < to))
            .OrderByDescending(d => d.At);
        return [.. unwritten, .. page];
    }

    /// <summary>The query, or null when any parameter is present but invalid (or repeated).</summary>
    internal static DecisionHistoryQuery? Parse(IQueryCollection query, int maxLimit)
    {
        if (query.Any(p => p.Value.Count > 1))
        {
            return null;
        }

        DateTimeOffset? Instant(string key, out bool invalid)
        {
            invalid = false;
            if (!query.TryGetValue(key, out var text))
            {
                return null;
            }

            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
            {
                return value;
            }

            invalid = true;
            return null;
        }

        var from = Instant("from", out var badFrom);
        var to = Instant("to", out var badTo);
        if (badFrom || badTo || (from is { } f && to is { } t && t <= f))
        {
            return null;
        }

        // The recent-decisions filters and their spelling are shared with GET api/decisions.
        var recent = new QueryCollection(query.Where(p => p.Key is not ("from" or "to" or "cursor")).ToDictionary(p => p.Key, p => p.Value));
        if (DecisionsEndpoint.Parse(recent, maxLimit) is not { } filters)
        {
            return null;
        }

        string? cursor = null;
        if (query.TryGetValue("cursor", out var cursorText))
        {
            cursor = cursorText.ToString();
            if (cursor.Length == 0)
            {
                return null;
            }
        }

        return new DecisionHistoryQuery(from, to, filters.Device, filters.Phase, filters.Reason, filters.Kind, filters.Limit, cursor);
    }
}
