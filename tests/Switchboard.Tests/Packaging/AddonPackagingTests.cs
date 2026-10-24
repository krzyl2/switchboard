using System.Text.Json;
using System.Text.RegularExpressions;
using Switchboard.Tests.Support;
using YamlDotNet.Serialization;

namespace Switchboard.Tests.Packaging;

/// <summary>
/// The add-on manifest is the install-time contract with the Supervisor (PKG-01, PKG-05). These
/// checks keep it in step with the code: one ingress port and no other, MQTT from the Supervisor,
/// shadow by default, and an options form users can read in both languages.
/// </summary>
[Trait("Category", "Packaging")]
public sealed class AddonPackagingTests
{
    private static readonly Lazy<Dictionary<object, object>> Config = new(() => LoadYaml("switchboard", "config.yaml"));

    [Fact]
    public void Slug_is_switchboard()
    {
        Assert.Equal("switchboard", Scalar(Config.Value, "slug"));
    }

    [Fact]
    public void Ingress_is_enabled_on_the_port_the_process_listens_on()
    {
        // ingress_port is omitted while it equals the Supervisor default; if the shipped port
        // changes, the manifest must declare it again or ingress points at nothing.
        Assert.Equal("true", Scalar(Config.Value, "ingress"));
        var ingressPort = Scalar(Config.Value, "ingress_port") ?? SupervisorDefaultIngressPort;
        Assert.Equal(ShippedIngressPort().ToString(System.Globalization.CultureInfo.InvariantCulture), ingressPort);
    }

    [Fact]
    public void Home_Assistant_api_access_is_requested()
    {
        Assert.Equal("true", Scalar(Config.Value, "homeassistant_api"));
    }

    [Fact]
    public void Mqtt_is_required_from_the_supervisor()
    {
        Assert.Contains("mqtt:need", Sequence(Config.Value, "services"));
    }

    [Fact]
    public void Shadow_is_the_default_mode()
    {
        // D7 / MODE-01: a fresh install computes and logs, it does not switch.
        Assert.Equal("shadow", Scalar(Map(Config.Value, "options"), "mode"));
    }

    [Fact]
    public void Mode_schema_offers_exactly_shadow_and_active()
    {
        Assert.Equal("list(shadow|active)", Scalar(Map(Config.Value, "schema"), "mode"));
    }

    [Fact]
    public void Both_target_architectures_are_declared()
    {
        var arch = Sequence(Config.Value, "arch");

        Assert.Contains("amd64", arch);
        Assert.Contains("aarch64", arch);
    }

