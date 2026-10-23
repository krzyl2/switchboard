using Switchboard.Application;
using Switchboard.Application.Modes;
using Switchboard.Host.Web;
using Switchboard.Infrastructure.Runtime;

namespace Switchboard.Host.Composition;

internal static class StartupReport
{
    /// <summary>One summary at startup: what runs, in which mode, and anything the user must know. No secrets.</summary>
    public static void Log(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Switchboard.Startup");
        var version = services.GetRequiredService<AppVersion>();
        var environment = services.GetRequiredService<RuntimeEnvironment>();
        var options = services.GetRequiredService<ResolvedAddonOptions>();

        logger.LogInformation(
            "Switchboard {Version} starting as {Deployment}; mode {Mode}, log level {LogLevel}",
            version.Value,
            environment.IsAddon ? "add-on" : "standalone container",
            options.Mode,
            options.LogLevel);

        foreach (var warning in options.Warnings)
        {
            logger.LogWarning("{Warning}", warning);
        }

        if (options.Mode == OperatingMode.Active)
        {
            logger.LogWarning("Mode is 'active', but this build contains no command path: nothing will be commanded.");
        }

        if (services.GetRequiredService<IngressAccessPolicy>().TrustAllRequests)
        {
            logger.LogWarning(
                "{Variable} is set: the ingress source-address check is OFF. Development use only.",
                EnvironmentVariableNames.DevTrustAllRequests);
        }
    }
}
