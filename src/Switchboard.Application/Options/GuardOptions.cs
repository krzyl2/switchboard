using Microsoft.Extensions.Options;

namespace Switchboard.Application.Options;

/// <summary>Bound from <c>Switchboard:Guard</c>: operational timing around the guard, never a control parameter.</summary>
public sealed class GuardOptions
{
    public const string SectionName = "Switchboard:Guard";

    /// <summary>
    /// How long a request that changes the guard's input (POST api/guard/kill-switch) waits for the
    /// guard's evaluation of it before answering without it. The change itself is in force at once.
    /// </summary>
    public required TimeSpan ApiEvaluationTimeout { get; init; }
}

public sealed class GuardOptionsValidator : IValidateOptions<GuardOptions>
{
    public ValidateOptionsResult Validate(string? name, GuardOptions options)
    {
        var failures = new List<string>();
        OptionsValidation.RequirePositive(failures, $"{GuardOptions.SectionName}:ApiEvaluationTimeout", options.ApiEvaluationTimeout);
        return OptionsValidation.Result(failures);
    }
}
