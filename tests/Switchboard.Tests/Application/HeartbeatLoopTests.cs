using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Connectivity;
using Switchboard.Application.Health;
using Switchboard.Tests.Support;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Switchboard.Tests.Application;

/// <summary>
/// The heartbeat is what an HA-side automation watches to shed loads when the add-on is gone
/// (SAFE-02). It has to keep beating on schedule, and it may only claim health when both links are up.
/// </summary>
public sealed class HeartbeatLoopTests
{
    private readonly ObservableTimeProvider _time = new();
    private readonly HomeAssistantConnectionState _homeAssistant = new();
    private readonly MqttConnectionState _mqtt = new();
    private readonly RecordingHealthPublisher _publisher = new();

    [Fact]
    public async Task Publishes_at_start_and_then_once_per_interval()
    {
        using var stop = new CancellationTokenSource();
        var run = StartLoop(stop.Token);

        await _publisher.NextAsync();
        await _time.WaitForTimerAsync(TestOptions.HeartbeatInterval);
        for (var beat = 0; beat < 3; beat++)
        {
            Assert.Equal(beat + 1, _publisher.Count);
            _time.Advance(TestOptions.HeartbeatInterval);
            await _publisher.NextAsync();
        }

        Assert.Equal(4, _publisher.Count);
        await stop.CancelAsync();
        await run.Soon();
    }

    [Fact]
    public async Task Nothing_is_published_between_intervals()
    {
        using var stop = new CancellationTokenSource();
        var run = StartLoop(stop.Token);
        await _publisher.NextAsync();
        await _time.WaitForTimerAsync(TestOptions.HeartbeatInterval);

        _time.Advance(TestOptions.HeartbeatInterval - TimeSpan.FromTicks(1));

        Assert.Equal(1, _publisher.Count);
        await stop.CancelAsync();
        await run.Soon();
    }

    [Theory]
    [InlineData(ConnectionState.Connected, ConnectionState.Connected, true)]
    [InlineData(ConnectionState.Connected, ConnectionState.Connecting, false)]
    [InlineData(ConnectionState.Connected, ConnectionState.Disconnected, false)]
    [InlineData(ConnectionState.Connecting, ConnectionState.Connected, false)]
    [InlineData(ConnectionState.Disconnected, ConnectionState.Connected, false)]
    [InlineData(ConnectionState.Disconnected, ConnectionState.Disconnected, false)]
    public async Task Beat_is_ON_only_when_both_Home_Assistant_and_MQTT_are_connected(
        ConnectionState homeAssistant, ConnectionState mqtt, bool expected)
    {
        _homeAssistant.Set(homeAssistant);
        _mqtt.Set(mqtt);
        using var stop = new CancellationTokenSource();
        var run = StartLoop(stop.Token);

        Assert.Equal(expected, await _publisher.NextAsync());

        await stop.CancelAsync();
        await run.Soon();
    }

    [Fact]
    public async Task Each_beat_reflects_the_link_state_at_that_moment()
    {
        _homeAssistant.Set(ConnectionState.Connected);
        _mqtt.Set(ConnectionState.Connected);
        using var stop = new CancellationTokenSource();
        var run = StartLoop(stop.Token);
        Assert.True(await _publisher.NextAsync());
        await _time.WaitForTimerAsync(TestOptions.HeartbeatInterval);

        _homeAssistant.Set(ConnectionState.Connecting);
        _time.Advance(TestOptions.HeartbeatInterval);
        Assert.False(await _publisher.NextAsync());

        _homeAssistant.Set(ConnectionState.Connected);
        _time.Advance(TestOptions.HeartbeatInterval);
        Assert.True(await _publisher.NextAsync());

        await stop.CancelAsync();
        await run.Soon();
    }

    [Fact]
    public async Task A_failed_publish_does_not_stop_the_heartbeat()
    {
        _publisher.FailNext = true;
        using var stop = new CancellationTokenSource();
        var run = StartLoop(stop.Token);
        await _publisher.NextAsync();
        await _time.WaitForTimerAsync(TestOptions.HeartbeatInterval);

        _time.Advance(TestOptions.HeartbeatInterval);
        await _publisher.NextAsync();

        Assert.Equal(2, _publisher.Count);
        await stop.CancelAsync();
        await run.Soon();
    }

    private Task StartLoop(CancellationToken cancellationToken)
    {
        var loop = new HeartbeatLoop(
            new HealthEvaluator(_homeAssistant, _mqtt),
            _publisher,
            MsOptions.Create(TestOptions.Heartbeat()),
            _time,
            NullLogger<HeartbeatLoop>.Instance);
        return loop.RunAsync(cancellationToken);
    }

    private sealed class RecordingHealthPublisher : IHealthPublisher
    {
        private readonly Channel<bool> _beats = Channel.CreateUnbounded<bool>();
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public bool FailNext { get; set; }

        public Task PublishHealthAsync(bool healthy, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            _beats.Writer.TryWrite(healthy);
            if (FailNext)
            {
                FailNext = false;
                return Task.FromException(new InvalidOperationException("broker gone"));
            }

            return Task.CompletedTask;
        }

        public async Task<bool> NextAsync() => await _beats.Reader.ReadAsync().Soon();
    }
}
