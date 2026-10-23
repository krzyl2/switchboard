using Microsoft.Extensions.Options;

namespace Switchboard.Application.Options;

internal static class OptionsValidation
{
    public static void RequirePositive(List<string> failures, string key, TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
        {
            failures.Add($"{key} must be greater than zero.");
        }
    }

    public static void RequireDelayRange(List<string> failures, string section, TimeSpan min, TimeSpan max)
    {
        RequirePositive(failures, $"{section}:ReconnectMinDelay", min);
        RequirePositive(failures, $"{section}:ReconnectMaxDelay", max);
        if (min > max)
        {
            failures.Add($"{section}:ReconnectMinDelay must not exceed ReconnectMaxDelay.");
        }
    }

    public static ValidateOptionsResult Result(List<string> failures) =>
        failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
}
