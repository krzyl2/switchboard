using System.Collections.Immutable;

namespace Switchboard.Application.Configuration;

/// <summary>
/// Applies <c>control:</c> overrides to the shipped defaults: mappings merge key by key, sequences
/// and scalars are replaced whole. A key the defaults do not have is an error, so a misspelt
/// override can never be silently ignored.
/// </summary>
internal static class ConfigNodeMerge
{
    public static ConfigNode Merge(ConfigNode defaults, ConfigNode? overrides, string path, ConfigIssues issues)
    {
        if (ConfigRead.IsNull(overrides))
        {
            return defaults;
        }

        switch (defaults)
        {
            case ConfigMapping defaultMapping:
                if (overrides is not ConfigMapping overrideMapping)
                {
                    issues.Error(ConfigurationIssueCodes.WrongType, path, overrides, "Expected a mapping of keys to values.");
                    return defaults;
                }

                var entries = defaultMapping.Entries.ToBuilder();
                foreach (var entry in overrideMapping.Entries)
                {
                    var index = entries.FindIndex(e => string.Equals(e.Key, entry.Key, StringComparison.Ordinal));
                    var childPath = ConfigRead.Join(path, entry.Key);
                    if (index < 0)
                    {
                        issues.ErrorAtKey(
                            ConfigurationIssueCodes.UnknownKey,
                            childPath,
                            entry,
                            $"Unknown key '{entry.Key}'. Allowed here: {string.Join(", ", defaultMapping.Entries.Select(e => e.Key))}.");
                        continue;
                    }

                    entries[index] = entries[index] with { Value = Merge(entries[index].Value, entry.Value, childPath, issues) };
                }

                return defaultMapping with { Entries = entries.ToImmutable() };

            case ConfigSequence when overrides is ConfigSequence:
            case ConfigScalar when overrides is ConfigScalar:
                return overrides!;

            default:
                issues.Error(
                    ConfigurationIssueCodes.WrongType,
                    path,
                    overrides,
                    defaults is ConfigSequence ? "Expected a list." : "Expected a single value.");
                return defaults;
        }
    }

    private static int FindIndex(this ImmutableArray<ConfigEntry>.Builder entries, Func<ConfigEntry, bool> match)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            if (match(entries[i]))
            {
                return i;
            }
        }

        return -1;
    }
}
