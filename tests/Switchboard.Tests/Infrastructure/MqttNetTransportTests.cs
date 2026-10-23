using System.Text;
using MQTTnet.Protocol;
using Switchboard.Infrastructure.Mqtt;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// The real MQTTnet adapter: the topic guard must run before the client is touched, and the Last
/// Will must actually reach the CONNECT options MQTTnet sends.
/// </summary>
public sealed class MqttNetTransportTests
{
    private static readonly MqttConnectSettings Settings = new()
    {
        Credentials = new MqttCredentials("core-mosquitto", 1883, useTls: false, "addons", "pw"),
        ClientId = "switchboard",
        WillTopic = "switchboard/availability",
        WillPayload = "offline",
        WillRetain = true,

        // Not MQTTnet's 15 s default, so the test can tell ours from the library's.
        KeepAlivePeriod = TimeSpan.FromSeconds(25),
    };

    [Theory]
    [InlineData("zigbee2mqtt/x/set")]
    [InlineData("homeassistant/switch/other/x/config")]
    public async Task A_foreign_topic_is_refused_before_the_client_is_used(string topic)
    {
        // The client is not even connected: any other exception would mean the guard ran too late.
        using var transport = new MqttNetTransport(new MqttTopicGuard("homeassistant", "switchboard"));

        await Assert.ThrowsAsync<MqttTopicRefusedException>(
            () => transport.PublishAsync(topic, "ON", retain: false, CancellationToken.None));
    }

    [Fact]
    public async Task A_foreign_last_will_topic_is_refused_before_connecting()
    {
        using var transport = new MqttNetTransport(new MqttTopicGuard("homeassistant", "switchboard"));
        var settings = new MqttConnectSettings
        {
            Credentials = Settings.Credentials,
            ClientId = Settings.ClientId,
            WillTopic = "zigbee2mqtt/heater/set",
            WillPayload = "ON",
            WillRetain = true,
            KeepAlivePeriod = Settings.KeepAlivePeriod,
        };

        await Assert.ThrowsAsync<MqttTopicRefusedException>(() => transport.ConnectAsync(settings, CancellationToken.None));
    }

    [Fact]
    public void Last_will_reaches_the_connect_options_as_offline_retained_qos1()
    {
        var options = MqttNetTransport.BuildClientOptions(Settings);

        Assert.Equal("switchboard/availability", options.WillTopic);
        Assert.Equal("offline", Encoding.UTF8.GetString(options.WillPayload));
        Assert.True(options.WillRetain);
        Assert.Equal(MqttQualityOfServiceLevel.AtLeastOnce, options.WillQualityOfServiceLevel);
        Assert.Equal("switchboard", options.ClientId);
    }

    [Fact]
    public void Keep_alive_reaches_the_connect_options()
    {
        // It decides how soon the broker drops a frozen process and publishes the Last Will, so it is
        // a configured parameter, never the library default.
        var options = MqttNetTransport.BuildClientOptions(Settings);

        Assert.Equal(TimeSpan.FromSeconds(25), options.KeepAlivePeriod);
    }

    [Fact]
    public void Anonymous_brokers_get_no_credentials()
    {
        var anonymous = new MqttConnectSettings
        {
            Credentials = new MqttCredentials("broker", 1883, useTls: false, null, null),
            ClientId = "switchboard",
            WillTopic = Settings.WillTopic,
            WillPayload = Settings.WillPayload,
            WillRetain = true,
            KeepAlivePeriod = Settings.KeepAlivePeriod,
        };

        Assert.Null(MqttNetTransport.BuildClientOptions(anonymous).Credentials);
    }

    [Fact]
    public void Connection_settings_never_print_the_password()
    {
        Assert.DoesNotContain("pw", Settings.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("pw", Settings.Credentials.ToString(), StringComparison.Ordinal);
    }
}
