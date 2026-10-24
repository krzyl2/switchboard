using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Signals;

namespace Switchboard.Infrastructure.HomeAssistant;

/// <summary>The outcome of reading one entity's state.</summary>
public abstract record EntityStateRead
{
    public sealed record Found(EntityUpdate Update) : EntityStateRead;

    public sealed record NotFound : EntityStateRead;

    public sealed record Failed(string Reason) : EntityStateRead;
}

/// <summary>Reads one entity's current state, including <c>last_reported</c>, which the subscription never carries.</summary>
public interface IEntityStateReader
{
    Task<EntityStateRead> ReadAsync(EntityId entity, CancellationToken cancellationToken);
}

/// <summary>Used when no Home Assistant endpoint is configured: every read fails, nothing is sent anywhere.</summary>
public sealed class UnavailableEntityStateReader : IEntityStateReader
{
    public Task<EntityStateRead> ReadAsync(EntityId entity, CancellationToken cancellationToken) =>
        Task.FromResult<EntityStateRead>(new EntityStateRead.Failed("Home Assistant is not configured"));
}

/// <summary>
/// The freshness probe: <c>GET {api}/states/{entity_id}</c> and nothing else. It carries the
/// Home Assistant token, so it is built as a structural gate: no method takes a path, an HTTP method
/// or a body, every request is a GET built here from a validated <see cref="EntityId"/>, redirects
/// are not followed (configured on its own named HttpClient), the answer size is capped and the
/// timeout runs on the injected clock.
/// </summary>
public sealed class HomeAssistantStateReader : IEntityStateReader
{
    /// <summary>Name of the dedicated HttpClient; never shared with the Supervisor client used for MQTT credentials.</summary>
    public const string HttpClientName = "home-assistant-state";

    // A protocol sanity limit, not a tuning knob: one entity state is a few hundred bytes.
    private const int MaxResponseBytes = 256 * 1024;
    private const string StatesPath = "states/";

    private readonly HttpClient _http;
    private readonly Uri _restBase;
    private readonly string _token;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _time;
    private readonly ILogger<HomeAssistantStateReader> _logger;

    public HomeAssistantStateReader(
        HttpClient http,
        Uri restBase,
        string accessToken,
        TimeSpan timeout,
        TimeProvider timeProvider,
        ILogger<HomeAssistantStateReader> logger)
    {
        ArgumentNullException.ThrowIfNull(restBase);
        _http = http;
        _restBase = restBase;
        _token = accessToken;
        _timeout = timeout;
        _time = timeProvider;
        _logger = logger;
    }

    public async Task<EntityStateRead> ReadAsync(EntityId entity, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_restBase, StatesPath + entity.Value));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new EntityStateRead.NotFound();
            }

            if (!response.IsSuccessStatusCode)
            {
                return Failed(entity, $"HTTP {(int)response.StatusCode}");
            }

            var body = await ReadCappedAsync(response.Content, linked.Token).ConfigureAwait(false);
            return body is null ? Failed(entity, "the answer exceeds the size limit") : Parse(entity, body);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Failed(entity, $"no answer within {_timeout}");
        }
        catch (HttpRequestException ex)
        {
            return Failed(entity, ex.Message);
        }
    }

    private EntityStateRead Failed(EntityId entity, string reason)
    {
        // Debug only: a failed probe is retried at the next due time, and the signal's age tells the story.
        _logger.LogDebug("State read of {Entity} failed: {Reason}", entity, reason);
        return new EntityStateRead.Failed(reason);
    }

    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaxResponseBytes + 1];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            length += read;
        }

        return length > MaxResponseBytes ? null : buffer.AsSpan(0, length).ToArray();
    }

    private EntityStateRead Parse(EntityId entity, byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || Text(root, "entity_id") != entity.Value
                || Text(root, "state") is not { } state
                || Time(root, "last_changed") is not { } lastChanged)
            {
                return Failed(entity, "the answer is not the state of the requested entity");
            }

            var lastUpdated = Time(root, "last_updated") ?? lastChanged;
            var reported = Time(root, "last_reported") ?? lastUpdated;
            string? unit = null;
            if (root.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object
                && attributes.TryGetProperty("unit_of_measurement", out var unitElement) && unitElement.ValueKind == JsonValueKind.String)
            {
                unit = unitElement.GetString();
            }

            return new EntityStateRead.Found(new EntityUpdate(entity, state, unit, lastUpdated, reported));
        }
        catch (JsonException)
        {
            return Failed(entity, "the answer is not JSON");
        }
    }

    private static string? Text(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? Time(JsonElement root, string key) =>
        Text(root, key) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;
}
