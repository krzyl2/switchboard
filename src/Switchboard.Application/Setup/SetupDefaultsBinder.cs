using System.Collections.Immutable;
using System.Globalization;
using Switchboard.Application.Configuration;
using Switchboard.Domain.Connection;
using Range = Switchboard.Application.Configuration.ConfigRead.Range;

namespace Switchboard.Application.Setup;

/// <summary>
/// Binds the embedded setup-defaults.yaml. The file ships inside the build, so anything wrong with
/// it is a build defect: the binder throws, and a packaging test binds it on every build.
/// </summary>
public static class SetupDefaultsBinder
{
    private static readonly string[] RootKeys = ["derived_platforms", "validation", "self_test", "prefill", "templates"];
    private static readonly string[] ValidationKeys =
        ["plausible_current_max_multiple", "plausible_power_max_multiple", "plausible_voltage_band", "phase_sum_tolerance_fraction", "phase_sum_min_samples", "suggested_max_age_intervals", "max_age_above_refresh_intervals"];

    private static readonly string[] SelfTestKeys = ["step_min_fraction", "quiet_max_fraction", "rating_tolerance", "sigma_multiplier", "dominance", "min_concordant", "isolation", "watch_timeout"];
    // A switch on and a switch off: fewer agreeing observations can never cover both directions.
    private const int MinConcordant = 2;

    private static readonly string[] TemplateKeys = ["prefill", "hints"];
    private static readonly string[] HintKeys = ["draw_recommended"];
    private static readonly string[] DurationKeys = ["reaction_time", "min_on", "min_off", "state_max_age"];

    public static SetupDefaults Bind(ConfigNode root)
    {
        var issues = new ConfigIssues();
        var defaults = Bind(root, issues);
        if (issues.HasErrors || defaults is null)
        {
            throw new InvalidOperationException($"The embedded setup defaults are invalid: {string.Join("; ", issues.Errors)}");
        }

        return defaults;
    }

    private static SetupDefaults? Bind(ConfigNode root, ConfigIssues issues)
    {
        if (ConfigRead.Mapping(root, string.Empty, issues) is not { } mapping)
        {
            return null;
        }

        ConfigRead.CheckKeys(mapping, string.Empty, RootKeys, issues);
        var platforms = ImmutableArray.CreateBuilder<string>();
        if (ConfigRead.Required(mapping, "derived_platforms", string.Empty, issues) is ConfigSequence sequence)
        {
            for (var i = 0; i < sequence.Items.Length; i++)
            {
                if (ConfigRead.String(sequence.Items[i], $"derived_platforms[{i}]", issues) is { } platform)
                {
                    platforms.Add(platform);
                }
            }
        }

        var validation = Section(mapping, "validation", ValidationKeys, issues) is { } v
            ? new SetupValidationRules(
                Positive(v, "plausible_current_max_multiple", "validation", issues),
                Positive(v, "plausible_power_max_multiple", "validation", issues),
                Fraction(v, "plausible_voltage_band", "validation", issues),
                Fraction(v, "phase_sum_tolerance_fraction", "validation", issues),
                Count(v, "phase_sum_min_samples", "validation", issues),
                Count(v, "suggested_max_age_intervals", "validation", issues),
                Count(v, "max_age_above_refresh_intervals", "validation", issues))
            : null;
        if (validation is not null && validation.MaxAgeAboveRefreshIntervals <= validation.SuggestedMaxAgeIntervals)
        {
            issues.Error(ConfigurationIssueCodes.OutOfRange, "validation.max_age_above_refresh_intervals", mapping, "max_age_above_refresh_intervals must be above suggested_max_age_intervals.");
        }

        SelfTestRules? selfTest = null;
        if (Section(mapping, "self_test", SelfTestKeys, issues) is { } s)
        {
            var classifier = new PhaseStepParameters(
                Fraction(s, "step_min_fraction", "self_test", issues),
                Fraction(s, "quiet_max_fraction", "self_test", issues),
                Fraction(s, "rating_tolerance", "self_test", issues),
                Positive(s, "sigma_multiplier", "self_test", issues),
                Positive(s, "dominance", "self_test", issues),
                ConfigRead.Required(s, "min_concordant", "self_test", issues) is { } concordant
                    ? ConfigRead.Integer(concordant, "self_test.min_concordant", issues, MinConcordant, 1000) ?? 0
                    : 0);
            if (classifier.QuietMaxFraction >= classifier.StepMinFraction)
            {
                issues.Error(ConfigurationIssueCodes.OutOfRange, "self_test.quiet_max_fraction", s, "quiet_max_fraction must be below step_min_fraction.");
            }

            selfTest = new SelfTestRules(
                classifier,
                Duration(s, "isolation", "self_test", issues),
                Duration(s, "watch_timeout", "self_test", issues));
        }

        var prefill = Section(mapping, "prefill", ["voltage_v"], issues) is { } p ? Positive(p, "voltage_v", "prefill", issues) : 0;
        var templates = Templates(mapping, issues);
        return validation is null || selfTest is null ? null : new SetupDefaults(platforms.ToImmutable(), validation, selfTest, prefill, templates);
    }

