using System.Collections.Immutable;

namespace Switchboard.Application.Configuration;

/// <summary>Where a node was read from: positions are reported to the user only for their own document.</summary>
public enum ConfigOrigin
{
    Document,
    Defaults,
}

/// <summary>
/// A parsed YAML node with its position (1-based line and column). The YAML library stays in
/// Infrastructure; validation here works on this small tree, so every error can name its line.
/// </summary>
public abstract record ConfigNode(int Line, int Column, ConfigOrigin Origin);

/// <param name="Quoted">A quoted scalar is always a string: <c>"null"</c> is text, not a null.</param>
public sealed record ConfigScalar(string Value, bool Quoted, int Line, int Column, ConfigOrigin Origin)
    : ConfigNode(Line, Column, Origin)
{
    public bool IsNull => !Quoted && Value is "" or "~" or "null" or "Null" or "NULL";
}

public sealed record ConfigEntry(string Key, int Line, int Column, ConfigNode Value);

/// <summary>A mapping in document order; keys are unique (the reader refuses duplicates).</summary>
public sealed record ConfigMapping(ImmutableArray<ConfigEntry> Entries, int Line, int Column, ConfigOrigin Origin)
    : ConfigNode(Line, Column, Origin)
{
    public ConfigEntry? Find(string key)
    {
        foreach (var entry in Entries)
        {
            if (string.Equals(entry.Key, key, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }
}

public sealed record ConfigSequence(ImmutableArray<ConfigNode> Items, int Line, int Column, ConfigOrigin Origin)
    : ConfigNode(Line, Column, Origin);
