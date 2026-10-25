using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Switchboard.Application.Options;
using Switchboard.Host.Composition;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// Switchboard:Statistics is operational (how the runtime UI integrates and keeps its history),
/// not control, but nonsense still stops the process at start: a bucket that does not tile a
/// quarter hour breaks local days in half-hour zones, a retention shorter than a 25-hour day cannot
/// show the DST day, and a file cap below what the writer needs would reset the history.
/// </summary>
public sealed class StatisticsOptionsTests
{
    private static readonly string ShippedSettings = RepoPaths.Combine("src", "Switchboard.Host", "appsettings.json");

    [Fact]
    public void The_shipped_statistics_settings_bind_and_validate()
    {
        using var provider = Build();

        var options = provider.GetRequiredService<IOptions<StatisticsOptions>>().Value;

        Assert.Equal((TimeSpan.FromMinutes(5), TimeSpan.FromHours(26), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1)), (options.BucketLength, options.Retention, options.MaxIntegrationGap, options.SaveInterval));
        Assert.Equal((1048576, 0.5, 720, 50), (options.MaxFileBytes, options.VoltageLossFraction, options.ExcursionMaxSamples, options.ExcursionMaxDecisions));
        Assert.Equal((TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2)), (options.ExcursionPreRoll, options.ExcursionPostRoll));
    }

    [Theory]
    [InlineData("BucketLength", "00:00:30", "BucketLength")]
    [InlineData("BucketLength", "00:04:00", "BucketLength")]
    [InlineData("BucketLength", "00:30:00", "BucketLength")]
    [InlineData("Retention", "1.00:00:00", "Retention")]
    [InlineData("Retention", "9.00:00:00", "Retention")]
    [InlineData("MaxIntegrationGap", "00:00:00", "MaxIntegrationGap")]
    [InlineData("MaxIntegrationGap", "00:15:00", "MaxIntegrationGap")]
    [InlineData("SaveInterval", "00:00:00", "SaveInterval")]
    [InlineData("MaxFileBytes", "1000", "MaxFileBytes")]
    [InlineData("MaxFileBytes", "8388608", "MaxFileBytes")]
    [InlineData("VoltageLossFraction", "0", "VoltageLossFraction")]
    [InlineData("VoltageLossFraction", "1", "VoltageLossFraction")]
    [InlineData("ExcursionPreRoll", "00:00:00", "ExcursionPreRoll")]
    [InlineData("ExcursionPostRoll", "01:00:00", "ExcursionPostRoll")]
    [InlineData("ExcursionMaxSamples", "5", "ExcursionMaxSamples")]
    [InlineData("ExcursionMaxDecisions", "0", "ExcursionMaxDecisions")]
    public void Statistics_parameters_are_validated(string key, string value, string fragment)
    {
        using var provider = Build(($"Switchboard:Statistics:{key}", value));

        var failure = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<StatisticsOptions>>().Value);
        Assert.Contains(failure.Failures, f => f.Contains(fragment, StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_statistics_parameter_is_rejected_rather_than_defaulted()
    {
        using var provider = Build(("Switchboard:Statistics:Retention", null));

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<StatisticsOptions>>().Value);
    }

    [Fact]
    public void Guard_presentation_and_mqtt_shutdown_fit_inside_the_container_stop_grace()
    {
        // The presentation's final save runs after the guard's stop and before MQTT's, bounded by the
        // same Persistence:ShutdownTimeout: guard + presentation + 2 × MQTT must fit s6's grace.
        var grace = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(RepoPaths.Combine("switchboard", "Dockerfile")), @"ENV S6_SERVICES_GRACETIME=(\d+)");
        Assert.True(grace.Success);
        using var settings = JsonDocument.Parse(File.ReadAllText(ShippedSettings));
        var switchboard = settings.RootElement.GetProperty("Switchboard");
        var mqtt = TimeSpan.Parse(switchboard.GetProperty("Mqtt").GetProperty("ShutdownTimeout").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var guard = TimeSpan.Parse(switchboard.GetProperty("Persistence").GetProperty("ShutdownTimeout").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(guard + guard + (mqtt * 2) <= TimeSpan.FromMilliseconds(int.Parse(grace.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));
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
}
