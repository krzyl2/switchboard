using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Switchboard.Application;

namespace Switchboard.Host.Web;

internal static class WebPipeline
{
    private const string SpaEntry = "index.html";

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

    public static WebApplication UseSwitchboardWeb(this WebApplication app)
    {
        // The guard runs first so a refused peer reaches nothing, not even static files.
        app.UseMiddleware<IngressGuardMiddleware>();
        app.UseMiddleware<IngressPathBaseMiddleware>();
        app.UseStaticFiles(SpaFiles);
        app.UseRouting();

        app.MapStatusApi();
        app.MapSignalsApi();

        // Unknown API routes are 404s, not the SPA shell.
        app.Map("/api/{**rest}", () => Results.NotFound());

        if (app.Environment.WebRootFileProvider.GetFileInfo(SpaEntry).Exists)
        {
            app.MapFallbackToFile(SpaEntry, SpaFiles);
        }
        else
        {
            // `dotnet run` without a web build: the service must still start and answer.
            app.MapGet("/", ([FromServices] AppVersion version) => Results.Text(
                $"Switchboard {version.Value} is running. The web UI has not been built.\n"
                + "Switchboard is not a protective device; it does not replace breakers or EVSE protection.\n",
                "text/plain"));
        }

        return app;
    }
}
