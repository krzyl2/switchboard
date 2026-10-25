using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Configuration;
using Switchboard.Application.Connectivity;
using Switchboard.Application.Effector;
using Switchboard.Application.Signals;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Tests.Support;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// A service call on the protection link (Stage C): it leaves in the allow-list's one shape after the
/// declared-actuator check, its answer is matched by id, and "commanded" means Home Assistant said
/// success. An error, no answer in time, or a link that ends first is never an acknowledgement; and
/// a refused call never touches the socket.
/// </summary>
public sealed class HomeAssistantCallServiceTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TestOptions.CommandTimeout;
    private static readonly ActuatorCall Off = new(EntityId.Parse("switch.heater_1"), "switch", ActuatorService.TurnOff, null);
    private static readonly ActuatorDeclaredCheck Declared = (_, _, _, _, _) => true;

    private readonly ObservableTimeProvider _time = new();
    private readonly ScriptedTransportFactory _factory = new();
    private readonly HomeAssistantConnectionState _state = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly HomeAssistantClient _client;
    private Task? _run;

    public HomeAssistantCallServiceTests()
    {
        _client = new HomeAssistantClient(
            _factory,
            new HomeAssistantEndpoint(new Uri("ws://supervisor/core/websocket"), "token"),
            _state,
            MsOptions.Create(TestOptions.HomeAssistant()),
            _time,
            NullLogger<HomeAssistantClient>.Instance,
            new RecordingSubscriptionSink());
    }

    [Fact]
    public async Task A_call_leaves_in_its_exact_shape_and_success_is_an_acknowledgement()
    {
        var transport = await ConnectAsync();

        var call = _client.CallServiceAsync(Off, Declared, Timeout, CancellationToken.None);
        var frame = await transport.NextSentAsync();
        using var sent = JsonDocument.Parse(frame);
        var id = sent.RootElement.GetProperty("id").GetInt32();

        Assert.Equal($$$"""{"id":{{{id}}},"type":"call_service","domain":"switch","service":"turn_off","target":{"entity_id":"switch.heater_1"}}""", frame);
        await transport.PushAndWaitAsync("{\"id\":" + id + ",\"type\":\"result\",\"success\":true,\"result\":{\"context\":{\"id\":\"x\"}}}");
        Assert.Equal(new CallOutcome(CommandOutcome.Acknowledged, null), await call.Soon());
    }

    [Fact]
    public async Task An_error_from_home_assistant_is_a_failure_with_its_message()
    {
        var transport = await ConnectAsync();

        var call = _client.CallServiceAsync(Off, Declared, Timeout, CancellationToken.None);
        var id = Id(await transport.NextSentAsync());
        await transport.PushAndWaitAsync($$$"""{"id":{{{id}}},"type":"result","success":false,"error":{"code":"not_found","message":"Entity switch.heater_1 not found"}}""");

        var outcome = await call.Soon();
        Assert.Equal(CommandOutcome.Failed, outcome.Outcome);
        Assert.Contains("not found", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_answer_in_time_is_a_timeout_on_the_injected_clock()
    {
        var transport = await ConnectAsync();

        var call = _client.CallServiceAsync(Off, Declared, Timeout, CancellationToken.None);
        await transport.NextSentAsync();
        await _time.WaitForTimerAsync(Timeout);
        Assert.False(call.IsCompleted);
        _time.Advance(Timeout);

        Assert.Equal(CommandOutcome.TimedOut, (await call.Soon()).Outcome);
    }

    [Fact]
    public async Task A_link_that_ends_before_the_answer_is_no_answer_never_an_acknowledgement_nor_an_error()
    {
        // The call may or may not have run: like a timeout, the device's reported state decides. An
        // error would count a mismatch against a healthy relay for every Home Assistant restart.
        var transport = await ConnectAsync();

        var call = _client.CallServiceAsync(Off, Declared, Timeout, CancellationToken.None);
        await transport.NextSentAsync();
        transport.Push(null);

        var outcome = await call.Soon();
        Assert.Equal(CommandOutcome.TimedOut, outcome.Outcome);
        Assert.Contains("link closed", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_call_the_configuration_does_not_declare_never_touches_the_socket()
    {
        var transport = await ConnectAsync();
        var before = transport.Sent.Count;

        await Assert.ThrowsAsync<HomeAssistantCommandRefusedException>(() => _client.CallServiceAsync(Off, (_, _, _, _, _) => false, Timeout, CancellationToken.None));

        Assert.Equal(before, transport.Sent.Count);
    }

    [Fact]
    public async Task Before_the_session_delivered_its_states_nothing_is_sent()
    {
        var outcome = await _client.CallServiceAsync(Off, Declared, Timeout, CancellationToken.None);

        Assert.Equal(CommandOutcome.NotSent, outcome.Outcome);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_run is not null)
        {
            try
            {
                await _run.Soon();
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }

    private static int Id(string frame)
    {
        using var document = JsonDocument.Parse(frame);
        return document.RootElement.GetProperty("id").GetInt32();
    }

    private async Task<ScriptedWebSocketTransport> ConnectAsync()
    {
        _run = _client.RunAsync(_stop.Token);
        var transport = await _factory.NextAsync();
        await transport.PushAndWaitAsync("""{"type":"auth_required"}""");
        await transport.NextSentAsync();
        await transport.PushAndWaitAsync("""{"type":"auth_ok"}""");
        await _state.WaitForAsync(ConnectionState.Connected);
        // Ready once the session delivered its (here: empty) initial states; a hang guard only.
        for (var tries = 0; !_client.Ready; tries++)
        {
            Assert.True(tries < 2000, "The session never became ready.");
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, TestContext.Current.CancellationToken);
        }

        return transport;
    }
}
