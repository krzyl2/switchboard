using System.Text.Json;
using Switchboard.Application.Connectivity;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Tests.Support;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// The HA link decides the health entity: it must report Connected only while HA demonstrably
/// answers, recover on its own after HA restarts (PKG-06), and never hammer HA with a bad token.
/// All timing runs on the virtual clock.
/// </summary>
public sealed class HomeAssistantClientTests : IAsyncDisposable
{
    private const string Token = "token-that-must-never-be-logged";
    private static readonly Uri SupervisorUri = new("ws://supervisor/core/websocket");

    private readonly ObservableTimeProvider _time = new();
    private readonly ScriptedTransportFactory _factory = new();
    private readonly HomeAssistantConnectionState _state = new();
    private readonly LogCapture _logs = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly HomeAssistantClient _client;
    private Task? _run;

    public HomeAssistantClientTests()
    {
        _client = new HomeAssistantClient(
            _factory,
            new HomeAssistantEndpoint(SupervisorUri, Token),
            _state,
            MsOptions.Create(TestOptions.HomeAssistant()),
            _time,
            _logs.For<HomeAssistantClient>());
    }

    [Fact]
    public async Task Successful_auth_makes_the_link_Connected()
    {
        var transport = await StartAndAuthenticateAsync();

        Assert.Equal(ConnectionState.Connected, _state.Current);
        Assert.Equal(SupervisorUri, transport.ConnectedUri);
        Assert.Equal(Token, transport.ConnectedToken);
    }

