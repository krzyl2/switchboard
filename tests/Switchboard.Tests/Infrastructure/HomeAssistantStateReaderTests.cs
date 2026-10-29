using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Signals;
using Switchboard.Domain.Signals;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// The freshness probe is the only REST call that carries the Home Assistant token. It must be able
/// to do exactly one thing, GET the state of one validated entity, and nothing else.
/// </summary>
public sealed class HomeAssistantStateReaderTests
{
    private const string Token = "token-for-states";
    private static readonly Uri Base = new("http://supervisor/core/api/");

    private readonly ObservableTimeProvider _time = new();
    private readonly RecordingHandler _handler = new();

    [Fact]
    public async Task Reads_last_reported_from_rest_state()
    {
        _handler.Respond(HttpStatusCode.OK, """
            {"entity_id":"sensor.meter_current_a","state":"5.437","attributes":{"unit_of_measurement":"A"},
             "last_changed":"2026-10-21T10:20:54.020123+00:00","last_reported":"2026-10-21T10:27:24.020456+00:00",
             "last_updated":"2026-10-21T10:21:54.000001+00:00","context":{"id":"x"}}
            """);

        var result = await Reader().ReadAsync(EntityId.Parse("sensor.meter_current_a"), CancellationToken.None);

        var update = Assert.IsType<EntityStateRead.Found>(result).Update;
        Assert.Equal(("5.437", "A"), (update.State, update.Unit));
        Assert.Equal(DateTimeOffset.Parse("2026-10-21T10:21:54.000001+00:00", System.Globalization.CultureInfo.InvariantCulture), update.LastUpdated);
        Assert.Equal(DateTimeOffset.Parse("2026-10-21T10:27:24.020456+00:00", System.Globalization.CultureInfo.InvariantCulture), update.Reported);
    }

    [Fact]
    public async Task Probe_reads_options()
    {
        // A select's options come with its state (0.7.1): the send-time options check reads them.
        _handler.Respond(HttpStatusCode.OK, """
            {"entity_id":"select.charger_force","state":"0","attributes":{"options":["0","1","2"],"friendly_name":"Force"},
             "last_changed":"2026-10-21T10:20:54.020123+00:00","last_reported":"2026-10-21T10:27:24.020456+00:00",
             "last_updated":"2026-10-21T10:21:54.000001+00:00","context":{"id":"x"}}
            """);

        var update = Assert.IsType<EntityStateRead.Found>(await Reader().ReadAsync(EntityId.Parse("select.charger_force"), CancellationToken.None)).Update;

        Assert.Equal(["0", "1", "2"], update.Options!.Value);
        Assert.Null(update.Unit);
    }

    [Fact]
    public async Task Only_GET_states_of_a_validated_entity_is_possible()
    {
        _handler.Respond(HttpStatusCode.NotFound, "{}");

        await Reader().ReadAsync(EntityId.Parse("sensor.meter_power_b"), CancellationToken.None);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(new Uri("http://supervisor/core/api/states/sensor.meter_power_b"), request.Uri);
        Assert.Equal($"Bearer {Token}", request.Authorization);
        Assert.Null(request.Body);

        // The type offers no way to choose a method, a path or a body.
        var publicMethods = typeof(HomeAssistantStateReader).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
        var read = Assert.Single(publicMethods);
        Assert.Equal([typeof(EntityId), typeof(CancellationToken)], read.GetParameters().Select(p => p.ParameterType));
    }

    [Fact]
    public async Task Missing_entity_reported_as_missing()
    {
        _handler.Respond(HttpStatusCode.NotFound, """{"message":"Entity not found."}""");

        Assert.IsType<EntityStateRead.NotFound>(await Reader().ReadAsync(EntityId.Parse("sensor.gone"), CancellationToken.None));
    }

    [Fact]
    public async Task An_answer_for_another_entity_is_not_taken()
    {
        _handler.Respond(HttpStatusCode.OK, """{"entity_id":"sensor.other","state":"1","attributes":{},"last_changed":"2026-10-21T10:20:54+00:00"}""");

        Assert.IsType<EntityStateRead.Failed>(await Reader().ReadAsync(EntityId.Parse("sensor.meter_current_a"), CancellationToken.None));
    }

