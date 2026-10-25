using Microsoft.Extensions.Options;

namespace Switchboard.Application.Options;

/// <summary>
/// Bound from <c>Switchboard:Statistics</c>: how the presentation integrates energy and records
/// excursions for the runtime UI, and how it keeps both in <c>/data</c>. Operational parameters,
/// not control parameters: nothing here changes a protection or dispatch decision.
/// </summary>
public sealed class StatisticsOptions
{
    public const string SectionName = "Switchboard:Statistics";

    /// <summary>Length of one energy bucket (UTC-aligned); it divides 15 minutes, so every real UTC offset stays aligned.</summary>
    public required TimeSpan BucketLength { get; init; }

    /// <summary>How long buckets and the worst excursion are kept: at least a 25-hour local day plus one bucket.</summary>
    public required TimeSpan Retention { get; init; }

    /// <summary>A step further than this from the previous one is counted as unobserved, never integrated.</summary>
    public required TimeSpan MaxIntegrationGap { get; init; }

    /// <summary>Without an excursion ending, statistics.json and excursions.json are written at most this often, and only when changed (statistics.json once a bucket closed or a counter moved).</summary>
    public required TimeSpan SaveInterval { get; init; }

    /// <summary>The largest file either of the two may be; the writer trims to fit and the reader refuses anything larger.</summary>
    public required int MaxFileBytes { get; init; }

    /// <summary>A fresh phase voltage below this fraction of the connection's nominal voltage counts as a voltage loss.</summary>
    public required double VoltageLossFraction { get; init; }

    /// <summary>Samples kept from before an excursion starts.</summary>
    public required TimeSpan ExcursionPreRoll { get; init; }

    /// <summary>An excursion ends after this long at or below the reserve threshold.</summary>
    public required TimeSpan ExcursionPostRoll { get; init; }

    /// <summary>Samples kept per excursion; beyond it they are thinned (minimum and maximum per time slot, the peak always kept).</summary>
    public required int ExcursionMaxSamples { get; init; }

    /// <summary>Decisions kept per excursion.</summary>
    public required int ExcursionMaxDecisions { get; init; }
}

public sealed class StatisticsOptionsValidator : IValidateOptions<StatisticsOptions>
{
    private static readonly TimeSpan MinBucket = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BucketAlignment = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LongestLocalDay = TimeSpan.FromHours(25);
    private static readonly TimeSpan MaxRetention = TimeSpan.FromDays(8);
    private static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxRoll = TimeSpan.FromMinutes(30);
    private const int MinFileBytes = 64 * 1024;
    private const int MaxFileBytes = 4 * 1024 * 1024;
    private const int MinSamples = 10;
    private const int MaxSamples = 10_000;
    private const int MaxDecisions = 500;

    public ValidateOptionsResult Validate(string? name, StatisticsOptions options)
    {
        const string section = StatisticsOptions.SectionName;
        var failures = new List<string>();

        if (options.BucketLength < MinBucket || BucketAlignment.Ticks % options.BucketLength.Ticks != 0)
        {
            failures.Add($"{section}:BucketLength must be at least {MinBucket} and divide {BucketAlignment} (1, 3, 5 or 15 minutes, for example).");
        }

        if (options.Retention < LongestLocalDay + options.BucketLength || options.Retention > MaxRetention)
        {
            failures.Add($"{section}:Retention must be between 25 hours plus one bucket and {MaxRetention.TotalDays:0} days.");
        }

        if (options.MaxIntegrationGap <= TimeSpan.Zero || options.MaxIntegrationGap > MaxGap)
        {
            failures.Add($"{section}:MaxIntegrationGap must be greater than zero and at most {MaxGap}.");
        }

        OptionsValidation.RequirePositive(failures, $"{section}:SaveInterval", options.SaveInterval);
        if (options.MaxFileBytes < MinFileBytes || options.MaxFileBytes > MaxFileBytes)
        {
            failures.Add($"{section}:MaxFileBytes must be between {MinFileBytes} and {MaxFileBytes}.");
        }

        if (!(options.VoltageLossFraction > 0 && options.VoltageLossFraction < 1))
        {
            failures.Add($"{section}:VoltageLossFraction must be between 0 and 1 (exclusive).");
        }

        RequireRoll(failures, $"{section}:ExcursionPreRoll", options.ExcursionPreRoll);
        RequireRoll(failures, $"{section}:ExcursionPostRoll", options.ExcursionPostRoll);
        if (options.ExcursionMaxSamples < MinSamples || options.ExcursionMaxSamples > MaxSamples)
        {
            failures.Add($"{section}:ExcursionMaxSamples must be between {MinSamples} and {MaxSamples}.");
        }

        if (options.ExcursionMaxDecisions < 1 || options.ExcursionMaxDecisions > MaxDecisions)
        {
            failures.Add($"{section}:ExcursionMaxDecisions must be between 1 and {MaxDecisions}.");
        }

        return OptionsValidation.Result(failures);
    }

    private static void RequireRoll(List<string> failures, string key, TimeSpan value)
    {
        if (value <= TimeSpan.Zero || value > MaxRoll)
        {
            failures.Add($"{key} must be greater than zero and at most {MaxRoll}.");
        }
    }
}