    [Fact]
    public void Store_description_says_it_is_not_a_protective_device()
    {
        // The store card is the first thing a user reads; no copy may suggest protection.
        var description = Scalar(Config.Value, "description");

        Assert.Contains("Best-effort", description, StringComparison.Ordinal);
        Assert.Contains("not a protective device", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Image_arch_labels_use_the_manifest_arch_names()
    {
        // Docker calls ARM "arm64"; Home Assistant calls it "aarch64". The label must carry
        // Home Assistant's name for every arch the manifest declares.
        var labels = Regex.Matches(Dockerfile(), @"io\.hass\.arch=""([^""]*)""")
            .Select(m => m.Groups[1].Value)
            .Order()
            .ToList();

        Assert.Equal(Sequence(Config.Value, "arch").Order().ToList(), labels);
    }

    [Fact]
    public void Mqtt_shutdown_fits_well_inside_the_container_stop_grace()
    {
        // s6 kills the process when S6_SERVICES_GRACETIME runs out. The MQTT step must give up
        // early enough that the Home Assistant close after it still runs.
        var grace = Regex.Match(Dockerfile(), @"ENV S6_SERVICES_GRACETIME=(\d+)");
        Assert.True(grace.Success, "switchboard/Dockerfile does not set S6_SERVICES_GRACETIME.");
        var graceTime = TimeSpan.FromMilliseconds(int.Parse(grace.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));

        using var settings = JsonDocument.Parse(File.ReadAllText(RepoPaths.Combine("src", "Switchboard.Host", "appsettings.json")));
        var shutdownTimeout = TimeSpan.Parse(
            settings.RootElement.GetProperty("Switchboard").GetProperty("Mqtt").GetProperty("ShutdownTimeout").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(shutdownTimeout * 2 <= graceTime, $"Mqtt:ShutdownTimeout {shutdownTimeout} takes more than half of the s6 grace {graceTime}.");
    }

    [Theory]
    [InlineData("ports")]
    [InlineData("ports_description")]
    [InlineData("host_network")]
    public void No_port_beyond_ingress_is_exposed(string key)
    {
        // The add-on holds a Supervisor token; anything reachable from the LAN is an attack surface.
        Assert.False(Config.Value.ContainsKey(key), $"config.yaml must not declare '{key}'.");
    }

    [Fact]
    public void Liveness_is_an_http_probe_of_the_ingress_port_not_a_tcp_watchdog()
    {
        // A tcp:// watchdog still connects to a hung process (the kernel completes the handshake);
        // only an HTTP request proves the app answers. Loopback passes the ingress guard.
        Assert.False(Config.Value.ContainsKey("watchdog"), "config.yaml must not declare a watchdog; the image HEALTHCHECK replaces it.");

        var probe = Regex.Match(Dockerfile(), @"HEALTHCHECK\b[^\n]*\\\r?\n\s*CMD curl [^\n]*http://127\.0\.0\.1:(\d+)/api/status");
        Assert.True(probe.Success, "switchboard/Dockerfile has no HEALTHCHECK probing http://127.0.0.1:<port>/api/status.");
        Assert.Equal(ShippedIngressPort().ToString(System.Globalization.CultureInfo.InvariantCulture), probe.Groups[1].Value);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pl")]
    public void Translations_name_every_option_in_the_schema(string language)
    {
        var translation = LoadYaml("switchboard", "translations", language + ".yaml");
        var configuration = Map(translation, "configuration");

        foreach (var option in Map(Config.Value, "schema").Keys.Cast<string>())
        {
            Assert.True(configuration.ContainsKey(option), $"{language}.yaml does not translate option '{option}'.");
            Assert.False(string.IsNullOrWhiteSpace(Scalar(Map(configuration, option), "name")), $"{language}.yaml has no name for '{option}'.");
        }
    }

    [Fact]
    public void Repository_descriptor_names_the_repository()
    {
        var repository = LoadYaml("repository.yaml");

        foreach (var key in new[] { "name", "url", "maintainer" })
        {
            Assert.False(string.IsNullOrWhiteSpace(Scalar(repository, key)), $"repository.yaml has no '{key}'.");
        }
    }

    // The Supervisor's ingress_port when the manifest does not declare one.
    private const string SupervisorDefaultIngressPort = "8099";

    private static int ShippedIngressPort()
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(RepoPaths.Combine("src", "Switchboard.Host", "appsettings.json")));
        return settings.RootElement.GetProperty("Switchboard").GetProperty("Ingress").GetProperty("Port").GetInt32();
    }

    private static string Dockerfile() => File.ReadAllText(RepoPaths.Combine("switchboard", "Dockerfile"));

    private static Dictionary<object, object> LoadYaml(params string[] path)
    {
        var file = RepoPaths.Combine(path);
        Assert.True(File.Exists(file), $"{string.Join('/', path)} is missing.");
        return new DeserializerBuilder().Build().Deserialize<Dictionary<object, object>>(File.ReadAllText(file));
    }

    private static Dictionary<object, object> Map(Dictionary<object, object> node, string key) =>
        Assert.IsType<Dictionary<object, object>>(node.GetValueOrDefault(key), exactMatch: false);

    private static List<string> Sequence(Dictionary<object, object> node, string key) =>
        Assert.IsType<List<object>>(node.GetValueOrDefault(key), exactMatch: false).Select(i => i.ToString()!).ToList();

    private static string? Scalar(Dictionary<object, object> node, string key) => node.GetValueOrDefault(key)?.ToString();
}