    [Fact]
    public async Task Oversized_answer_is_refused()
    {
        _handler.Respond(HttpStatusCode.OK, "{\"x\":\"" + new string('a', 300 * 1024) + "\"}");

        Assert.IsType<EntityStateRead.Failed>(await Reader().ReadAsync(EntityId.Parse("sensor.meter_current_a"), CancellationToken.None));
    }

    [Fact]
    public async Task Timeout_runs_on_the_clock()
    {
        _handler.Hold = true;
        var reading = Reader().ReadAsync(EntityId.Parse("sensor.meter_current_a"), CancellationToken.None);

        await _time.WaitForTimerAsync(TestOptions.StateReadTimeout);
        _time.Advance(TestOptions.StateReadTimeout);

        Assert.IsType<EntityStateRead.Failed>(await reading.Soon());
    }

    [Fact]
    public void Supervisor_core_api_as_addon_HA_url_standalone()
    {
        var addon = HomeAssistantEndpointResolution.Resolve(new Switchboard.Infrastructure.Runtime.RuntimeEnvironment { SupervisorToken = "t" }, TestOptions.HomeAssistant());
        var standalone = HomeAssistantEndpointResolution.Resolve(
            new Switchboard.Infrastructure.Runtime.RuntimeEnvironment { HomeAssistantUrl = "ws://ha.local:8123/api/websocket", HomeAssistantToken = "t" },
            TestOptions.HomeAssistant());

        Assert.Equal(new Uri("http://supervisor/core/api/"), addon.Endpoint!.RestBaseUri);
        Assert.Equal(new Uri("http://ha.local:8123/api/"), standalone.Endpoint!.RestBaseUri);
    }

    private HomeAssistantStateReader Reader() =>
        new(new HttpClient(_handler) { Timeout = Timeout.InfiniteTimeSpan }, Base, Token, TestOptions.StateReadTimeout, _time, NullLogger<HomeAssistantStateReader>.Instance);

    internal sealed record RecordedRequest(HttpMethod Method, Uri? Uri, string? Authorization, string? Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<RecordedRequest> _requests = new();
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _body = "{}";

        public bool Hold { get; set; }

        public IReadOnlyList<RecordedRequest> Requests => [.. _requests];

        public void Respond(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Enqueue(new RecordedRequest(
                request.Method,
                request.RequestUri,
                request.Headers.Authorization?.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (Hold)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, cancellationToken);
            }

            return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
        }
    }
}

/// <summary>The live source: session status follows the subscription, probes arrive as probe batches, one per entity at a time.</summary>
public sealed class HomeAssistantSignalSourceTests
{
    private readonly SignalInbox _inbox = new();

    [Fact]
    public void Status_is_connecting_at_authentication_and_connected_once_the_initial_state_is_in()
    {
        var source = new HomeAssistantSignalSource(_inbox, new UnavailableEntityStateReader(), NullLogger<HomeAssistantSignalSource>.Instance);

        source.SessionStarted();
        source.Deliver(EntityUpdateBatch.Of(BatchOrigin.Initial));
        source.SessionEnded();

        Assert.Equal(
            ["Connecting", "Initial", "Connected", "Disconnected"],
            Drain().Select(i => i switch
            {
                SourceStatusInput s => s.Status.ToString(),
                BatchInput b => b.Batch.Origin.ToString(),
                _ => "?",
            }));
    }

    [Fact]
    public void Track_raises_a_change_only_when_the_entity_set_changes()
    {
        var source = new HomeAssistantSignalSource(_inbox, new UnavailableEntityStateReader(), NullLogger<HomeAssistantSignalSource>.Instance);
        var changes = 0;
        source.TrackedChanged += (_, _) => changes++;

        source.Track([new TrackedEntity(EntityId.Parse("sensor.b"), "W"), new TrackedEntity(EntityId.Parse("sensor.a"), "A")]);
        source.Track([new TrackedEntity(EntityId.Parse("sensor.a"), "A"), new TrackedEntity(EntityId.Parse("sensor.b"), "W")]);

        Assert.Equal(1, changes);
        Assert.Equal(["sensor.a", "sensor.b"], source.Tracked.Select(e => e.Value));
    }

