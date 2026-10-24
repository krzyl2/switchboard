using Microsoft.Extensions.Options;

namespace Switchboard.Application.Options;

/// <summary>
/// Bound from <c>Switchboard:Configuration</c>: how the configuration document is watched. Its
/// path is environment (RuntimeEnvironment.ConfigPath), not a setting.
/// </summary>
public sealed class ConfigurationOptions
{
    public const string SectionName = "Switchboard:Configuration";

    /// <summary>Changes are applied once the file has been quiet this long, so an editor's save is one reload.</summary>
    public required TimeSpan ReloadDebounce { get; init; }

    /// <summary>
    /// The file is checked for changes this often. File-system events are only an accelerator: they
    /// do not cross some container bind mounts and are lost on overflow.
    /// </summary>
    public required TimeSpan PollInterval { get; init; }

    /// <summary>A larger file is refused unread: the document is untrusted input.</summary>
    public required int MaxFileBytes { get; init; }
}

public sealed class ConfigurationOptionsValidator : IValidateOptions<ConfigurationOptions>
{
    private const int MinFileBytes = 1024;
    private const int MaxFileBytes = 1024 * 1024;

    public ValidateOptionsResult Validate(string? name, ConfigurationOptions options)
    {
        const string section = ConfigurationOptions.SectionName;
        var failures = new List<string>();

        OptionsValidation.RequirePositive(failures, $"{section}:ReloadDebounce", options.ReloadDebounce);
        OptionsValidation.RequirePositive(failures, $"{section}:PollInterval", options.PollInterval);
        if (options.MaxFileBytes < MinFileBytes || options.MaxFileBytes > MaxFileBytes)
        {
            failures.Add($"{section}:MaxFileBytes must be between {MinFileBytes} and {MaxFileBytes}.");
        }

        return OptionsValidation.Result(failures);
    }
}
