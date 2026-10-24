using Switchboard.Application.Options;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Infrastructure.Runtime;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Infrastructure;

/// <summary>Standalone users paste the address they open HA with; the client must find the WebSocket API from it.</summary>
public sealed class HomeAssistantUrlsTests
{
    [Theory]
    [InlineData("http://homeassistant:8123", "ws://homeassistant:8123/api/websocket")]
    [InlineData("http://homeassistant:8123/", "ws://homeassistant:8123/api/websocket")]
    [InlineData("https://ha.example.org", "wss://ha.example.org/api/websocket")]
    [InlineData("https://ha.example.org:8443/", "wss://ha.example.org:8443/api/websocket")]
    [InlineData("http://192.168.1.10:8123/proxy/ha", "ws://192.168.1.10:8123/proxy/ha/api/websocket")]
    [InlineData("ws://homeassistant:8123/api/websocket", "ws://homeassistant:8123/api/websocket")]
    [InlineData("wss://ha.example.org/custom/socket", "wss://ha.example.org/custom/socket")]
    public void Derives_the_websocket_endpoint(string input, string expected)
    {
        Assert.True(HomeAssistantUrls.TryGetWebSocketUri(input, out var uri));
        Assert.Equal(new Uri(expected), uri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("homeassistant:8123")]
    [InlineData("ftp://homeassistant")]
    [InlineData("not a url")]
    public void Rejects_what_is_not_an_http_or_websocket_url(string? input)
    {
        Assert.False(HomeAssistantUrls.TryGetWebSocketUri(input, out _));
    }

    [Theory]
    [InlineData("http://homeassistant:8123", "http://homeassistant:8123/api/")]
    [InlineData("https://ha.example.org:8443/", "https://ha.example.org:8443/api/")]
    [InlineData("http://192.168.1.10:8123/proxy/ha", "http://192.168.1.10:8123/proxy/ha/api/")]
    [InlineData("ws://homeassistant:8123/api/websocket", "http://homeassistant:8123/api/")]
    [InlineData("wss://ha.example.org/proxy/api/websocket/", "https://ha.example.org/proxy/api/")]
    public void Derives_the_rest_base_from_either_url_form(string input, string expected)
    {
        // The state read must reach the same Home Assistant as the socket, whichever form the user pasted.
        Assert.True(HomeAssistantUrls.TryGetRestBaseUri(input, out var uri));
        Assert.Equal(new Uri(expected), uri);
    }

    [Fact]
    public void Addon_mode_uses_the_supervisor_proxy_and_token()
    {
        var environment = new RuntimeEnvironment { SupervisorToken = "supervisor-token", HomeAssistantUrl = "http://elsewhere:8123", HomeAssistantToken = "other" };

        var resolution = HomeAssistantEndpointResolution.Resolve(environment, TestOptions.HomeAssistant());

        Assert.Equal(new Uri("ws://supervisor/core/websocket"), resolution.Endpoint!.WebSocketUri);
        Assert.Equal(new Uri("http://supervisor/core/api/"), resolution.Endpoint.RestBaseUri);
        Assert.Equal("supervisor-token", resolution.Endpoint.AccessToken);
    }

    [Fact]
    public void Standalone_mode_uses_the_environment()
    {
        var environment = new RuntimeEnvironment { HomeAssistantUrl = "http://homeassistant:8123", HomeAssistantToken = "long-lived" };

        var resolution = HomeAssistantEndpointResolution.Resolve(environment, TestOptions.HomeAssistant());

        Assert.Equal(new Uri("ws://homeassistant:8123/api/websocket"), resolution.Endpoint!.WebSocketUri);
        Assert.Equal(new Uri("http://homeassistant:8123/api/"), resolution.Endpoint.RestBaseUri);
        Assert.Equal("long-lived", resolution.Endpoint.AccessToken);
    }

    [Fact]
    public void Standalone_without_url_or_token_reports_the_problem_instead_of_crashing()
    {
        // The add-on must still serve its page and status so the user can see what is missing.
        var resolution = HomeAssistantEndpointResolution.Resolve(new RuntimeEnvironment(), TestOptions.HomeAssistant());

        Assert.Null(resolution.Endpoint);
        Assert.Contains(EnvironmentVariableNames.HomeAssistantUrl, resolution.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void The_endpoint_never_prints_its_token()
    {
        var endpoint = new HomeAssistantEndpoint(new Uri("ws://user:pw@homeassistant:8123/api/websocket"), "secret-token");

        Assert.DoesNotContain("secret-token", endpoint.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("pw", endpoint.DisplayUri, StringComparison.Ordinal);
    }
}

/// <summary>
/// The allow-list is the outbound boundary of the HA link. Its content is pinned by a test so that
/// widening it is always a deliberate, reviewed change, and so is the exact shape of each command.
/// </summary>
public sealed class HomeAssistantCommandAllowListTests
{
    [Fact]
    public void Allow_list_is_exactly_ping_and_subscribe_entities()
    {
        Assert.Equal(["ping", "subscribe_entities"], HomeAssistantCommandAllowList.Allowed.Order());
    }

    [Theory]
    [InlineData("call_service")]
    [InlineData("fire_event")]
    [InlineData("execute_script")]
    [InlineData("get_states")]
    [InlineData("unsubscribe_events")]
    [InlineData("unknown_type")]
    public void Commanding_types_are_refused(string type)
    {
        Assert.Throws<HomeAssistantCommandRefusedException>(() => HomeAssistantCommandAllowList.EnsureAllowed(type));
    }

    [Theory]
    [InlineData("""{"id":1,"type":"ping"}""")]
    [InlineData("""{"id":2,"type":"subscribe_entities","entity_ids":["sensor.a","sensor.b"]}""")]
    public void Allowed_shapes_pass(string payload)
    {
        HomeAssistantCommandAllowList.EnsurePayloadAllowed(System.Text.Encoding.UTF8.GetBytes(payload));
    }

    [Theory]
    [InlineData("""{"id":3,"type":"subscribe_entities","type":"call_service","entity_ids":["switch.x"]}""")]
    [InlineData("""{"id":3,"type":"ping","type":"call_service"}""")]
    [InlineData("""{"id":3,"type":"call_service","type":"ping"}""")]
    [InlineData("""{"id":3,"id":4,"type":"ping"}""")]
    [InlineData("""{"id":3,"type":"subscribe_entities"}""")]
    [InlineData("""{"id":3,"type":"subscribe_entities","entity_ids":[]}""")]
    [InlineData("""{"id":3,"type":"ping","domain":"switch","service":"turn_on"}""")]
    [InlineData("""{"id":3,"type":"subscribe_entities","entity_ids":["sensor.a"],"service_data":{}}""")]
    [InlineData("""{"id":3,"type":"subscribe_entities","entity_ids":[{"type":"call_service"}]}""")]
    [InlineData("""{"id":3,"type":"call_service","domain":"switch","service":"turn_on"}""")]
    [InlineData("""[{"id":3,"type":"ping"}]""")]
    [InlineData("""{"id":3,"type":"ping"} {"id":4,"type":"call_service"}""")]
    public void A_command_with_any_key_beyond_its_shape_is_refused(string payload)
    {
        // Home Assistant keeps the last duplicate key, so {"type":"subscribe_entities","type":"call_service"}
        // would run as call_service if only the first type were checked; a parser keeping the first
        // would run {"type":"call_service","type":"ping"}. Duplicates are refused outright, and the
        // shape check reads the bytes exactly as they would leave: nothing else may pass.
        Assert.Throws<HomeAssistantCommandRefusedException>(
            () => HomeAssistantCommandAllowList.EnsurePayloadAllowed(System.Text.Encoding.UTF8.GetBytes(payload)));
    }
}
