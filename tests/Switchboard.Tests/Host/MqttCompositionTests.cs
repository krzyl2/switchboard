using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Switchboard.Application.Guard;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Infrastructure.Mqtt;
using Switchboard.Infrastructure.Persistence;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// The MQTT side as the real host composes it. The client id is the configured prefix and an id kept in the data folder, so a restart connects as the same
/// client and another install on the same broker does not. The add-on option <c>mqtt_enabling_commands</c> reaches the entities the way <c>mode</c> reaches the
/// mode: from options.json, which only the Supervisor writes (never the panel, the wizard or MQTT), or from its environment variable in a standalone container.
/// </summary>
public sealed class MqttCompositionTests
{
    [Fact]
    public async Task The_client_id_is_the_prefix_and_the_install_id_and_a_restart_keeps_it()
    {
        using var root = new TempWebRoot();
        using var other = new TempWebRoot();

        var first = await ConnectedClientIdAsync(root.Path);
        var restarted = await ConnectedClientIdAsync(root.Path);
        var elsewhere = await ConnectedClientIdAsync(other.Path);

        Assert.Matches(@"\Aswitchboard-[0-9a-f]{12}\z", first);
        Assert.Equal(first, restarted);
        Assert.NotEqual(first, elsewhere);
        Assert.Contains(first["switchboard-".Length..], await File.ReadAllTextAsync(Path.Combine(root.Path, "data", MqttClientIdFile.Name), TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public void Without_the_option_a_release_from_MQTT_is_refused()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);

        Assert.True(ReleaseFromMqtt(factory));
    }

    [Theory]
    [InlineData("""{"mode":"shadow","log_level":"info","mqtt_enabling_commands":false}""", true)]
    [InlineData("""{"mode":"shadow","log_level":"info","mqtt_enabling_commands":"true"}""", true)]
    [InlineData("""{"mode":"shadow","log_level":"info","mqtt_enabling_commands":true}""", false)]
    public async Task The_option_in_options_json_decides_whether_MQTT_may_release_the_kill_switch(string options, bool stillEngaged)
    {
        using var root = new TempWebRoot();
        var path = Path.Combine(root.Path, "options.json");
        await File.WriteAllTextAsync(path, options, TestContext.Current.CancellationToken);
        using var factory = new SwitchboardHostFactory(root.Path, new Dictionary<string, string?> { ["SWITCHBOARD_OPTIONS_PATH"] = path });

        Assert.Equal(stillEngaged, ReleaseFromMqtt(factory));
    }

    [Theory]
    [InlineData("true", false)]
    [InlineData("True", true)]
    [InlineData("false", true)]
    public void The_environment_variable_sets_the_option_in_a_standalone_container(string value, bool stillEngaged)
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path, new Dictionary<string, string?> { ["SWITCHBOARD_MQTT_ENABLING_COMMANDS"] = value });

        Assert.Equal(stillEngaged, ReleaseFromMqtt(factory));
    }

    // Engages the kill switch from the panel, then sends a release on its MQTT command topic; true when it is still engaged.
    private static bool ReleaseFromMqtt(SwitchboardHostFactory factory)
    {
        var killSwitch = factory.Services.GetRequiredService<IKillSwitchControl>();
        killSwitch.Set(true, KillSwitchSource.Api);
        factory.Services.GetRequiredService<GuardEntities>().HandleCommand("switchboard/kill_switch/set", GuardEntities.PayloadOff);
        return killSwitch.Status.Engaged;
    }

    private static async Task<string> ConnectedClientIdAsync(string root)
    {
        var transport = new FakeMqttTransport(new MqttTopicGuard("homeassistant", "switchboard"));
        await using var factory = new SwitchboardHostFactory(root, replaceLinks: services =>
        {
            services.RemoveAll<IMqttTransport>();
            services.AddSingleton<IMqttTransport>(transport);
            services.RemoveAll<IMqttCredentialSource>();
            services.AddSingleton<IMqttCredentialSource>(new FakeCredentialSource());
            services.RemoveAll<IWebSocketTransportFactory>();
            services.AddSingleton<IWebSocketTransportFactory>(new ScriptedTransportFactory());
        });

        _ = factory.Server;
        await transport.UntilAsync(o => o.Kind == "connect");
        return Assert.Single(transport.Connects).ClientId;
    }
}
