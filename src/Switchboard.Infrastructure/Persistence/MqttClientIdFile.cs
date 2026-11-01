using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Switchboard.Infrastructure.Persistence;

/// <summary>The part of the MQTT client id that belongs to this install: 12 lowercase hexadecimal digits.</summary>
public sealed record MqttInstallId(string Value);

/// <summary>
/// <c>/data/mqtt-client.json</c>: the per-install part of the MQTT client id, generated once (48 random bits) and kept, so a restart or an update connects as
/// the same client, while a second install on the same broker never takes over this one's session (a broker drops the older connection of a client id it
/// sees twice). Read as untrusted input (exact keys, exactly 12 lowercase hexadecimal digits); a corrupt file is set aside as
/// <c>mqtt-client.json.corrupt</c> and a new id is generated. Written atomically.
/// </summary>
public sealed partial class MqttClientIdFile : PresentationFile<MqttInstallId>
{
    public const string Name = "mqtt-client.json";
    private const int SchemaVersion = 1;
    private const int IdLength = 12;

    // A bound on untrusted content, far above the few dozen bytes the file holds.
    private const int MaxFileBytes = 1024;

    private readonly ILogger<MqttClientIdFile> _logger;

    public MqttClientIdFile(string dataDirectory, ILogger<MqttClientIdFile> logger)
        : base(dataDirectory, Name, MaxFileBytes, logger)
    {
        _logger = logger;
    }

    protected override string LostText => "a new MQTT client id is generated";

    /// <summary>The id kept in the file, else a new one written there; a new one that cannot be written lasts for this process only.</summary>
    public MqttInstallId LoadOrCreate()
    {
        if (Load().State is { } kept)
        {
            return kept;
        }

        var created = new MqttInstallId(RandomNumberGenerator.GetHexString(IdLength, lowercase: true));
        try
        {
            Save(created);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("{File} could not be written ({Reason}); the MQTT client id is new again at the next start", Name, ex.Message);
        }

        return created;
    }

    protected override byte[] Serialize(MqttInstallId state)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", SchemaVersion);
            writer.WriteString("install_id", state.Value);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    protected override MqttInstallId Parse(JsonElement root)
    {
        var keys = PresentationJson.Keys(root, "the document", "schema_version", "install_id");
        if (PresentationJson.Count(keys["schema_version"], "schema_version") != SchemaVersion)
        {
            throw new InvalidPresentationFileException($"schema_version is not {SchemaVersion}");
        }

        var id = keys["install_id"].ValueKind == JsonValueKind.String ? keys["install_id"].GetString() : null;
        return id is not null && InstallId().IsMatch(id)
            ? new MqttInstallId(id)
            : throw new InvalidPresentationFileException($"install_id is not {IdLength} lowercase hexadecimal digits");
    }

    /// <summary>Nothing to drop: the file is one record.</summary>
    protected override MqttInstallId? Trim(MqttInstallId state, double ratio) => null;

    // \z, not $: a trailing newline is not part of an id.
    [GeneratedRegex(@"\A[0-9a-f]{12}\z", RegexOptions.CultureInvariant)]
    private static partial Regex InstallId();
}
