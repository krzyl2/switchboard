using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Switchboard.Application.Options;
using Switchboard.Infrastructure.Runtime;

namespace Switchboard.Infrastructure.Mqtt;

/// <summary>
/// Add-on: asks the Supervisor (<c>services: mqtt:need</c>) for the broker credentials (PKG-04).
/// Standalone: takes them from SWITCHBOARD_MQTT_*. The response body and the password never reach
/// a log line or an exception message.
/// </summary>
public sealed class MqttCredentialSource : IMqttCredentialSource
{
    // The IANA-registered MQTT port; used only when standalone leaves SWITCHBOARD_MQTT_PORT unset.
    private const int DefaultMqttPort = 1883;

    private readonly HttpClient _http;
    private readonly RuntimeEnvironment _environment;
    private readonly MqttOptions _options;
    private readonly ILogger<MqttCredentialSource> _logger;
    private readonly MqttCredentials? _standalone;

    public MqttCredentialSource(
        HttpClient http,
        RuntimeEnvironment environment,
        IOptions<MqttOptions> options,
        ILogger<MqttCredentialSource> logger)
    {
        _http = http;
        _environment = environment;
        _options = options.Value;
        _logger = logger;
        if (!environment.IsAddon)
        {
            (_standalone, ConfigurationProblem) = ReadStandalone(environment);
        }
    }

    public string? ConfigurationProblem { get; }

    public async Task<MqttCredentials> GetAsync(CancellationToken cancellationToken)
    {
        if (!_environment.IsAddon)
        {
            return _standalone ?? throw new MqttCredentialsUnavailableException(ConfigurationProblem ?? "MQTT is not configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, _options.SupervisorServiceUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _environment.SupervisorToken);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new MqttCredentialsUnavailableException(
                $"Supervisor MQTT service returned HTTP {(int)response.StatusCode}; is the MQTT broker add-on installed?");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var credentials = ParseSupervisorResponse(body);
        _logger.LogInformation("MQTT broker provided by the Supervisor: {Broker}", credentials);
        return credentials;
    }

    /// <summary>Parses <c>{"result":"ok","data":{"host","port","ssl","username","password"}}</c>.</summary>
    internal static MqttCredentials ParseSupervisorResponse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("result", out var result) || !result.ValueEquals("ok")
                || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                throw new MqttCredentialsUnavailableException("Supervisor MQTT service did not return result 'ok'.");
            }

            var host = data.TryGetProperty("host", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
            if (string.IsNullOrWhiteSpace(host)
                || !data.TryGetProperty("port", out var p) || p.ValueKind != JsonValueKind.Number
                || !p.TryGetInt32(out var port) || port is <= 0 or > 65535)
            {
                throw new MqttCredentialsUnavailableException("Supervisor MQTT service response has no usable host and port.");
            }

            var ssl = data.TryGetProperty("ssl", out var s) && s.ValueKind == JsonValueKind.True;
            return new MqttCredentials(host, port, ssl, OptionalString(data, "username"), OptionalString(data, "password"));
        }
        catch (JsonException)
        {
            // The body carries the password: report the shape problem, never the content.
            throw new MqttCredentialsUnavailableException("Supervisor MQTT service response is not valid JSON.");
        }
    }

    private static (MqttCredentials? Credentials, string? Problem) ReadStandalone(RuntimeEnvironment environment)
    {
        if (string.IsNullOrWhiteSpace(environment.MqttHost))
        {
            return (null, $"Standalone mode needs {EnvironmentVariableNames.MqttHost}; MQTT stays disconnected and no health entity is published.");
        }

        var port = DefaultMqttPort;
        if (!string.IsNullOrWhiteSpace(environment.MqttPort)
            && (!int.TryParse(environment.MqttPort, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is <= 0 or > 65535))
        {
            return (null, $"{EnvironmentVariableNames.MqttPort} is not a valid port; MQTT stays disconnected.");
        }

        var username = string.IsNullOrEmpty(environment.MqttUsername) ? null : environment.MqttUsername;
        var password = string.IsNullOrEmpty(environment.MqttPassword) ? null : environment.MqttPassword;
        return (new MqttCredentials(environment.MqttHost.Trim(), port, useTls: false, username, password), null);
    }

    private static string? OptionalString(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
