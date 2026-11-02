using Microsoft.AspNetCore.Mvc;
using Switchboard.Application;

namespace Switchboard.Host.Web;

internal static class WebPipeline
{
    private const string SpaEntry = "index.html";

    // The SPA loads its bundle, its stylesheet and the API from its own origin only (relative URLs under
    // the ingress path), and has no inline script or style; the favicon is a data: URL. The ingress
    // panel is an iframe of the Home Assistant page on the same origin, so 'self' may frame it.
    private const string ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'self'";

    // The shell must be revalidated on every load: after an add-on update a cached index.html would
    // point at hashed bundles the new image no longer has. Hashed bundles never change.
    private static readonly StaticFileOptions SpaFiles = new()
    {
        OnPrepareResponse = context =>
        {
            var headers = context.Context.Response.Headers;
            if (string.Equals(context.File.Name, SpaEntry, StringComparison.Ordinal))
            {
                headers.CacheControl = "no-cache";
            }
            else if (context.Context.Request.Path.StartsWithSegments("/assets", StringComparison.Ordinal))
            {
                headers.CacheControl = "public, max-age=31536000, immutable";
            }
        },
    };

    /// <summary>
    /// A POST, PUT or PATCH needs a JSON body: a cross-site form cannot send one without a CORS
    /// preflight, which the add-on never answers, so no other page can change anything here. (A DELETE
    /// carries no body and is not asked for one; it cannot be sent cross-site without a preflight either.)
    /// </summary>
    private static ValueTask<object?> RequireJsonOnWrites(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        return (HttpMethods.IsPost(request.Method) || HttpMethods.IsPut(request.Method) || HttpMethods.IsPatch(request.Method)) && !request.HasJsonContentType()
            ? ValueTask.FromResult<object?>(ApiResults.Refusal(StatusCodes.Status400BadRequest, ApiResults.BadRequestCode, "Send Content-Type: application/json."))
            : next(context);
    }

    public static WebApplication UseSwitchboardWeb(this WebApplication app)
    {
        app.Use((context, next) =>
        {
            context.Response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
            return next(context);
        });

        // An answer with a status and no body (a 403, a 404, a 413) gets a problem document, so every error of the API reads the same.
        app.UseStatusCodePages();

        // The guard runs first so a refused peer reaches nothing, not even static files.
        app.UseMiddleware<IngressGuardMiddleware>();
        app.UseMiddleware<RequestBodyLimitMiddleware>();
        app.UseMiddleware<IngressPathBaseMiddleware>();
        app.UseStaticFiles(SpaFiles);
        app.UseRouting();

        // Every API route sits in one group; a body-carrying method needs JSON there (see RequireJsonOnWrites).
        var api = app.MapGroup("/api");
        api.AddEndpointFilter(RequireJsonOnWrites);

        api.MapStatusApi();
        api.MapSignalsApi();
        api.MapGuardApi();
        api.MapDecisionsApi();
        api.MapDispatchApi();
        api.MapPowerApi();
        api.MapStatisticsApi();
        api.MapExcursionsApi();
        api.MapDevicesApi();
        api.MapSetupApi();
        api.MapModeApi();
        api.MapBalanceApi();
        api.MapEconomicsApi();
        api.MapPlanApi();
        api.MapEvApi();
        api.MapGoalsApi();
        api.MapControlApi();
        api.MapDecisionHistoryApi();

        // The development sink's record exists only in standalone development replay.
        if (app.Services.GetRequiredService<Composition.SignalSourceSelection>().IsReplay)
        {
            api.MapDevApi();
        }

        // Unknown API routes are 404s, not the SPA shell.
        app.Map("/api/{**rest}", () => TypedResults.NotFound());

        if (app.Environment.WebRootFileProvider.GetFileInfo(SpaEntry).Exists)
        {
            app.MapFallbackToFile(SpaEntry, SpaFiles);
        }
        else
        {
            // `dotnet run` without a web build: the service must still start and answer.
            app.MapGet("/", ([FromServices] AppVersion version) => TypedResults.Text(
                $"Switchboard {version.Value} is running. The web UI has not been built.\n"
                + "Switchboard is not a protective device; it does not replace breakers or EVSE protection.\n",
                "text/plain"));
        }

        return app;
    }
}
