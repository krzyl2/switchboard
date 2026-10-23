using Microsoft.AspNetCore.Mvc;
using Switchboard.Application;

namespace Switchboard.Host.Web;

internal static class WebPipeline
{
    private const string SpaEntry = "index.html";

    public static WebApplication UseSwitchboardWeb(this WebApplication app)
    {
        // The guard runs first so a refused peer reaches nothing, not even static files.
        app.UseMiddleware<IngressGuardMiddleware>();
        app.UseMiddleware<IngressPathBaseMiddleware>();
        app.UseStaticFiles();
        app.UseRouting();

        app.MapStatusApi();

        // Unknown API routes are 404s, not the SPA shell.
        app.Map("/api/{**rest}", () => Results.NotFound());

        if (app.Environment.WebRootFileProvider.GetFileInfo(SpaEntry).Exists)
        {
            app.MapFallbackToFile(SpaEntry);
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
