using System.Net;
using System.Text;
using Switchboard.Infrastructure.Mqtt;
using Switchboard.Infrastructure.Runtime;
using Switchboard.Tests.Support;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// PKG-04: in the add-on the broker comes from the Supervisor, so the user configures nothing.
/// The password travels through here and must never end up in a log or an error message.
/// </summary>
public sealed class MqttCredentialSourceTests
{
    private const string SupervisorToken = "supervisor-token-secret";
    private const string Password = "mqtt-password-secret";

    private const string DocumentedResponse = $$$"""
        {"result":"ok","data":{"addon":"core_mosquitto","host":"core-mosquitto","port":1883,"ssl":false,
         "protocol":"3.1.1","username":"addons","password":"{{{Password}}}"}}
        """;

    private readonly LogCapture _logs = new();

    [Fact]
    public async Task Addon_mode_asks_the_supervisor_with_the_bearer_token_and_parses_the_documented_response()
    {
        var handler = new StubHandler(HttpStatusCode.OK, DocumentedResponse);
        var source = AddonSource(handler);

        var credentials = await source.GetAsync(CancellationToken.None);

        Assert.Equal(new Uri("http://supervisor/services/mqtt"), handler.Request!.RequestUri);
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal(SupervisorToken, handler.Request.Headers.Authorization.Parameter);
        Assert.Equal("core-mosquitto", credentials.Host);
        Assert.Equal(1883, credentials.Port);
        Assert.False(credentials.UseTls);
        Assert.Equal("addons", credentials.Username);
        Assert.Equal(Password, credentials.Password);
    }

    [Fact]
    public async Task Credentials_are_fetched_on_every_attempt()
    {
        // A re-provisioned broker add-on hands out new credentials; a cached copy would lock us out.
        var handler = new StubHandler(HttpStatusCode.OK, DocumentedResponse);
        var source = AddonSource(handler);

        await source.GetAsync(CancellationToken.None);
        await source.GetAsync(CancellationToken.None);

        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Ssl_flag_is_honoured()
    {
        var source = AddonSource(new StubHandler(HttpStatusCode.OK, DocumentedResponse.Replace("\"ssl\":false", "\"ssl\":true", StringComparison.Ordinal)));

        Assert.True((await source.GetAsync(CancellationToken.None)).UseTls);
    }

    [Fact]
    public async Task Password_and_token_never_reach_the_logs()
    {
        var source = AddonSource(new StubHandler(HttpStatusCode.OK, DocumentedResponse));

        await source.GetAsync(CancellationToken.None);

        Assert.NotEmpty(_logs.Entries);
        Assert.DoesNotContain(Password, _logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(SupervisorToken, _logs.AllText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"result":"error","message":"No MQTT service"}""")]
    [InlineData("""{"result":"ok","data":{"port":1883,"password":"mqtt-password-secret"}}""")]
    [InlineData("""{"result":"ok","data":{"host":"core-mosquitto","port":"x","password":"mqtt-password-secret"}}""")]
    [InlineData("""{"result":"ok","data":{"host":"core-mosquitto","password":"mqtt-password-secret" """)]
    public async Task Unusable_responses_fail_without_echoing_the_body(string body)
    {
        var source = AddonSource(new StubHandler(HttpStatusCode.OK, body));

        var failure = await Assert.ThrowsAsync<MqttCredentialsUnavailableException>(() => source.GetAsync(CancellationToken.None));

        Assert.DoesNotContain(Password, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Supervisor_http_error_fails_with_a_hint()
    {
        var source = AddonSource(new StubHandler(HttpStatusCode.BadRequest, "{}"));

        var failure = await Assert.ThrowsAsync<MqttCredentialsUnavailableException>(() => source.GetAsync(CancellationToken.None));

        Assert.Contains("400", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Standalone_mode_takes_the_broker_from_the_environment_without_calling_the_supervisor()
    {
        var handler = new StubHandler(HttpStatusCode.OK, DocumentedResponse);
        var source = new MqttCredentialSource(
            new HttpClient(handler),
            new RuntimeEnvironment { MqttHost = "mosquitto", MqttPort = "1884", MqttUsername = "user", MqttPassword = Password },
            MsOptions.Create(TestOptions.Mqtt()),
            _logs.For<MqttCredentialSource>());

        var credentials = await source.GetAsync(CancellationToken.None);

        Assert.Null(source.ConfigurationProblem);
        Assert.Equal(0, handler.Calls);
        Assert.Equal("mosquitto", credentials.Host);
        Assert.Equal(1884, credentials.Port);
        Assert.Equal("user", credentials.Username);
        Assert.Equal(Password, credentials.Password);
    }

    [Fact]
    public async Task Standalone_username_and_password_are_optional_and_port_defaults_to_1883()
    {
        var source = StandaloneSource(new RuntimeEnvironment { MqttHost = "mosquitto" });

        var credentials = await source.GetAsync(CancellationToken.None);

        Assert.Equal(1883, credentials.Port);
        Assert.Null(credentials.Username);
        Assert.Null(credentials.Password);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("mosquitto", "not-a-port")]
    [InlineData("mosquitto", "70000")]
    public void Standalone_without_a_usable_broker_reports_a_configuration_problem(string? host, string? port)
    {
        var source = StandaloneSource(new RuntimeEnvironment { MqttHost = host, MqttPort = port });

        Assert.NotNull(source.ConfigurationProblem);
    }

    private MqttCredentialSource AddonSource(StubHandler handler) => new(
        new HttpClient(handler),
        new RuntimeEnvironment { SupervisorToken = SupervisorToken },
        MsOptions.Create(TestOptions.Mqtt()),
        _logs.For<MqttCredentialSource>());

    private MqttCredentialSource StandaloneSource(RuntimeEnvironment environment) => new(
        new HttpClient(new StubHandler(HttpStatusCode.OK, "{}")),
        environment,
        MsOptions.Create(TestOptions.Mqtt()),
        _logs.For<MqttCredentialSource>());

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
