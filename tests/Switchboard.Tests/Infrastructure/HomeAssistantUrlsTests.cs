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

    [Fact]
    public void Addon_mode_uses_the_supervisor_proxy_and_token()
    {
        var environment = new RuntimeEnvironment { SupervisorToken = "supervisor-token", HomeAssistantUrl = "http://elsewhere:8123", HomeAssistantToken = "other" };

        var resolution = HomeAssistantEndpointResolution.Resolve(environment, TestOptions.HomeAssistant());

        Assert.Equal(new Uri("ws://supervisor/core/websocket"), resolution.Endpoint!.WebSocketUri);
        Assert.Equal("supervisor-token", resolution.Endpoint.AccessToken);
    }

    [Fact]
    public void Standalone_mode_uses_the_environment()
    {
        var environment = new RuntimeEnvironment { HomeAssistantUrl = "http://homeassistant:8123", HomeAssistantToken = "long-lived" };

        var resolution = HomeAssistantEndpointResolution.Resolve(environment, TestOptions.HomeAssistant());

        Assert.Equal(new Uri("ws://homeassistant:8123/api/websocket"), resolution.Endpoint!.WebSocketUri);
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
/// widening it is always a deliberate, reviewed change.
/// </summary>
public sealed class HomeAssistantCommandAllowListTests
{
    [Fact]
    public void Stage_0_allows_only_ping()
    {
        Assert.Equal(["ping"], HomeAssistantCommandAllowList.Allowed.Order());
    }

    [Theory]
    [InlineData("call_service")]
    [InlineData("fire_event")]
    [InlineData("execute_script")]
    [InlineData("unknown_type")]
    public void Commanding_types_are_refused(string type)
    {
        Assert.Throws<HomeAssistantCommandRefusedException>(() => HomeAssistantCommandAllowList.EnsureAllowed(type));
    }
}