    private static ImmutableArray<DeviceTemplate> Templates(ConfigMapping root, ConfigIssues issues)
    {
        var templates = ImmutableArray.CreateBuilder<DeviceTemplate>();
        if (ConfigRead.Required(root, "templates", string.Empty, issues) is not { } node || ConfigRead.Mapping(node, "templates", issues) is not { } mapping)
        {
            return templates.ToImmutable();
        }

        foreach (var entry in mapping.Entries)
        {
            var path = ConfigRead.Join("templates", entry.Key);
            if (ConfigRead.Mapping(entry.Value, path, issues) is not { } template)
            {
                continue;
            }

            ConfigRead.CheckKeys(template, path, TemplateKeys, issues);
            var values = ImmutableArray.CreateBuilder<TemplateValue>();
            Domain.Devices.DeviceType? type = null;
            if (ConfigRead.Required(template, "prefill", path, issues) is { } prefillNode
                && ConfigRead.Mapping(prefillNode, ConfigRead.Join(path, "prefill"), issues) is { } prefill)
            {
                foreach (var key in prefill.Entries)
                {
                    var keyPath = ConfigRead.Join(ConfigRead.Join(path, "prefill"), key.Key);
                    if (ConfigRead.String(key.Value, keyPath, issues) is not { } text)
                    {
                        continue;
                    }

                    if (key.Key == "type")
                    {
                        type = DeviceSectionParser.TypeOf(text);
                        if (type is null)
                        {
                            issues.Error(ConfigurationIssueCodes.InvalidValue, keyPath, key.Value, $"'{text}' is not a device type.");
                        }

                        continue;
                    }

                    var kind = DurationKeys.Contains(key.Key) ? TemplateValueKind.Duration
                        : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? TemplateValueKind.Number
                        : TemplateValueKind.Text;
                    if (kind == TemplateValueKind.Duration)
                    {
                        ConfigRead.NonNegativeDuration(key.Value, keyPath, issues);
                    }

                    values.Add(new TemplateValue(key.Key, text, kind));
                }
            }

            var draw = false;
            if (ConfigRead.Optional(template, "hints") is { } hintsNode && ConfigRead.Mapping(hintsNode, ConfigRead.Join(path, "hints"), issues) is { } hints)
            {
                ConfigRead.CheckKeys(hints, ConfigRead.Join(path, "hints"), HintKeys, issues);
                draw = ConfigRead.Optional(hints, "draw_recommended") is { } drawNode
                    && (ConfigRead.Boolean(drawNode, ConfigRead.Join(path, "hints.draw_recommended"), issues) ?? false);
            }

            if (type is { } deviceType)
            {
                templates.Add(new DeviceTemplate(entry.Key, deviceType, values.ToImmutable(), draw));
            }
            else
            {
                issues.Error(ConfigurationIssueCodes.MissingKey, ConfigRead.Join(path, "prefill.type"), template, "A template names its device type.");
            }
        }

        return templates.ToImmutable();
    }

    private static ConfigMapping? Section(ConfigMapping root, string key, string[] keys, ConfigIssues issues)
    {
        if (ConfigRead.Required(root, key, string.Empty, issues) is not { } node || ConfigRead.Mapping(node, key, issues) is not { } mapping)
        {
            return null;
        }

        ConfigRead.CheckKeys(mapping, key, keys, issues);
        return mapping;
    }

    private static double Positive(ConfigMapping mapping, string key, string path, ConfigIssues issues) =>
        ConfigRead.Required(mapping, key, path, issues) is { } node ? ConfigRead.Number(node, ConfigRead.Join(path, key), issues, Range.Above(0)) ?? 0 : 0;

    private static double Fraction(ConfigMapping mapping, string key, string path, ConfigIssues issues) =>
        ConfigRead.Required(mapping, key, path, issues) is { } node ? ConfigRead.Number(node, ConfigRead.Join(path, key), issues, new Range(0, 1, MinExclusive: true)) ?? 0 : 0;

    private static int Count(ConfigMapping mapping, string key, string path, ConfigIssues issues) =>
        ConfigRead.Required(mapping, key, path, issues) is { } node ? ConfigRead.Integer(node, ConfigRead.Join(path, key), issues, 1, 1000) ?? 0 : 0;

    private static TimeSpan Duration(ConfigMapping mapping, string key, string path, ConfigIssues issues) =>
        ConfigRead.Required(mapping, key, path, issues) is { } node ? ConfigRead.PositiveDuration(node, ConfigRead.Join(path, key), issues) ?? TimeSpan.Zero : TimeSpan.Zero;
}
