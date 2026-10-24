using System.Globalization;
using System.Text.RegularExpressions;

namespace Switchboard.Application.Configuration;

/// <summary>Collects every issue of one read, so the user sees all of them, not only the first.</summary>
internal sealed class ConfigIssues
{
    public List<ConfigurationIssue> Errors { get; } = [];

    public List<ConfigurationIssue> Warnings { get; } = [];

    public bool HasErrors => Errors.Count > 0;

    public void Error(string code, string path, ConfigNode? at, string message) => Errors.Add(Create(code, path, at, message));

    public void ErrorAtKey(string code, string path, ConfigEntry entry, string message) =>
        Errors.Add(CreateAt(code, path, entry.Value.Origin, entry.Line, entry.Column, message));

    public void Warning(string code, string path, ConfigNode? at, string message) => Warnings.Add(Create(code, path, at, message));

    public static ConfigurationIssue Create(string code, string path, ConfigNode? at, string message) =>
        at is null ? new ConfigurationIssue(code, path, null, null, message) : CreateAt(code, path, at.Origin, at.Line, at.Column, message);

    private static ConfigurationIssue CreateAt(string code, string path, ConfigOrigin origin, int line, int column, string message) =>
        origin == ConfigOrigin.Document
            ? new ConfigurationIssue(code, path, line, column, message)
            : new ConfigurationIssue(code, path, null, null, message);
}

/// <summary>Typed readers over <see cref="ConfigNode"/>; each reports a precise issue and returns null on failure.</summary>
internal static partial class ConfigRead
{
    public static string Join(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";

    public static bool IsNull(ConfigNode? node) => node is null || node is ConfigScalar { IsNull: true };

    public static ConfigMapping? Mapping(ConfigNode? node, string path, ConfigIssues issues)
    {
        if (node is ConfigMapping mapping)
        {
            return mapping;
        }

        if (!IsNull(node))
        {
            issues.Error(ConfigurationIssueCodes.WrongType, path, node, "Expected a mapping of keys to values.");
        }

        return null;
    }

    /// <summary>A misspelt key is an error, never silently ignored: a typo in a limit must not fall back to a default.</summary>
    public static void CheckKeys(ConfigMapping mapping, string path, IReadOnlyCollection<string> allowed, ConfigIssues issues)
    {
        foreach (var entry in mapping.Entries)
        {
            if (!allowed.Contains(entry.Key))
            {
                issues.ErrorAtKey(
                    ConfigurationIssueCodes.UnknownKey,
                    Join(path, entry.Key),
                    entry,
                    $"Unknown key '{entry.Key}'. Allowed here: {string.Join(", ", allowed)}.");
            }
        }
    }

    /// <summary>The value of a key, or null when the key is absent or its value is null.</summary>
    public static ConfigNode? Optional(ConfigMapping mapping, string key) =>
        mapping.Find(key) is { } entry && !IsNull(entry.Value) ? entry.Value : null;

    public static ConfigNode? Required(ConfigMapping mapping, string key, string path, ConfigIssues issues)
    {
        var value = Optional(mapping, key);
        if (value is null)
        {
            issues.Error(ConfigurationIssueCodes.MissingKey, Join(path, key), (ConfigNode?)mapping.Find(key)?.Value ?? mapping, $"'{key}' is required.");
        }

        return value;
    }

    public static string? String(ConfigNode node, string path, ConfigIssues issues)
    {
        if (node is ConfigScalar scalar && !scalar.IsNull)
        {
            return scalar.Value;
        }

        issues.Error(ConfigurationIssueCodes.WrongType, path, node, "Expected a text value.");
        return null;
    }

    public static double? Number(ConfigNode node, string path, ConfigIssues issues)
    {
        if (node is ConfigScalar { Quoted: false } scalar
            && double.TryParse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value))
        {
            return value;
        }

        issues.Error(ConfigurationIssueCodes.WrongType, path, node, "Expected a number.");
        return null;
    }

    public static double? Number(ConfigNode node, string path, ConfigIssues issues, Range range)
    {
        if (Number(node, path, issues) is not { } value)
        {
            return null;
        }

        if (!range.Contains(value))
        {
            issues.Error(ConfigurationIssueCodes.OutOfRange, path, node, $"{value.ToString(CultureInfo.InvariantCulture)} is out of range; expected {range}.");
            return null;
        }

        return value;
    }

