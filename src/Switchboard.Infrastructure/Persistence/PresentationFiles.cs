using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Presentation;
using Switchboard.Application.State;

namespace Switchboard.Infrastructure.Persistence;

/// <summary>
/// What statistics.json and excursions.json share with state.json: read as untrusted input (exact
/// keys, exact types, values in range, at most MaxFileBytes before parsing), set aside as
/// <c>&lt;name&gt;.corrupt</c> with a warning when anything is wrong (the run then starts fresh), and
/// written atomically (a flushed temporary file moved over the old one, so a crash mid-write leaves
/// the previous file whole).
/// </summary>
public abstract class PresentationFile<T> : IPresentationStore<T>
    where T : class
{
    public const string CorruptSuffix = ".corrupt";
    public const string TemporarySuffix = ".tmp";

    private readonly string _directory;
    private readonly ILogger _logger;

    protected PresentationFile(string dataDirectory, string fileName, int maxBytes, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        _directory = dataDirectory;
        FileName = fileName;
        MaxBytes = maxBytes;
        _logger = logger;
    }

    public string FileName { get; }

    public string FilePath => Path.Combine(_directory, FileName);

    protected int MaxBytes { get; }

    public PresentationLoad<T> Load()
    {
        byte[] bytes;
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists)
            {
                return new PresentationLoad<T>(null, StateFileStatus.Fresh, null);
            }

            if (info.Length > MaxBytes)
            {
                return Reset($"it is larger than {MaxBytes} bytes");
            }

            bytes = File.ReadAllBytes(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var warning = $"{FileName} could not be read ({ex.Message}); {LostText}.";
            _logger.LogWarning("{Warning}", warning);
            return new PresentationLoad<T>(null, StateFileStatus.ResetCorrupt, warning);
        }

        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            return new PresentationLoad<T>(Parse(document.RootElement), StateFileStatus.Loaded, null);
        }
        catch (JsonException ex)
        {
            return Reset($"it is not valid JSON ({ex.Message})");
        }
        catch (InvalidPresentationFileException ex)
        {
            return Reset(ex.Message);
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentException or FormatException)
        {
            // A value the strict checks let through but the types cannot hold: still not usable,
            // and never a reason for the add-on not to start.
            return Reset($"a value is out of range ({ex.Message})");
        }
    }

    public void Save(T state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var payload = SerializeWithinCap(state);
        Directory.CreateDirectory(_directory);
        var temporary = FilePath + TemporarySuffix;
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
        }

        Move(temporary, FilePath);
    }

    /// <summary>Serialises, trimming the state until it fits under MaxBytes (the oldest history goes first), so the reader never refuses what the writer wrote.</summary>
    internal byte[] SerializeWithinCap(T state)
    {
        var payload = Serialize(state);
        var trimmed = false;
        var current = state;
        while (payload.Length > MaxBytes && Trim(current, (double)MaxBytes / payload.Length) is { } smaller)
        {
            current = smaller;
            payload = Serialize(current);
            trimmed = true;
        }

        if (payload.Length > MaxBytes)
        {
            throw new IOException($"{FileName} would be {payload.Length} bytes even trimmed, above the {MaxBytes} byte limit");
        }

        if (trimmed && !TrimReported)
        {
            // Once per process: the configuration (devices, retention) keeps producing too much.
            _logger.LogWarning("{File} exceeded {Limit} bytes; its oldest history was dropped to fit", FileName, MaxBytes);
            TrimReported = true;
        }

        return payload;
    }

    /// <summary>The atomic replace; overridable so a test can make it fail.</summary>
    protected virtual void Move(string temporary, string target) => File.Move(temporary, target, overwrite: true);

    protected abstract byte[] Serialize(T state);

    protected abstract T Parse(JsonElement root);

    /// <summary>
    /// A smaller version of the state (older history dropped, samples thinned) of roughly
    /// <paramref name="ratio"/> of its size, or null when nothing can be dropped.
    /// </summary>
    protected abstract T? Trim(T state, double ratio);

    /// <summary>What a set-aside file costs, for the warning: the runtime UI's history unless the file says otherwise.</summary>
    protected virtual string LostText => "the runtime UI started without its history";

    private bool TrimReported { get; set; }

    private PresentationLoad<T> Reset(string reason)
    {
        var warning = $"{FileName} was not usable ({reason}); it was kept as {FileName}{CorruptSuffix} and {LostText}.";
        try
        {
            File.Move(FilePath, FilePath + CorruptSuffix, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warning = $"{FileName} was not usable ({reason}) and could not be renamed ({ex.Message}); {LostText}.";
        }

        _logger.LogWarning("{Warning}", warning);
        return new PresentationLoad<T>(null, StateFileStatus.ResetCorrupt, warning);
    }
}