    [Fact]
    public void Track_carries_every_declared_attribute_of_an_entity_and_resubscribes_when_they_change()
    {
        // Why: two sun signals read two attributes of one entity; the subscription's parser is built from the attribute names, so a change needs a new one.
        var source = new HomeAssistantSignalSource(_inbox, new UnavailableEntityStateReader(), NullLogger<HomeAssistantSignalSource>.Instance);
        var changes = 0;
        source.TrackedChanged += (_, _) => changes++;
        var sun = EntityId.Parse("sun.sun");

        source.Track([new TrackedEntity(sun, null) { Attribute = "next_setting" }, new TrackedEntity(sun, null) { Attribute = "next_rising" }]);
        source.Track([new TrackedEntity(sun, null) { Attribute = "next_rising" }, new TrackedEntity(sun, null) { Attribute = "next_setting" }]);
        Assert.Equal(1, changes);
        Assert.Equal(["next_rising", "next_setting"], source.TimestampAttributes[sun]);

        source.Track([new TrackedEntity(sun, null) { Attribute = "next_rising" }]);
        Assert.Equal(2, changes);
        Assert.Equal(["next_rising"], source.TimestampAttributes[sun]);
        Assert.Single(source.Tracked);
    }

    [Fact]
    public async Task At_most_one_probe_per_entity_is_in_flight_and_its_answer_is_a_probe_batch()
    {
        var reader = new ScriptedReader();
        var source = new HomeAssistantSignalSource(_inbox, reader, NullLogger<HomeAssistantSignalSource>.Instance);
        var present = EntityId.Parse("sensor.present");

        source.RequestProbe(present);
        source.RequestProbe(present);
        reader.Complete(present, new EntityStateRead.Found(new EntityUpdate(present, "1", "W", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)));
        var batch = await NextBatchAsync();

        Assert.Equal(1, reader.Calls[present]);
        Assert.Equal((BatchOrigin.Probe, "sensor.present"), (batch.Origin, batch.Updates.Single().Entity.Value));
    }

    [Fact]
    public async Task Probe_of_an_entity_home_assistant_does_not_know_reports_it_missing()
    {
        var reader = new ScriptedReader();
        var source = new HomeAssistantSignalSource(_inbox, reader, NullLogger<HomeAssistantSignalSource>.Instance);
        var gone = EntityId.Parse("sensor.gone");

        source.RequestProbe(gone);
        reader.Complete(gone, new EntityStateRead.NotFound());

        Assert.Equal(gone, (await NextBatchAsync()).Missing.Single());
    }

    private async Task<EntityUpdateBatch> NextBatchAsync()
    {
        while (true)
        {
            if (await _inbox.Reader.ReadAsync().AsTask().Soon() is BatchInput batch)
            {
                return batch.Batch;
            }
        }
    }

    private List<SignalInput> Drain()
    {
        var inputs = new List<SignalInput>();
        while (_inbox.Reader.TryRead(out var input))
        {
            inputs.Add(input);
        }

        return inputs;
    }

    private sealed class ScriptedReader : IEntityStateReader
    {
        private readonly ConcurrentDictionary<EntityId, TaskCompletionSource<EntityStateRead>> _pending = new();

        public ConcurrentDictionary<EntityId, int> Calls { get; } = new();

        public Task<EntityStateRead> ReadAsync(EntityId entity, CancellationToken cancellationToken)
        {
            Calls.AddOrUpdate(entity, 1, (_, n) => n + 1);
            return Pending(entity).Task;
        }

        public void Complete(EntityId entity, EntityStateRead result) => Pending(entity).TrySetResult(result);

        private TaskCompletionSource<EntityStateRead> Pending(EntityId entity) =>
            _pending.GetOrAdd(entity, _ => new TaskCompletionSource<EntityStateRead>(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}
