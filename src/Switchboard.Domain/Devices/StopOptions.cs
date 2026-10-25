using System.Diagnostics.CodeAnalysis;

namespace Switchboard.Domain.Devices;

/// <summary>
/// One option of a select stop actuator as the select offers it (<c>"1"</c>, <c>"eco"</c>): 1 to
/// 255 characters of text without control characters. It is compared for equality and named in the
/// log, nothing else: a command never carries it (the command port resolves the option from the
/// configuration loaded at send time), so no entity can ever be addressed through it.
/// </summary>
public readonly record struct OptionText
{
    /// <summary>Schema bound (Home Assistant's own limit on a state), not a control parameter.</summary>
    public const int MaxLength = 255;

    private OptionText(string value) => Value = value;

    public string Value { get; }

    public static bool TryParse(string? value, [NotNullWhen(true)] out OptionText? option)
    {
        option = value is { Length: > 0 and <= MaxLength } && !value.Any(char.IsControl) ? new OptionText(value) : null;
        return option is not null;
    }

    public static OptionText Parse(string value) =>
        TryParse(value, out var option) ? option.Value : throw new FormatException($"'{value}' is not an option (1 to {MaxLength} characters of text).");

    public override string ToString() => Value;
}

/// <summary>
/// The two options of a select stop actuator (a modulating device stopped by choosing an option, not
/// by a switch): <see cref="StopOption"/> stops the device, <see cref="ResumeOption"/> lets it run
/// again. Declared by the user (<c>stop_option</c>, <c>resume_option</c>); nothing else is ever sent
/// to that entity, and any reported option other than the stop option reads as able to run.
/// </summary>
public sealed record StopOptions(OptionText StopOption, OptionText ResumeOption);
