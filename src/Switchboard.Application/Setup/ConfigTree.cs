using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Switchboard.Application.Configuration;

namespace Switchboard.Application.Setup;

/// <summary>
/// Small edits of a parsed configuration tree for the wizard: a key replaced keeps its position, a
/// new key is appended, a scalar whose value did not change keeps its original spelling (7.0 stays
/// 7.0). New nodes carry no position; the written file is parsed again before anything is saved.
/// </summary>
internal static class ConfigTree
{
    public static ConfigMapping EmptyMapping() => new([], 0, 0, ConfigOrigin.Document);

    public static ConfigSequence Sequence(IEnumerable<ConfigNode> items) => new([.. items], 0, 0, ConfigOrigin.Document);

    /// <summary>A plain scalar: numbers, booleans, durations and closed-list words.</summary>
    public static ConfigScalar Plain(string text) => new(text, false, 0, 0, ConfigOrigin.Document);

    /// <summary>Free text (a display name, an entity ID): quoted whenever its plain form could read as anything but this text.</summary>
    public static ConfigScalar Text(string text) => new(text, NeedsQuotes(text), 0, 0, ConfigOrigin.Document);

    public static ConfigScalar Number(double value) => Plain(value.ToString("R", CultureInfo.InvariantCulture));

    public static ConfigScalar Boolean(bool value) => Plain(value ? "true" : "false");

    public static ConfigNode? Get(this ConfigMapping mapping, string key) => mapping.Find(key)?.Value;

    public static ConfigMapping? GetMapping(this ConfigMapping mapping, string key) => mapping.Find(key)?.Value as ConfigMapping;

    /// <summary>Sets a key: an existing entry keeps its place in the document, a new one goes last.</summary>
    public static ConfigMapping Set(this ConfigMapping mapping, string key, ConfigNode value)
    {
        var entries = mapping.Entries;
        for (var i = 0; i < entries.Length; i++)
        {
            if (string.Equals(entries[i].Key, key, StringComparison.Ordinal))
            {
                return mapping with { Entries = entries.SetItem(i, entries[i] with { Value = value }) };
            }
        }

        return mapping with { Entries = entries.Add(new ConfigEntry(key, 0, 0, value)) };
    }

    public static ConfigMapping Remove(this ConfigMapping mapping, string key) =>
        mapping with { Entries = [.. mapping.Entries.Where(e => !string.Equals(e.Key, key, StringComparison.Ordinal))] };

    /// <summary>Sets a number, keeping the existing scalar when it already holds this value.</summary>
    public static ConfigMapping SetNumber(this ConfigMapping mapping, string key, double? value) =>
        value is not { } number ? mapping.Remove(key)
        : AsNumber(mapping.Get(key)) == number ? mapping
        : mapping.Set(key, Number(number));

    /// <summary>Sets free text, keeping the existing scalar when it already holds this text.</summary>
    public static ConfigMapping SetText(this ConfigMapping mapping, string key, string? value) =>
        value is null ? mapping.Remove(key)
        : AsText(mapping.Get(key)) == value ? mapping
        : mapping.Set(key, Text(value));

    /// <summary>Sets a plain word or duration, keeping the existing scalar when it already holds it.</summary>
    public static ConfigMapping SetPlain(this ConfigMapping mapping, string key, string? value) =>
        value is null ? mapping.Remove(key)
        : mapping.Get(key) is ConfigScalar { Quoted: false } existing && existing.Value == value ? mapping
        : mapping.Set(key, Plain(value));

    public static ConfigMapping SetBoolean(this ConfigMapping mapping, string key, bool? value) =>
        value is not { } flag ? mapping.Remove(key)
        : AsBoolean(mapping.Get(key)) == flag ? mapping
        : mapping.Set(key, Boolean(flag));

    public static string? AsText(ConfigNode? node) => node is ConfigScalar { IsNull: false } scalar ? scalar.Value : null;

    public static double? AsNumber(ConfigNode? node) =>
        node is ConfigScalar { Quoted: false, IsNull: false } scalar
        && double.TryParse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && double.IsFinite(value)
            ? value
            : null;

    public static bool? AsBoolean(ConfigNode? node) => node is ConfigScalar { Quoted: false } scalar
        ? scalar.Value switch
        {
            "true" or "True" or "TRUE" => true,
            "false" or "False" or "FALSE" => false,
            _ => null,
        }
        : null;

    public static bool NeedsQuotes(string text) => ConfigTreeText.NeedsQuotes(text);
}

/// <summary>How the wizard decides whether free text needs quotes in the written file.</summary>
public static partial class ConfigTreeText
{
    /// <summary>
    /// True when a plain scalar of this text would not read back as exactly this text, in this
    /// parser or another YAML reader: empty, null/boolean/number look-alikes (YAML 1.1 included:
    /// yes, no, on, off), a leading indicator, ": " or " #", surrounding blanks, control characters.
    /// </summary>
    public static bool NeedsQuotes(string text) =>
        text.Length == 0
        || text != text.Trim()
        || text.Any(char.IsControl)
        || Reserved().IsMatch(text)
        || NumberLike().IsMatch(text)
        || "-?:,[]{}#&*!|>'\"%@`".Contains(text[0], StringComparison.Ordinal)
        || text.Contains(": ", StringComparison.Ordinal)
        || text.Contains(" #", StringComparison.Ordinal)
        || text.EndsWith(':');

    [GeneratedRegex("^(?:~|null|true|false|yes|no|y|n|on|off)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Reserved();

    [GeneratedRegex(@"^[-+]?(?:\.?[0-9]|0x|0o|\.inf|\.nan)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberLike();
}

/// <summary>
/// Which draft field wrote which document path, so an issue of the runtime parser (which names a
/// document path) is reported at the field the user can fix (UI-05). A path that is not mapped
/// itself falls back to its nearest mapped parent.
/// </summary>
public sealed class FieldMap
{
    private readonly Dictionary<string, string> _fields = new(StringComparer.Ordinal);

    public void Add(string documentPath, string field) => _fields[documentPath] = field;

    public string? FieldFor(string documentPath)
    {
        var path = documentPath;
        while (path.Length > 0)
        {
            if (_fields.TryGetValue(path, out var field))
            {
                return field;
            }

            var cut = Math.Max(path.LastIndexOf('.'), path.LastIndexOf('['));
            if (cut <= 0)
            {
                return null;
            }

            path = path[..cut];
        }

        return null;
    }

    public ImmutableDictionary<string, string> All => _fields.ToImmutableDictionary(StringComparer.Ordinal);
}
