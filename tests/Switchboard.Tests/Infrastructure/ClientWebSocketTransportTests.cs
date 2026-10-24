using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Connectivity;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Tests.Support;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// The scripted tests prove the protocol logic; this one proves the real socket adapter speaks it:
/// bearer header on the upgrade (the Supervisor proxy needs it), auth handshake, ping/pong framing
/// and a clean close. A minimal HA stand-in runs on loopback; all client timing is still virtual.
/// </summary>
public sealed class ClientWebSocketTransportTests : IAsyncDisposable
{
    private const string Token = "loopback-token";

    private readonly Channel<string> _received = Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource<string> _upgradeAuthorization = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _peerClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WebApplication _server;

    public ClientWebSocketTransportTests()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        _server = builder.Build();
        _server.UseWebSockets();
        _server.Map("/api/websocket", ServeAsync);
    }

    [Fact]
    public async Task Real_socket_authenticates_answers_keepalive_and_closes_cleanly()
    {
        await _server.StartAsync(TestContext.Current.CancellationToken);
        var address = _server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var endpoint = new HomeAssistantEndpoint(new Uri(address.Replace("http://", "ws://", StringComparison.Ordinal) + "/api/websocket"), Token);

        var time = new ObservableTimeProvider();
        var state = new HomeAssistantConnectionState();
        var client = new HomeAssistantClient(
            new ClientWebSocketTransportFactory(),
            endpoint,
            state,
            MsOptions.Create(TestOptions.HomeAssistant()),
            time,
            new LogCapture().For<HomeAssistantClient>(),
            new RecordingSubscriptionSink());
        using var stop = new CancellationTokenSource();
        var run = client.RunAsync(stop.Token);

        await state.WaitForAsync(ConnectionState.Connected);
        Assert.Equal($"Bearer {Token}", await _upgradeAuthorization.Task.Soon());
        using (var auth = JsonDocument.Parse(await _received.Reader.ReadAsync(TestContext.Current.CancellationToken).Soon()))
        {
            Assert.Equal("auth", auth.RootElement.GetProperty("type").GetString());
            Assert.Equal(Token, auth.RootElement.GetProperty("access_token").GetString());
        }

        time.Advance(TestOptions.PingInterval);
        using (var ping = JsonDocument.Parse(await _received.Reader.ReadAsync(TestContext.Current.CancellationToken).Soon()))
        {
            Assert.Equal("ping", ping.RootElement.GetProperty("type").GetString());
            Assert.Equal(1, ping.RootElement.GetProperty("id").GetInt32());
        }

        await client.CloseAsync(TestContext.Current.CancellationToken);
        await _peerClosed.Task.Soon();
        await run.Soon();
        Assert.Equal(ConnectionState.Disconnected, state.Current);
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    // Just enough of Home Assistant's WebSocket API: auth handshake, then pong for every ping.
    private async Task ServeAsync(HttpContext context)
    {
        _upgradeAuthorization.TrySetResult(context.Request.Headers.Authorization.ToString());
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await SendAsync(socket, """{"type":"auth_required","ha_version":"2026.10.0"}""");
        var buffer = new byte[4096];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                _peerClosed.TrySetResult();
                return;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            _received.Writer.TryWrite(text);
            using var message = JsonDocument.Parse(text);
            switch (message.RootElement.GetProperty("type").GetString())
            {
                case "auth":
                    await SendAsync(socket, """{"type":"auth_ok","ha_version":"2026.10.0"}""");
                    break;
                case "ping":
                    await SendAsync(socket, $$"""{"id":{{message.RootElement.GetProperty("id").GetInt32()}},"type":"pong"}""");
                    break;
            }
        }
    }

    private static Task SendAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
}