    public static int? Integer(ConfigNode node, string path, ConfigIssues issues, int min, int max)
    {
        if (node is not ConfigScalar { Quoted: false } scalar
            || !int.TryParse(scalar.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            issues.Error(ConfigurationIssueCodes.WrongType, path, node, "Expected a whole number.");
            return null;
        }

        if (value < min || value > max)
        {
            issues.Error(ConfigurationIssueCodes.OutOfRange, path, node, $"{value} is out of range; expected {min} to {max}.");
            return null;
        }

        return value;
    }

    public static bool? Boolean(ConfigNode node, string path, ConfigIssues issues)
    {
        if (node is ConfigScalar { Quoted: false } scalar)
        {
            switch (scalar.Value)
            {
                case "true" or "True" or "TRUE":
                    return true;
                case "false" or "False" or "FALSE":
                    return false;
            }
        }

        issues.Error(ConfigurationIssueCodes.WrongType, path, node, "Expected true or false.");
        return null;
    }

    public static T? Choice<T>(ConfigNode node, string path, ConfigIssues issues, IReadOnlyDictionary<string, T> choices)
        where T : struct
    {
        if (String(node, path, issues) is not { } text)
        {
            return null;
        }

        if (choices.TryGetValue(text, out var value))
        {
            return value;
        }

        issues.Error(ConfigurationIssueCodes.InvalidValue, path, node, $"'{text}' is not one of: {string.Join(", ", choices.Keys)}.");
        return null;
    }

    /// <summary>
    /// <c>500ms</c>, <c>90s</c>, <c>6m</c>, <c>1h</c>; <c>unlimited</c> (null) where allowed. Whole
    /// numbers with one unit only, so a value is never ambiguous.
    /// </summary>
    public static bool TryDuration(ConfigNode node, string path, ConfigIssues issues, bool allowUnlimited, out TimeSpan? duration)
    {
        duration = null;
        if (node is ConfigScalar { Quoted: false } scalar)
        {
            if (allowUnlimited && scalar.Value == "unlimited")
            {
                return true;
            }

            var match = DurationPattern().Match(scalar.Value);
            if (match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
            {
                try
                {
                    duration = match.Groups[2].Value switch
                    {
                        "ms" => TimeSpan.FromMilliseconds(amount),
                        "s" => TimeSpan.FromSeconds(amount),
                        "m" => TimeSpan.FromMinutes(amount),
                        _ => TimeSpan.FromHours(amount),
                    };
                    return true;
                }
                catch (OverflowException)
                {
                    // Reported below like any other unusable value.
                }
            }
        }

        issues.Error(
            ConfigurationIssueCodes.InvalidDuration,
            path,
            node,
            allowUnlimited
                ? "Expected a duration such as 500ms, 90s, 6m or 1h, or 'unlimited'."
                : "Expected a duration such as 500ms, 90s, 6m or 1h.");
        return false;
    }

    public static TimeSpan? PositiveDuration(ConfigNode node, string path, ConfigIssues issues)
    {
        if (!TryDuration(node, path, issues, allowUnlimited: false, out var duration) || duration is not { } value)
        {
            return null;
        }

        if (value <= TimeSpan.Zero)
        {
            issues.Error(ConfigurationIssueCodes.OutOfRange, path, node, "Must be longer than zero.");
            return null;
        }

        return value;
    }

    public static TimeSpan? NonNegativeDuration(ConfigNode node, string path, ConfigIssues issues) =>
        TryDuration(node, path, issues, allowUnlimited: false, out var duration) ? duration : null;

    [GeneratedRegex("^([0-9]{1,9})(ms|s|m|h)$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();

    /// <summary>A numeric interval with open or closed ends, printed in messages.</summary>
    public readonly record struct Range(double Min, double Max, bool MinExclusive = false, bool MaxExclusive = false)
    {
        public static Range AtLeast(double min) => new(min, double.PositiveInfinity);

        public static Range Above(double min) => new(min, double.PositiveInfinity, MinExclusive: true);

        public static Range Fraction => new(0, 1);

        public bool Contains(double value) =>
            (MinExclusive ? value > Min : value >= Min) && (MaxExclusive ? value < Max : value <= Max);

        public override string ToString()
        {
            var min = Min.ToString(CultureInfo.InvariantCulture);
            if (double.IsPositiveInfinity(Max))
            {
                return MinExclusive ? $"more than {min}" : $"at least {min}";
            }

            var max = Max.ToString(CultureInfo.InvariantCulture);
            return $"{(MinExclusive ? "more than " : string.Empty)}{min} to {(MaxExclusive ? "less than " : string.Empty)}{max}";
        }
    }
}
