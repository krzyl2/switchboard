using Switchboard.Application.Configuration;
using Switchboard.Application.Setup;

namespace Switchboard.Infrastructure.Configuration;

/// <summary>The setup wizard's rules compiled into the assembly (setup-defaults.yaml); not overridable.</summary>
public static class EmbeddedSetupDefaults
{
    public const string ResourceName = "Switchboard.Infrastructure.Configuration.setup-defaults.yaml";

    private static readonly Lazy<string> Text = new(ReadText);

    public static string Yaml => Text.Value;

    /// <summary>The bound rules. A broken embedded file is a build defect, so it throws (a packaging test binds it).</summary>
    public static SetupDefaults Load()
    {
        var result = YamlDocumentReader.Read(Yaml, ConfigOrigin.Defaults);
        if (result.Root is null || !result.Errors.IsEmpty)
        {
            throw new InvalidOperationException($"The embedded setup defaults are not valid YAML: {string.Join("; ", result.Errors)}");
        }

        return SetupDefaultsBinder.Bind(result.Root);
    }

    private static string ReadText()
    {
        using var stream = typeof(EmbeddedSetupDefaults).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
