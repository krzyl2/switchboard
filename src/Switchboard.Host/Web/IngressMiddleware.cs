using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Switchboard.Application.Options;

namespace Switchboard.Host.Web;

/// <summary>Refuses every request that did not come through the ingress proxy.</summary>
internal sealed class IngressGuardMiddleware(RequestDelegate next, IngressAccessPolicy policy)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (policy.IsAllowed(context.Connection.RemoteIpAddress))
        {
            return next(context);
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}

/// <summary>
/// The Supervisor serves the add-on under /api/hassio_ingress/&lt;token&gt; and says so in
/// X-Ingress-Path. Using it as PathBase keeps generated URLs inside that prefix. The header is
/// routing information only; access is decided by <see cref="IngressGuardMiddleware"/>.
/// </summary>
internal sealed class IngressPathBaseMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Ingress-Path";

    public Task InvokeAsync(HttpContext context)
    {
        var values = context.Request.Headers[HeaderName];
        if (values.Count == 1 && values[0] is { Length: > 0 } raw && raw[0] == '/' && !raw.Contains("//", StringComparison.Ordinal))
        {
            context.Request.PathBase = new PathString(raw.TrimEnd('/'));
        }

        return next(context);
    }
}

/// <summary>
/// The global limit for every request body (<see cref="IngressOptions.MaxRequestBodyBytes"/>); an
/// endpoint may hold a smaller one of its own (<see cref="RequestBody.WithBodyLimit"/>). A declared
/// length above it is refused before any endpoint runs; a body without one (chunked) is cut off by the
/// server once it grows past it, which the one body reader (<see cref="RequestBody.ReadBodyAsync"/>)
/// answers as 413.
/// </summary>
internal sealed class RequestBodyLimitMiddleware(RequestDelegate next, IOptions<IngressOptions> options)
{
    public Task InvokeAsync(HttpContext context)
    {
        var limit = options.Value.MaxRequestBodyBytes;
        if (context.Request.ContentLength > limit)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return Task.CompletedTask;
        }

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        {
            feature.MaxRequestBodySize = limit;
        }

        return next(context);
    }
}
