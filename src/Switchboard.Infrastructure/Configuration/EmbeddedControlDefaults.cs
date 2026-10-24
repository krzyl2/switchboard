using Switchboard.Application.Configuration;

namespace Switchboard.Infrastructure.Configuration;

/// <summary>The control-parameter defaults file compiled into the assembly (DECISIONS: never C# literals).</summary>
public static class EmbeddedControlDefaults
{
    public const string ResourceName = "Switchboard.Infrastructure.Configuration.control-defaults.yaml";

    private static readonly Lazy<string> Text = new(ReadText);

    public static string Yaml => Text.Value;

    /// <summary>
    /// The defaults as a tree. A broken embedded file is a build defect, so it throws rather than
    /// letting the add-on start without parameters (a packaging test reads it on every build).
    /// </summary>
    public static ConfigNode Load()
    {
        var result = YamlDocumentReader.Read(Yaml, ConfigOrigin.Defaults);
        if (result.Root is null || !result.Errors.IsEmpty)
        {
            throw new InvalidOperationException(
                $"The embedded control defaults are not valid YAML: {string.Join("; ", result.Errors)}");
        }

        return result.Root;
    }

    private static string ReadText()
    {
        using var stream = typeof(EmbeddedControlDefaults).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
