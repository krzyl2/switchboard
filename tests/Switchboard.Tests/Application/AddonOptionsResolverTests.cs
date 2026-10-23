using Switchboard.Application.Modes;

namespace Switchboard.Tests.Application;

/// <summary>
/// Mode resolution is the first safety gate: a mistaken "active" switches real loads, a mistaken
/// "shadow" only logs. Every doubtful input must therefore land in shadow.
/// </summary>
public sealed class AddonOptionsResolverTests
{
    [Fact]
    public void Exact_active_is_the_only_value_that_enables_active_mode()
    {
        var resolved = AddonOptionsResolver.Resolve("""{"mode":"active","log_level":"info"}""", null, null);

        Assert.Equal(OperatingMode.Active, resolved.Mode);
    }

    [Theory]
    [InlineData("shadow")]
    [InlineData("")]
    [InlineData("Active")]
    [InlineData("ACTIVE")]
    [InlineData(" active")]
    [InlineData("active ")]
    [InlineData("on")]
    [InlineData("true")]
    public void Any_other_mode_string_in_the_file_falls_back_to_shadow(string mode)
    {
        var json = $$"""{"mode":"{{mode}}"}""";

        Assert.Equal(OperatingMode.Shadow, AddonOptionsResolver.Resolve(json, null, null).Mode);
    }

    [Theory]
    [InlineData("""{"mode":null}""")]
    [InlineData("""{"mode":true}""")]
    [InlineData("""{"mode":1}""")]
    [InlineData("""{"mode":["active"]}""")]
    [InlineData("""{"log_level":"info"}""")]
    [InlineData("""["active"]""")]
    [InlineData("""{"mode":"active""")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void Missing_mistyped_or_corrupt_options_fall_back_to_shadow(string json)
    {
        Assert.Equal(OperatingMode.Shadow, AddonOptionsResolver.Resolve(json, null, null).Mode);
    }

    [Fact]
    public void Missing_file_falls_back_to_shadow()
    {
        Assert.Equal(OperatingMode.Shadow, AddonOptionsResolver.Resolve(null, null, null).Mode);
    }

    [Fact]
    public void Environment_override_wins_over_the_file_in_both_directions()
    {
        // Standalone users configure through the environment; it must win so they can force shadow.
        Assert.Equal(OperatingMode.Shadow, AddonOptionsResolver.Resolve("""{"mode":"active"}""", "shadow", null).Mode);
        Assert.Equal(OperatingMode.Active, AddonOptionsResolver.Resolve("""{"mode":"shadow"}""", "active", null).Mode);
    }

    [Fact]
    public void An_empty_environment_override_still_counts_as_set_and_resolves_to_shadow()
    {
        Assert.Equal(OperatingMode.Shadow, AddonOptionsResolver.Resolve("""{"mode":"active"}""", string.Empty, null).Mode);
    }

    [Fact]
    public void Environment_override_is_held_to_the_same_exact_match_rule()
    {
        Assert.Equal(OperatingMode.Shadow, AddonOptionsResolver.Resolve(null, "Active", null).Mode);
    }

    [Theory]
    [InlineData("debug", AddonLogLevel.Debug)]
    [InlineData("info", AddonLogLevel.Info)]
    [InlineData("warning", AddonLogLevel.Warning)]
    [InlineData("verbose", AddonLogLevel.Info)]
    [InlineData("", AddonLogLevel.Info)]
    public void Log_level_maps_the_schema_values_and_defaults_to_info(string level, AddonLogLevel expected)
    {
        var json = $$"""{"mode":"shadow","log_level":"{{level}}"}""";

        Assert.Equal(expected, AddonOptionsResolver.Resolve(json, null, null).LogLevel);
    }

    [Fact]
    public void Log_level_environment_override_wins_over_the_file()
    {
        Assert.Equal(AddonLogLevel.Debug, AddonOptionsResolver.Resolve("""{"log_level":"warning"}""", null, "debug").LogLevel);
    }

    [Fact]
    public void An_unrecognised_mode_is_reported_so_the_user_learns_why_nothing_switches()
    {
        var resolved = AddonOptionsResolver.Resolve("""{"mode":"Active"}""", null, null);

        Assert.Contains(resolved.Warnings, w => w.Contains("shadow", StringComparison.Ordinal));
    }

    [Fact]
    public void A_plain_shadow_setting_produces_no_warning()
    {
        Assert.Empty(AddonOptionsResolver.Resolve("""{"mode":"shadow","log_level":"info"}""", null, null).Warnings);
    }
}
