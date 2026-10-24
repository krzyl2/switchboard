using Microsoft.Extensions.Options;

namespace Switchboard.Application.Options;

/// <summary>Bound from <c>Switchboard:Guard</c>: operational timing around the guard, never a control parameter.</summary>
public sealed class GuardOptions
{
    public const string SectionName = "Switchboard:Guard";

    /// <summary>
    /// How long a request that changes the guard's input (POST api/guard/kill-switch, POST
    /// api/devices/{id}/override) waits for the evaluation of it before answering without it. The
    /// change itself is in force at once.
    /// </summary>
    public required TimeSpan ApiEvaluationTimeout { get; init; }

    /// <summary>Verifications and observations kept per device for the API (UI-12 history); memory only, never persisted.</summary>
    public required int DeviceHistoryLength { get; init; }
}

public sealed class GuardOptionsValidator : IValidateOptions<GuardOptions>
{
    // A bound on memory per device, not a control parameter.
    private const int MaxDeviceHistoryLength = 1000;

    public ValidateOptionsResult Validate(string? name, GuardOptions options)
    {
        var failures = new List<string>();
        OptionsValidation.RequirePositive(failures, $"{GuardOptions.SectionName}:ApiEvaluationTimeout", options.ApiEvaluationTimeout);
        if (options.DeviceHistoryLength is < 1 or > MaxDeviceHistoryLength)
        {
            failures.Add($"{GuardOptions.SectionName}:DeviceHistoryLength must be between 1 and {MaxDeviceHistoryLength}.");
        }
        return OptionsValidation.Result(failures);
    }
}
