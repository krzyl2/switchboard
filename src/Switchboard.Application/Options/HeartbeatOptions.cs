using Microsoft.Extensions.Options;

namespace Switchboard.Application.Options;

/// <summary>Bound from <c>Switchboard:Heartbeat</c>.</summary>
public sealed class HeartbeatOptions
{
    public const string SectionName = "Switchboard:Heartbeat";

    public required TimeSpan Interval { get; init; }

    /// <summary>Sent to Home Assistant as expire_after; a silent add-on goes unavailable after this.</summary>
    public required TimeSpan ExpireAfter { get; init; }
}

public sealed class HeartbeatOptionsValidator : IValidateOptions<HeartbeatOptions>
{
    public ValidateOptionsResult Validate(string? name, HeartbeatOptions options)
    {
        const string section = HeartbeatOptions.SectionName;
        var failures = new List<string>();

        OptionsValidation.RequirePositive(failures, $"{section}:Interval", options.Interval);

        // With less than two intervals of slack a single late beat flips the entity to
        // unavailable, and the HA-side automation would shed loads for nothing.
        if (options.ExpireAfter <= options.Interval * 2)
        {
            failures.Add($"{section}:ExpireAfter must be longer than twice the Interval.");
        }

        // expire_after is an integer number of seconds in the discovery payload.
        if (options.ExpireAfter.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            failures.Add($"{section}:ExpireAfter must be a whole number of seconds.");
        }

        return OptionsValidation.Result(failures);
    }
}