    [Fact]
    public async Task Auth_message_carries_the_token_and_nothing_else()
    {
        Start();
        var transport = await _factory.NextAsync();
        await transport.PushAndWaitAsync("""{"type":"auth_required","ha_version":"2026.10.0"}""");

        using var auth = JsonDocument.Parse(await transport.NextSentAsync());

        Assert.Equal("auth", auth.RootElement.GetProperty("type").GetString());
        Assert.Equal(Token, auth.RootElement.GetProperty("access_token").GetString());
        Assert.Equal(2, auth.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task Link_is_not_Connected_before_auth_ok()
    {
        Start();
        var transport = await _factory.NextAsync();
        await transport.PushAndWaitAsync("""{"type":"auth_required"}""");

        Assert.Equal(ConnectionState.Connecting, _state.Current);
    }

    [Fact]
    public async Task Rejected_token_waits_the_full_max_delay_before_trying_again()
    {
        // A bad token must not turn into a tight loop of failed logins against the user's HA.
        Start();
        var transport = await _factory.NextAsync();
        await transport.PushAndWaitAsync("""{"type":"auth_required"}""");
        transport.Push("""{"type":"auth_invalid","message":"Invalid access token or password"}""");
        await _time.WaitForTimerAsync(TestOptions.ReconnectMax);

        Assert.Equal(ConnectionState.Disconnected, _state.Current);
        _time.Advance(TestOptions.ReconnectMax - TimeSpan.FromTicks(1));
        Assert.Equal(1, _factory.CreatedCount);

        _time.Advance(TimeSpan.FromTicks(1));
        await _factory.NextAsync();
        Assert.Equal(2, _factory.CreatedCount);
    }

    [Fact]
    public async Task Rejected_token_is_logged_as_an_error_without_the_token()
    {
        Start();
        var transport = await _factory.NextAsync();
        await transport.PushAndWaitAsync("""{"type":"auth_required"}""");
        transport.Push("""{"type":"auth_invalid","message":"Invalid access token or password"}""");
        await _time.WaitForTimerAsync(TestOptions.ReconnectMax);

        Assert.Contains(_logs.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error);
        Assert.DoesNotContain(Token, _logs.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pings_go_out_every_interval_with_increasing_ids()
    {
        var transport = await StartAndAuthenticateAsync();

        var ids = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            _time.Advance(TestOptions.PingInterval);
            var ping = await NextCommandAsync(transport);
            Assert.Equal("ping", ping.Type);
            ids.Add(ping.Id);
            await transport.PushAndWaitAsync($$"""{"id":{{ping.Id}},"type":"pong"}""");
        }

        Assert.Equal([1, 2, 3], ids);
    }

    [Fact]
    public async Task Answered_pings_keep_the_link_Connected()
    {
        var transport = await StartAndAuthenticateAsync();

        _time.Advance(TestOptions.PingInterval);
        var ping = await NextCommandAsync(transport);
        await transport.PushAndWaitAsync($$"""{"id":{{ping.Id}},"type":"pong"}""");
        _time.Advance(TestOptions.PongTimeout);

        Assert.Equal(ConnectionState.Connected, _state.Current);
        Assert.False(transport.IsAborted);
    }

    [Fact]
    public async Task Missing_pong_drops_the_socket_and_reconnects()
    {
        // TCP can stay "up" long after HA stopped answering; only a pong proves the link is alive.
        var first = await StartAndAuthenticateAsync();
        _time.Advance(TestOptions.PingInterval);
        await NextCommandAsync(first);

        _time.Advance(TestOptions.PongTimeout);
        await _time.WaitForTimerAsync(TestOptions.ReconnectMin);

        Assert.True(first.IsAborted);
        Assert.NotEqual(ConnectionState.Connected, _state.Current);

        _time.Advance(TestOptions.ReconnectMin);
        var second = await _factory.NextAsync();
        await second.Connected;
    }

    [Fact]
    public async Task A_pong_for_another_id_does_not_count_as_an_answer()
    {
        var transport = await StartAndAuthenticateAsync();
        _time.Advance(TestOptions.PingInterval);
        var ping = await NextCommandAsync(transport);

        await transport.PushAndWaitAsync($$"""{"id":{{ping.Id + 100}},"type":"pong"}""");
        _time.Advance(TestOptions.PongTimeout);
        await _time.WaitForTimerAsync(TestOptions.ReconnectMin);

        Assert.True(transport.IsAborted);
    }

    [Fact]
    public async Task Ids_restart_on_a_new_connection()
    {
        var first = await StartAndAuthenticateAsync();
        _time.Advance(TestOptions.PingInterval);
        await NextCommandAsync(first);
        _time.Advance(TestOptions.PongTimeout);
        await _time.WaitForTimerAsync(TestOptions.ReconnectMin);
        _time.Advance(TestOptions.ReconnectMin);

        var second = await AuthenticateNextAsync();
        _time.Advance(TestOptions.PingInterval);

        Assert.Equal(1, (await NextCommandAsync(second)).Id);
    }

    [Fact]
    public async Task Backoff_doubles_caps_at_max_and_resets_after_successful_auth()
    {
        _factory.FailConnects = true;
        Start();

        var expected = new[] { 1, 2, 4, 8, 8 }.Select(s => TimeSpan.FromSeconds(s)).ToArray();
        for (var attempt = 0; attempt < expected.Length; attempt++)
        {
            await _time.WaitForTimerAsync(expected[attempt]);
            await _factory.NextAsync();
            if (attempt == expected.Length - 1)
            {
                _factory.FailConnects = false;
            }

            _time.Advance(expected[attempt]);
        }

        // Failed connects arm no other timer, so the first armings are exactly the backoff steps.
        Assert.Equal(expected, _time.Armed.Take(expected.Length));

        var connected = await AuthenticateNextAsync();
        connected.Push(null);
        await _time.WaitForTimerAsync(TestOptions.ReconnectMin);

        Assert.Equal(TestOptions.ReconnectMin, _time.Armed[^1]);
    }

    [Fact]
    public async Task Peer_closing_the_socket_triggers_a_reconnect()
    {
        var transport = await StartAndAuthenticateAsync();

        transport.Push(null);
        await _time.WaitForTimerAsync(TestOptions.ReconnectMin);
        Assert.Equal(ConnectionState.Disconnected, _state.Current);

        _time.Advance(TestOptions.ReconnectMin);
        await (await _factory.NextAsync()).Connected;
    }

    [Fact]
    public async Task A_handshake_that_never_comes_is_abandoned_after_the_answer_deadline()
    {
        Start();
        var silent = await _factory.NextAsync();
        await silent.Connected;
        await _time.WaitForTimerAsync(TestOptions.PongTimeout);

        _time.Advance(TestOptions.PongTimeout);
        await _time.WaitForTimerAsync(TestOptions.ReconnectMin);

        Assert.True(silent.IsDisposed);
    }

    [Fact]
    public async Task Close_sends_a_clean_close_and_stops_reconnecting()
    {
        var transport = await StartAndAuthenticateAsync();

        await _client.CloseAsync(CancellationToken.None);
        await _run!.Soon();

        Assert.True(transport.CloseRequested);
        Assert.Equal(1, _factory.CreatedCount);
        Assert.Equal(ConnectionState.Disconnected, _state.Current);
    }

    [Fact]
    public async Task Cancellation_ends_the_run_loop()
    {
        await StartAndAuthenticateAsync();

        await _stop.CancelAsync();
        await _run!.Soon();

        Assert.Equal(ConnectionState.Disconnected, _state.Current);
    }

    [Theory]
    [InlineData("call_service")]
    [InlineData("fire_event")]
    [InlineData("execute_script")]
    [InlineData("subscribe_trigger")]
    [InlineData("config/entity_registry/update")]
    [InlineData("PING")]
    [InlineData("")]
    public async Task Commands_outside_the_allow_list_are_refused_and_nothing_is_sent(string type)
    {
        // The hard rule behind "never command an undeclared entity": the type is checked before the socket is touched.
        var transport = await StartAndAuthenticateAsync();
        var sentBefore = transport.Sent.Count;

        var refused = await Assert.ThrowsAsync<HomeAssistantCommandRefusedException>(
            () => _client.SendCommandAsync(type, CancellationToken.None));

        Assert.Equal(type, refused.CommandType);
        Assert.Equal(sentBefore, transport.Sent.Count);
    }

    [Fact]
    public async Task An_allowed_command_does_go_out()
    {
        var transport = await StartAndAuthenticateAsync();

        await _client.SendCommandAsync(HomeAssistantCommandAllowList.Ping, CancellationToken.None);

        Assert.Equal("ping", (await NextCommandAsync(transport)).Type);
    }

    [Fact]
    public async Task The_token_never_appears_in_any_log_line()
    {
        var transport = await StartAndAuthenticateAsync();
        _time.Advance(TestOptions.PingInterval);
        await NextCommandAsync(transport);
        _time.Advance(TestOptions.PongTimeout);
        await _time.WaitForTimerAsync(TestOptions.ReconnectMin);

        Assert.NotEmpty(_logs.Entries);
        Assert.DoesNotContain(Token, _logs.AllText, StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_run is not null)
        {
            await _run.Soon();
        }

        _stop.Dispose();
    }

    private void Start() => _run = _client.RunAsync(_stop.Token);

    private async Task<ScriptedWebSocketTransport> StartAndAuthenticateAsync()
    {
        Start();
        return await AuthenticateNextAsync();
    }

    private async Task<ScriptedWebSocketTransport> AuthenticateNextAsync()
    {
        var transport = await _factory.NextAsync();
        await transport.PushAndWaitAsync("""{"type":"auth_required"}""");
        Assert.Equal("auth", JsonDocument.Parse(await transport.NextSentAsync()).RootElement.GetProperty("type").GetString());
        await transport.PushAndWaitAsync("""{"type":"auth_ok","ha_version":"2026.10.0"}""");
        await _state.WaitForAsync(ConnectionState.Connected);
        return transport;
    }

    private static async Task<(int Id, string? Type)> NextCommandAsync(ScriptedWebSocketTransport transport)
    {
        using var command = JsonDocument.Parse(await transport.NextSentAsync());
        return (command.RootElement.GetProperty("id").GetInt32(), command.RootElement.GetProperty("type").GetString());
    }
}
