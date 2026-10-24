using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Switchboard.Host.Hosting;

namespace Switchboard.Tests.Host;

/// <summary>
/// The real Program in memory, with the background links and the signal pipeline removed: these
/// tests exercise the web surface, and link states and signal diagnostics are set directly through
/// their ports.
/// </summary>
internal sealed class SwitchboardHostFactory : WebApplicationFactory<Program>
{
    private static readonly Type[] Links =
    [
        typeof(HomeAssistantLinkService), typeof(MqttLinkService), typeof(HeartbeatService),
        typeof(SignalStoreService), typeof(ConfigurationWatcherService), typeof(ReplayService),
    ];

    private readonly Dictionary<string, string?> _settings;
    private readonly string _webRoot;
    private readonly Action<IServiceCollection>? _replaceLinks;

    /// <param name="replaceLinks">
    /// When given, the background links stay registered and this replaces their transports instead.
    /// </param>
    public SwitchboardHostFactory(string webRoot, IDictionary<string, string?>? settings = null, Action<IServiceCollection>? replaceLinks = null)
    {
        _webRoot = webRoot;
        _replaceLinks = replaceLinks;
        _settings = new Dictionary<string, string?>
        {
            // Never read a real /data/options.json, and never inherit the developer's environment.
            ["SWITCHBOARD_OPTIONS_PATH"] = Path.Combine(webRoot, "no-options.json"),
            ["SWITCHBOARD_CONFIG_PATH"] = Path.Combine(webRoot, "no-switchboard.yaml"),
            ["SWITCHBOARD_DEV_REPLAY"] = null,
            ["SUPERVISOR_TOKEN"] = null,
            ["SWITCHBOARD_DEV_TRUST_ALL_REQUESTS"] = null,
            ["SWITCHBOARD_MODE"] = null,
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            _settings[key] = value;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Production: no static web assets from the project folder, only the web root given here.
        builder.UseEnvironment(Environments.Production);
        builder.UseSetting(WebHostDefaults.WebRootKey, _webRoot);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(_settings));
        builder.ConfigureTestServices(services =>
        {
            if (_replaceLinks is not null)
            {
                _replaceLinks(services);
                return;
            }

            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService) && Links.Contains(d.ImplementationType)).ToList())
            {
                services.Remove(descriptor);
            }
        });
    }
}

/// <summary>A throwaway web root, optionally with an index.html, so tests never depend on a web build.</summary>
internal sealed class TempWebRoot : IDisposable
{
    public TempWebRoot(string? indexHtml = null)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "switchboard-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        if (indexHtml is not null)
        {
            File.WriteAllText(System.IO.Path.Combine(Path, "index.html"), indexHtml);
        }
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp folder is harmless.
        }
    }
}
