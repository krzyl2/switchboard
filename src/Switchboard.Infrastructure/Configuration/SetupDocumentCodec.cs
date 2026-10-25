using System.Collections.Immutable;
using Switchboard.Application.Configuration;
using Switchboard.Application.Setup;

namespace Switchboard.Infrastructure.Configuration;

/// <summary>
/// The wizard's YAML: reading a file into a tree, writing a tree, and parsing the exact bytes to be
/// written through the same reader the running add-on uses (<see cref="ConfigurationFileReader"/>).
/// </summary>
public sealed class SetupDocumentCodec(ConfigurationFileReader reader, ConfigNode defaults) : ISetupDocumentCodec
{
    public (ConfigNode? Root, ImmutableArray<ConfigurationIssue> Errors) ReadTree(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = YamlDocumentReader.Read(text.TrimStart('﻿'), ConfigOrigin.Document);
        return (result.Errors.IsEmpty ? result.Root : null, result.Errors);
    }

    public string Write(ConfigNode root) => YamlDocumentWriter.Write(root);

    public bool HasComments(string text) => YamlDocumentWriter.HasComments(text);

    public ConfigurationResult Parse(byte[] bytes, DateTimeOffset now) => reader.Parse(reader.Snapshot(bytes), now);

    public ControlParameters? Control(ConfigNode? root) => SwitchboardDocumentParser.BindControl(root, defaults);
}
