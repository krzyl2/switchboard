using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Switchboard.Application.Connectivity;

namespace Switchboard.Tests.Host;

/// <summary>
/// The add-on holds a Supervisor token, so its web surface is reachable only through HA's
/// authenticated ingress proxy (PKG-05). The status API feeds the page and must never leak a secret.
/// </summary>
public sealed class WebSurfaceTests
{
    private const string Supervisor = "172.30.32.2";

    [Theory]
    [InlineData(Supervisor)]
    [InlineData("::ffff:172.30.32.2")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task Ingress_proxy_and_loopback_are_allowed(string remote)
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);

        var context = await SendAsync(factory, "/api/status", remote);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.20")]
    [InlineData("172.30.32.3")]
    [InlineData("::ffff:10.0.0.5")]
    [InlineData("fe80::1")]
    public async Task Any_other_peer_is_refused(string remote)
    {
        using var root = new TempWebRoot("<html>spa</html>");
        using var factory = new SwitchboardHostFactory(root.Path);

        foreach (var path in new[] { "/api/status", "/", "/index.html" })
        {
            var context = await SendAsync(factory, path, remote);
            Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        }
    }

    [Fact]
    public async Task Forged_ingress_header_does_not_let_a_foreign_peer_in()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);

        var context = await SendAsync(factory, "/api/status", "10.0.0.5", ingressPath: "/api/hassio_ingress/abc");

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task Dev_trust_flag_lets_any_peer_in()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path, new Dictionary<string, string?> { ["SWITCHBOARD_DEV_TRUST_ALL_REQUESTS"] = "true" });

        var context = await SendAsync(factory, "/api/status", "10.0.0.5");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task Ingress_path_header_becomes_the_path_base()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);

        var context = await SendAsync(factory, "/api/status", Supervisor, ingressPath: "/api/hassio_ingress/Zx9_token");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("/api/hassio_ingress/Zx9_token", context.Request.PathBase.Value);
    }

    [Fact]
    public async Task Status_has_exactly_the_contract_shape()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);

        var context = await SendAsync(factory, "/api/status", Supervisor);
        using var json = JsonDocument.Parse(context.Response.Body);

        Assert.StartsWith("application/json", context.Response.ContentType, StringComparison.Ordinal);
        Assert.Equal(
            ["version", "mode", "homeAssistant", "mqtt", "healthy"],
            json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("0.1.0", json.RootElement.GetProperty("version").GetString());
        Assert.Equal("shadow", json.RootElement.GetProperty("mode").GetString());
        Assert.Equal("disconnected", json.RootElement.GetProperty("homeAssistant").GetString());
        Assert.Equal("disconnected", json.RootElement.GetProperty("mqtt").GetString());
        Assert.False(json.RootElement.GetProperty("healthy").GetBoolean());
    }

    [Fact]
    public async Task Status_reflects_live_link_states_and_health()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        _ = factory.Server;
        factory.Services.GetRequiredService<HomeAssistantConnectionState>().Set(ConnectionState.Connected);
        factory.Services.GetRequiredService<MqttConnectionState>().Set(ConnectionState.Connecting);

        using (var partial = JsonDocument.Parse((await SendAsync(factory, "/api/status", Supervisor)).Response.Body))
        {
            Assert.Equal("connected", partial.RootElement.GetProperty("homeAssistant").GetString());
            Assert.Equal("connecting", partial.RootElement.GetProperty("mqtt").GetString());
            Assert.False(partial.RootElement.GetProperty("healthy").GetBoolean());
        }

        factory.Services.GetRequiredService<MqttConnectionState>().Set(ConnectionState.Connected);
        using var full = JsonDocument.Parse((await SendAsync(factory, "/api/status", Supervisor)).Response.Body);
        Assert.True(full.RootElement.GetProperty("healthy").GetBoolean());
    }

    [Fact]
    public async Task Status_never_contains_a_token_or_password()
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path, new Dictionary<string, string?>
        {
            ["SUPERVISOR_TOKEN"] = "supervisor-token-secret",
            ["SWITCHBOARD_HA_TOKEN"] = "ha-token-secret",
            ["SWITCHBOARD_MQTT_PASSWORD"] = "mqtt-password-secret",
        });

        var context = await SendAsync(factory, "/api/status", Supervisor);
        var body = await ReadBodyAsync(context);

        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mode_comes_from_the_options_file_and_active_is_reported_as_such()
    {
        using var root = new TempWebRoot();
        var options = Path.Combine(root.Path, "options.json");
        await File.WriteAllTextAsync(options, """{"mode":"active","log_level":"info"}""", TestContext.Current.CancellationToken);
        using var factory = new SwitchboardHostFactory(root.Path, new Dictionary<string, string?> { ["SWITCHBOARD_OPTIONS_PATH"] = options });

        using var json = JsonDocument.Parse((await SendAsync(factory, "/api/status", Supervisor)).Response.Body);

        Assert.Equal("active", json.RootElement.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Without_a_web_build_the_root_answers_with_a_plain_text_placeholder()
    {
        // `dotnet run` before `npm run build` must still start and answer, not fail.
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);

        var context = await SendAsync(factory, "/", Supervisor);
        var body = await ReadBodyAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.StartsWith("text/plain", context.Response.ContentType, StringComparison.Ordinal);
        Assert.Contains("not a protective device", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_a_web_build_the_root_and_client_routes_serve_the_spa()
    {
        using var root = new TempWebRoot("<!doctype html><title>Switchboard</title>");
        using var factory = new SwitchboardHostFactory(root.Path);

        foreach (var path in new[] { "/", "/some/client/route" })
        {
            var context = await SendAsync(factory, path, Supervisor);
            var body = await ReadBodyAsync(context);
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            Assert.Contains("<title>Switchboard</title>", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Unknown_api_routes_are_404_not_the_spa()
    {
        using var root = new TempWebRoot("<!doctype html>");
        using var factory = new SwitchboardHostFactory(root.Path);

        var context = await SendAsync(factory, "/api/nothing-here", Supervisor);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task The_api_accepts_no_writes_in_stage_0(string method)
    {
        // Nothing in this build may change state through the web surface.
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);

        var context = await SendAsync(factory, "/api/status", Supervisor, method: method);

        Assert.Contains(context.Response.StatusCode, new[] { StatusCodes.Status404NotFound, StatusCodes.Status405MethodNotAllowed });
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    private static Task<HttpContext> SendAsync(
        SwitchboardHostFactory factory, string path, string remote, string? ingressPath = null, string method = "GET") =>
        factory.Server.SendAsync(context =>
        {
            context.Request.Method = method;
            context.Request.Path = path;
            context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
            if (ingressPath is not null)
            {
                context.Request.Headers["X-Ingress-Path"] = ingressPath;
            }
        });
}
