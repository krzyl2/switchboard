using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Switchboard.Application;
using Switchboard.Application.Effector;
using Switchboard.Host.Composition;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Infrastructure.Replay;
using Switchboard.Tests.Support;

namespace Switchboard.Tests;

/// <summary>
/// SAFE-05 as structure (Stage C): there is exactly one way a state-changing message can leave the
/// process, and every step of it is pinned. The allow-list has exactly one state-changing type; only
/// the client serializes it, only the channel calls the client's send, only the command port uses the
/// channel, and only the effector gate holds the port. The development sink and plant exist only in
/// standalone development replay. Application never names an entity, a domain or a service.
/// </summary>
public sealed partial class ControlArchitectureTests
{
    [Fact]
    public void The_allow_list_has_seven_types_and_exactly_one_changes_state()
    {
        Assert.Equal(7, HomeAssistantCommandAllowList.Allowed.Count);
        Assert.Equal(
            ["call_service"],
            HomeAssistantCommandAllowList.Allowed.Except(HomeAssistantCommandAllowList.ReadOnlyQueries).Except(["ping", "subscribe_entities", "unsubscribe_events"]));
    }

    [Fact]
    public void No_source_names_another_state_changing_message_anywhere()
    {
        // Not even in the allow-list: fire_event, execute_script, registry or config writes, the
        // REST services API, homeassistant.* services and templates have no literal in src.
        var offenders = SourceFiles("src")
            .SelectMany(f => StringLiteral().Matches(File.ReadAllText(f)).Select(m => (File: Path.GetFileName(f), Literal: m.Value)))
            .Where(l => Forbidden().IsMatch(l.Literal))
            .Select(l => $"{l.File}: {l.Literal}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Only_the_channel_calls_the_clients_service_call()
    {
        Assert.Equal(["CallServiceChannel.cs", "HomeAssistantClient.cs"], FilesMatching(CallServiceCall()));
        Assert.True(typeof(HomeAssistantClient).GetMethod("CallServiceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.IsAssembly);
    }

    [Fact]
    public void Only_the_command_port_sends_through_a_channel()
    {
        // The interface is declared once and implemented by the protection link and the development
        // sink; the composition wires it, the development endpoint only reads the sink's entries; the
        // port is the one place that sends.
        Assert.Equal(
            ["CallServiceChannel.cs", "DevCommandSink.cs", "DevEndpoint.cs", "HomeAssistantDeviceCommandPort.cs", "SwitchboardServices.cs"],
            FilesMatching(ChannelMention()));
        Assert.Equal(["HomeAssistantDeviceCommandPort.cs"], FilesMatching(ChannelSend()));
        var holders = typeof(HomeAssistantClient).Assembly.GetTypes()
            .Concat(typeof(AppVersion).Assembly.GetTypes())
            .Concat(typeof(SwitchboardServices).Assembly.GetTypes())
            .Where(t => t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Any(f => f.FieldType == typeof(ICallServiceChannel)))
            .ToList();
        Assert.Equal([typeof(HomeAssistantDeviceCommandPort)], holders);
    }

    [Fact]
    public void Only_the_gate_submits_commands()
    {
        // Submit is a port call only the effector gate makes; nothing in Application, the hosts or
        // the endpoints can push a command past protection.
        Assert.Equal(["EffectorGate.cs", "HomeAssistantDeviceCommandPort.cs", "IDeviceCommandPort.cs"], FilesMatching(PortSubmit()));
    }

    [Fact]
    public void Application_never_names_an_entity_a_domain_or_a_service()
    {
        // Entity IDs exist only in the configuration layer's data; domains and services only in the
        // allow-list (Infrastructure). Comments may explain; literals may not.
        var offenders = SourceFiles("src/Switchboard.Application")
            .Where(f => !f.Replace('\\', '/').Contains("/Configuration/", StringComparison.Ordinal) && !f.Replace('\\', '/').Contains("/Setup/", StringComparison.Ordinal))
            .SelectMany(f => StringLiteral().Matches(File.ReadAllText(f)).Select(m => (File: Path.GetFileName(f), Literal: m.Value)))
            .Where(l => EntityLiteral().IsMatch(l.Literal))
            .Select(l => $"{l.File}: {l.Literal}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Live_composition_sends_through_the_protection_link()
    {
        using var provider = Build();

        Assert.IsType<HomeAssistantCallServiceChannel>(provider.GetRequiredService<ICallServiceChannel>());
        Assert.Same(provider.GetRequiredService<HomeAssistantDeviceCommandPort>(), provider.GetRequiredService<ICommandPathStatus>());
    }

    [Fact]
    public void Development_replay_sends_to_the_sink_and_never_to_a_link()
    {
        using var provider = Build(("SWITCHBOARD_DEV_REPLAY", RepoPaths.Combine("tests", "fixtures", "replay", "synthetic-dispatch-day.csv")));

        Assert.IsType<DevCommandSink>(provider.GetRequiredService<ICallServiceChannel>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_development_plant_outside_development_replay_stops_the_start(bool addOn)
    {
        var settings = new List<(string, string?)> { ("SWITCHBOARD_DEV_PLANT", RepoPaths.Combine("tests", "fixtures", "replay", "synthetic-plant.json")) };
        if (addOn)
        {
            settings.Add(("SUPERVISOR_TOKEN", "token"));
        }

        using var provider = Build([.. settings]);

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ICallServiceChannel>());
    }

    private static List<string> FilesMatching(Regex pattern) =>
        [.. SourceFiles("src").Where(f => pattern.IsMatch(File.ReadAllText(f))).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)];

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.GetFiles(RepoPaths.Combine(root.Split('/')), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static ServiceProvider Build(params (string Key, string? Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(RepoPaths.Combine("src", "Switchboard.Host", "appsettings.json"), optional: false)
            .AddInMemoryCollection([
                new("SWITCHBOARD_OPTIONS_PATH", Path.Combine(Path.GetTempPath(), "switchboard-no-options.json")),
                new("SWITCHBOARD_CONFIG_PATH", Path.Combine(Path.GetTempPath(), "switchboard-no-config.yaml")),
                new("SWITCHBOARD_DATA_PATH", Path.Combine(Path.GetTempPath(), "switchboard-no-data")),
                .. settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)),
            ])
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(logging => logging.ClearProviders());
        services.AddSingleton<IHostApplicationLifetime, Microsoft.Extensions.Hosting.Internal.ApplicationLifetime>();
        services.AddSwitchboard();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [GeneratedRegex("\"(?:[^\"\\\\\\n]|\\\\.)*\"")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"fire_event|execute_script|subscribe_trigger|render_template|config/[a-z_]+/(?:update|remove|delete|create)|config_entries/|api/services|homeassistant\.|\btoggle\b|script\.turn_on|automation\.", RegexOptions.IgnoreCase)]
    private static partial Regex Forbidden();

    [GeneratedRegex(@"\.CallServiceAsync\(|Task<CallOutcome> CallServiceAsync\(")]
    private static partial Regex CallServiceCall();

    [GeneratedRegex(@"ICallServiceChannel\b")]
    private static partial Regex ChannelMention();

    [GeneratedRegex(@"[cC]hannel\.SendAsync\(")]
    private static partial Regex ChannelSend();

    [GeneratedRegex(@"\bIDeviceCommandPort\b|_port!?\.Submit\(")]
    private static partial Regex PortSubmit();

    [GeneratedRegex(@"""(?:switch|number|input_boolean|input_number|sensor|binary_sensor)\.[a-z0-9_]+""|""(?:turn_on|turn_off|set_value|call_service)""")]
    private static partial Regex EntityLiteral();
}