/// <summary>Strict JSON reading and writing shared by the presentation files.</summary>
internal static class PresentationJson
{
    public static void WriteTime(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is { } time)
        {
            writer.WriteString(name, Time(time));
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    /// <summary>UTC with a Z: unambiguous for a person reading the file, and read back by "O".</summary>
    public static string Time(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>The object's properties, which must be exactly <paramref name="expected"/>, each once.</summary>
    public static Dictionary<string, JsonElement> Keys(JsonElement element, string path, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidPresentationFileException($"{path} is not an object");
        }

        var found = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name, StringComparer.Ordinal) || !found.TryAdd(property.Name, property.Value))
            {
                throw new InvalidPresentationFileException($"{path} has an unexpected or repeated key '{property.Name}'");
            }
        }

        if (found.Count != expected.Length)
        {
            throw new InvalidPresentationFileException($"{path} lacks {string.Join(", ", expected.Where(k => !found.ContainsKey(k)))}");
        }

        return found;
    }

    public static List<JsonProperty> Object(JsonElement element, string path, int maxEntries)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidPresentationFileException($"{path} is not an object");
        }

        var properties = element.EnumerateObject().ToList();
        if (properties.Count > maxEntries || properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Count)
        {
            throw new InvalidPresentationFileException($"{path} has too many or repeated entries");
        }

        return properties;
    }

    public static List<JsonElement> Array(JsonElement element, string path, int maxEntries)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > maxEntries)
        {
            throw new InvalidPresentationFileException($"{path} is not a list of at most {maxEntries} entries");
        }

        return [.. element.EnumerateArray()];
    }

    public static double Number(JsonElement element, string path) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value) && double.IsFinite(value)
            ? value
            : throw new InvalidPresentationFileException($"{path} is not a finite number");

    public static double NonNegative(JsonElement element, string path) =>
        Number(element, path) is var value && value >= 0 ? value : throw new InvalidPresentationFileException($"{path} is negative");

    public static int Count(JsonElement element, string path) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value) && value >= 0
            ? value
            : throw new InvalidPresentationFileException($"{path} is not a non-negative integer");

    public static long LongCount(JsonElement element, string path) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value) && value >= 0
            ? value
            : throw new InvalidPresentationFileException($"{path} is not a non-negative integer");

    public static bool Bool(JsonElement element, string path) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new InvalidPresentationFileException($"{path} is not true or false"),
    };

    public static string Text(JsonElement element, string path, int maxLength) =>
        element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } text && text.Length <= maxLength
            ? text
            : throw new InvalidPresentationFileException($"{path} is not a text of 1 to {maxLength} characters");

    public static DateTimeOffset? Time(JsonElement element, string path, bool nullable = false)
    {
        if (element.ValueKind == JsonValueKind.Null && nullable)
        {
            return null;
        }

        return element.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParseExact(element.GetString(), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
                ? value
                : throw new InvalidPresentationFileException($"{path} is not an ISO 8601 round-trip time");
    }

    public static string Snake<TEnum>(TEnum value)
        where TEnum : struct, Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    public static TEnum Choice<TEnum>(JsonElement element, string path)
        where TEnum : struct, Enum
    {
        var text = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        return System.Enum.GetValues<TEnum>().Where(v => Snake(v) == text).Select(v => (TEnum?)v).SingleOrDefault()
            ?? throw new InvalidPresentationFileException($"{path} is not one of {string.Join(", ", System.Enum.GetValues<TEnum>().Select(Snake))}");
    }
}

internal sealed class InvalidPresentationFileException(string message) : Exception(message);
