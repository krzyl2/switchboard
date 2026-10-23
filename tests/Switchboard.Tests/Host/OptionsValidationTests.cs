using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Switchboard.Application.Options;
using Switchboard.Host.Composition;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// Control parameters live in appsettings.json, not in code, so the file itself is part of the
/// product: it must bind completely, and nonsense must stop the process at start instead of
/// producing a heartbeat that expires between beats or a reconnect loop with no delay.
/// </summary>
public sealed class OptionsValidationTests
{
    private static readonly string ShippedSettings = RepoPaths.Combine("src", "Switchboard.Host", "appsettings.json");

    [Fact]
    public void The_shipped_appsettings_bind_and_validate()
    {
        using var provider = Build();

        provider.GetRequiredService<IStartupValidator>().Validate();

        var ingress = provider.GetRequiredService<IOptions<IngressOptions>>().Value;
        var heartbeat = provider.GetRequiredService<IOptions<HeartbeatOptions>>().Value;
        var mqtt = provider.GetRequiredService<IOptions<MqttOptions>>().Value;
        var homeAssistant = provider.GetRequiredService<IOptions<HomeAssistantOptions>>().Value;
        Assert.Equal(8099, ingress.Port);
        Assert.Equal("172.30.32.2", ingress.TrustedProxy);
        Assert.Equal(TimeSpan.FromSeconds(10), heartbeat.Interval);
        Assert.Equal(TimeSpan.FromSeconds(30), heartbeat.ExpireAfter);
        Assert.Equal("homeassistant", mqtt.DiscoveryPrefix);
        Assert.Equal("switchboard", mqtt.BaseTopic);
        Assert.Equal("switchboard", mqtt.ClientId);
        Assert.Equal("http://supervisor/services/mqtt", mqtt.SupervisorServiceUrl);
        Assert.Equal(TimeSpan.FromSeconds(15), mqtt.KeepAlivePeriod);
        Assert.Equal(TimeSpan.FromSeconds(4), mqtt.ShutdownTimeout);
        Assert.Equal("ws://supervisor/core/websocket", homeAssistant.SupervisorWebSocketUrl);
    }

    [Theory]
    [InlineData("00:00:20")]
    [InlineData("00:00:15")]
    [InlineData("00:00:05")]
    public void Heartbeat_expiry_not_longer_than_two_intervals_is_rejected(string expireAfter)
    {
        // With Interval 10 s, one late beat would flip the entity unavailable and shed loads for nothing.
        using var provider = Build(("Switchboard:Heartbeat:ExpireAfter", expireAfter));

        AssertRejected<HeartbeatOptions>(provider, "ExpireAfter");
    }

    [Fact]
    public void Fractional_expiry_is_rejected_because_expire_after_is_whole_seconds()
    {
        using var provider = Build(("Switchboard:Heartbeat:ExpireAfter", "00:00:30.5"));

        AssertRejected<HeartbeatOptions>(provider, "whole number");
    }

    [Theory]
    [InlineData("Switchboard:HomeAssistant:ReconnectMinDelay", "00:00:00")]
    [InlineData("Switchboard:HomeAssistant:PingInterval", "-00:00:01")]
    [InlineData("Switchboard:HomeAssistant:PongTimeout", "00:00:00")]
    [InlineData("Switchboard:Heartbeat:Interval", "00:00:00")]
    public void Non_positive_durations_are_rejected(string key, string value)
    {
        using var provider = Build((key, value));

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void Non_positive_mqtt_delay_is_rejected()
    {
        using var provider = Build(("Switchboard:Mqtt:ReconnectMaxDelay", "00:00:00"));

        AssertRejected<MqttOptions>(provider, "ReconnectMaxDelay");
    }

    [Theory]
    [InlineData("Switchboard:Mqtt:KeepAlivePeriod", "00:00:00")]
    [InlineData("Switchboard:Mqtt:ShutdownTimeout", "00:00:00")]
    public void Non_positive_mqtt_timing_is_rejected(string key, string value)
    {
        using var provider = Build((key, value));

        AssertRejected<MqttOptions>(provider, key[(key.LastIndexOf(':') + 1)..]);
    }

    [Theory]
    [InlineData("00:00:30")]
    [InlineData("00:01:00")]
    public void Keep_alive_not_shorter_than_heartbeat_expiry_is_rejected(string keepAlive)
    {
        // The keep-alive bounds how long a frozen session's retained "online" survives for a restarted
        // HA to read; it must stay below the 30 s state expiry.
        using var provider = Build(("Switchboard:Mqtt:KeepAlivePeriod", keepAlive));

        AssertRejected<MqttOptions>(provider, "KeepAlivePeriod");
    }

    [Fact]
    public void Min_delay_above_max_delay_is_rejected()
    {
        using var provider = Build(("Switchboard:Mqtt:ReconnectMinDelay", "00:05:00"));

        AssertRejected<MqttOptions>(provider, "must not exceed");
    }

    [Fact]
    public void Pong_timeout_not_shorter_than_ping_interval_is_rejected()
    {
        using var provider = Build(("Switchboard:HomeAssistant:PongTimeout", "00:00:30"));

        AssertRejected<HomeAssistantOptions>(provider, "PongTimeout");
    }

    [Theory]
    [InlineData("Switchboard:Mqtt:BaseTopic", "switch/board")]
    [InlineData("Switchboard:Mqtt:BaseTopic", "#")]
    [InlineData("Switchboard:Mqtt:DiscoveryPrefix", "")]
    public void Topic_roots_that_would_widen_the_topic_guard_are_rejected(string key, string value)
    {
        using var provider = Build((key, value));

        AssertRejected<MqttOptions>(provider, key[(key.LastIndexOf(':') + 1)..]);
    }

    [Theory]
    [InlineData("Switchboard:Ingress:Port", "0")]
    [InlineData("Switchboard:Ingress:Port", "70000")]
    [InlineData("Switchboard:Ingress:TrustedProxy", "supervisor")]
    public void Ingress_nonsense_is_rejected(string key, string value)
    {
        using var provider = Build((key, value));

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<IngressOptions>>().Value);
    }

    [Fact]
    public void A_missing_parameter_is_rejected_rather_than_defaulted()
    {
        using var provider = Build(("Switchboard:Heartbeat:Interval", null));

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<HeartbeatOptions>>().Value);
    }

    [Fact]
    public void A_misspelt_key_is_rejected_instead_of_silently_ignored()
    {
        using var provider = Build(("Switchboard:Heartbeat:ExpiresAfter", "00:01:00"));

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IOptions<HeartbeatOptions>>().Value);
    }

    private static ServiceProvider Build(params (string Key, string? Value)[] overrides)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(ShippedSettings, optional: false)
            .AddInMemoryCollection(overrides.Select(o => new KeyValuePair<string, string?>(o.Key, o.Value)))
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSwitchboardOptions();
        return services.BuildServiceProvider();
    }

    private static void AssertRejected<TOptions>(ServiceProvider provider, string expectedFragment)
        where TOptions : class
    {
        var failure = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<TOptions>>().Value);
        Assert.Contains(failure.Failures, f => f.Contains(expectedFragment, StringComparison.Ordinal));
    }
}
