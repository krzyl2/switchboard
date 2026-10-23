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
