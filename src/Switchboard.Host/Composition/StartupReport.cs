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

        // Resolved here, before anything starts: a replay setting in an add-on stops the process.
        var source = services.GetRequiredService<SignalSourceSelection>();

        logger.LogInformation(
            "Switchboard {Version} starting as {Deployment}; mode {Mode}, log level {LogLevel}; configuration {ConfigPath}",
            version.Value,
            environment.IsAddon ? "add-on" : "standalone container",
            options.Mode,
            options.LogLevel,
            environment.ConfigPath);

        foreach (var warning in options.Warnings)
        {
            logger.LogWarning("{Warning}", warning);
        }

        if (options.Mode == OperatingMode.Active)
        {
            logger.LogWarning("Mode is 'active', but this build contains no command path: nothing will be commanded.");
        }

        if (source.IsReplay)
        {
            logger.LogWarning(
                "DEVELOPMENT REPLAY: signals come from {File} at {Speed}x speed on the recording's clock. Home Assistant and MQTT are not connected; no health entity is published.",
                source.ReplayPath,
                source.ReplaySpeed);
            if (!string.IsNullOrEmpty(environment.MqttHost))
            {
                logger.LogWarning("{Variable} and the other MQTT settings are ignored in replay mode.", EnvironmentVariableNames.MqttHost);
            }
        }

        if (services.GetRequiredService<IngressAccessPolicy>().TrustAllRequests)
        {
            logger.LogWarning(
                "{Variable} is set: the ingress source-address check is OFF. Development use only.",
                EnvironmentVariableNames.DevTrustAllRequests);
        }
    }
}
