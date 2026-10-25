using System.Collections.Immutable;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Switchboard.Application.Connectivity;
using Switchboard.Application.Options;
using Switchboard.Application.Setup.Discovery;
using Switchboard.Application.Setup.Readouts;
using Switchboard.Application.Signals;

namespace Switchboard.Infrastructure.HomeAssistant;

/// <summary>
/// The setup wizard's own, on-demand connection to Home Assistant: a second <see cref="HomeAssistantClient"/>
/// with its own connection state, so its failures never touch the protection link, its health or
/// the signal source. It sends only read-only messages: the three discovery queries, and
/// subscribe_entities / unsubscribe_events for the entities the wizard is showing (at most
/// MaxReadoutEntities, each for a lease the wizard renews, or held by a self-test session for its
/// whole timeout). It opens on the first setup request and closes after DiscoveryLinkIdleTimeout
/// without requests, leases or self-tests. Leases and the idle time run on the host clock; readout
/// ages on the signal clock.
/// </summary>
public sealed class HomeAssistantDiscoveryLink : IEntityDiscoverySource, ICandidateReadouts, IEntitySubscriptionSink, IAsyncDisposable
{
    private readonly IWebSocketTransportFactory _transports;
    private readonly HomeAssistantEndpoint _endpoint;
    private readonly IOptions<HomeAssistantOptions> _homeAssistant;
    private readonly SetupOptions _options;
    private readonly CandidateReadoutStore _store;
    private readonly IEntityStateReader _reader;
    private readonly TimeProvider _time;
    private readonly TimeProvider _signalTime;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<HomeAssistantDiscoveryLink> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<EntityId, DateTimeOffset> _leases = [];
    private readonly List<HoldHandle> _holds = [];
    private readonly Dictionary<EntityId, DateTimeOffset> _probedAt = [];
    private readonly ITimer _idle;
    private readonly ITimer _leaseExpiry;
    private ImmutableArray<EntityId> _tracked = [];
    private Session? _session;
    private bool _disposed;

    public HomeAssistantDiscoveryLink(
        IWebSocketTransportFactory transports,
        HomeAssistantEndpoint endpoint,
        IOptions<HomeAssistantOptions> homeAssistant,
        IOptions<SetupOptions> options,
        CandidateReadoutStore store,
        IEntityStateReader reader,
        TimeProvider time,
        SignalClock clock,
        ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        _transports = transports;
        _endpoint = endpoint;
        _homeAssistant = homeAssistant;
        _options = options.Value;
        _store = store;
        _reader = reader;
        _time = time;
        _signalTime = clock.Time;
        _loggers = loggers;
        _logger = loggers.CreateLogger<HomeAssistantDiscoveryLink>();
        _idle = time.CreateTimer(_ => OnIdle(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _leaseExpiry = time.CreateTimer(_ => OnLeaseExpiry(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event EventHandler? TrackedChanged;

    /// <summary>The link's own connection state; never the protection link's.</summary>
    public IConnectionState? Connection
    {
        get
        {
            lock (_gate)
            {
                return _session?.State;
            }
        }
    }

    public IReadOnlyList<EntityId> Tracked => _tracked;

    public async Task<DiscoverySnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var session = EnsureOpen();
        await WaitConnectedAsync(session, cancellationToken).ConfigureAwait(false);
        var timeout = _options.DiscoveryRequestTimeout;
        try
        {
            var states = await session.Client.QueryAsync(HomeAssistantCommandAllowList.GetStates, timeout, cancellationToken).ConfigureAwait(false);
            var entities = await OptionalAsync(session, HomeAssistantCommandAllowList.EntityRegistryList, cancellationToken).ConfigureAwait(false);
            var devices = await OptionalAsync(session, HomeAssistantCommandAllowList.DeviceRegistryList, cancellationToken).ConfigureAwait(false);
            return DiscoveryResponseParser.Parse(states, entities, devices, _time.GetUtcNow(), _options.MaxDiscoveredEntities);
        }
        catch (WebSocketMessageTooLargeException ex)
        {
            _logger.LogWarning("Discovery answer from Home Assistant is too large for one message: {Reason}", ex.Message);
            throw new DiscoveryUnavailableException(DiscoveryUnavailableException.TooLarge, ex.Message);
        }
        catch (Exception ex) when (ex is TimeoutException or HomeAssistantQueryFailedException or InvalidOperationException or WebSocketException or IOException)
        {
            // A socket reset while the query is sent (Home Assistant restarting) is "unavailable" too, never a 500.
            throw new DiscoveryUnavailableException(DiscoveryUnavailableException.HomeAssistantUnavailable, $"Home Assistant did not answer the discovery: {ex.Message}");
        }
    }

    public ImmutableArray<CandidateReadout> Lease(IReadOnlyCollection<EntityId> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        EnsureOpen();
        var wanted = entities.Distinct().Take(_options.MaxReadoutEntities).ToList();
        lock (_gate)
        {
            var until = _time.GetUtcNow() + _options.ReadoutLeaseTime;
            foreach (var entity in wanted)
            {
                _leases[entity] = until;
            }

            _store.Touch(wanted, HeldLocked());
            ArmLeaseExpiryLocked();
        }

        UpdateTracked();
        ProbeSilent(wanted);
        var now = _signalTime.GetUtcNow();
        return [.. wanted.Select(e => _store.Read(e, now) ?? new CandidateReadout(e, ReadoutStatus.Pending, null, null, null, null, null, 0, 0))];
    }

    public IDisposable Hold(IReadOnlyCollection<EntityId> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        EnsureOpen();
        var hold = new HoldHandle(this, [.. entities.Distinct()]);
        lock (_gate)
        {
            _holds.Add(hold);
            _store.Touch(hold.Entities, HeldLocked());
        }

        UpdateTracked();
        return hold;
    }

    public CandidateReadout? Peek(EntityId entity) => _store.Read(entity, _signalTime.GetUtcNow());

    public ImmutableArray<EntityUpdate> History(EntityId entity) => _store.History(entity);

    public void SessionStarted()
    {
    }

    public void SessionReady()
    {
    }

    public void Deliver(EntityUpdateBatch batch) => _store.Deliver(batch);

    public void SessionEnded() => _store.SessionEnded();

    public async ValueTask DisposeAsync()
    {
        Session? session;
        lock (_gate)
        {
            _disposed = true;
            session = _session;
            _session = null;
        }

        _idle.Dispose();
        _leaseExpiry.Dispose();
        if (session is not null)
        {
            await session.CloseAsync().ConfigureAwait(false);
        }
    }

    private async Task<JsonElement?> OptionalAsync(Session session, string type, CancellationToken cancellationToken)
    {
        try
        {
            return await session.Client.QueryAsync(type, _options.DiscoveryRequestTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HomeAssistantQueryFailedException or TimeoutException)
        {
            // Without the registry the wizard still offers entities, but cannot tell derived sensors apart and says so.
            _logger.LogWarning("Home Assistant did not answer {Query}: {Reason}", type, ex.Message);
            return null;
        }
    }

    private Session EnsureOpen()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _idle.Change(_options.DiscoveryLinkIdleTimeout, Timeout.InfiniteTimeSpan);
            if (_session is { } open)
            {
                return open;
            }

            var state = new HomeAssistantConnectionState();
            var client = new HomeAssistantClient(_transports, _endpoint, state, _homeAssistant, _time, _loggers.CreateLogger<HomeAssistantClient>(), this);
            var stop = new CancellationTokenSource();
            var session = new Session(client, state, stop, Task.Run(() => client.RunAsync(stop.Token)));
            _session = session;
            _logger.LogInformation("Setup discovery link to Home Assistant opened (read-only queries and subscriptions only)");
            return session;
        }
    }

    private async Task WaitConnectedAsync(Session session, CancellationToken cancellationToken)
    {
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, ConnectionState state)
        {
            if (state == ConnectionState.Connected)
            {
                connected.TrySetResult();
            }
        }

        session.State.Changed += OnChanged;
        try
        {
            if (session.State.Current == ConnectionState.Connected)
            {
                return;
            }

            await connected.Task.WaitAsync(_options.DiscoveryRequestTimeout, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new DiscoveryUnavailableException(
                DiscoveryUnavailableException.HomeAssistantUnavailable,
                $"Home Assistant could not be reached within {_options.DiscoveryRequestTimeout}.");
        }
        finally
        {
            session.State.Changed -= OnChanged;
        }
    }

    /// <summary>The link stays open while anything still needs it: a hold (self-test) or an unexpired lease.</summary>
    private void OnIdle()
    {
        Session? closing;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var now = _time.GetUtcNow();
            if (_holds.Count > 0 || _leases.Values.Any(until => until > now))
            {
                _idle.Change(_options.DiscoveryLinkIdleTimeout, Timeout.InfiniteTimeSpan);
                return;
            }

            closing = _session;
            _session = null;
            _leases.Clear();
            _probedAt.Clear();
        }

        UpdateTracked();
        _store.Clear();
        if (closing is not null)
        {
            _logger.LogInformation("Setup discovery link closed after {Timeout} without setup activity", _options.DiscoveryLinkIdleTimeout);
            _ = closing.CloseAsync();
        }
    }

    private void OnLeaseExpiry()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var gone in _leases.Where(l => l.Value <= now).Select(l => l.Key).ToList())
            {
                _leases.Remove(gone);
            }

            ArmLeaseExpiryLocked();
        }

        UpdateTracked();
    }

    private void ArmLeaseExpiryLocked()
    {
        if (_disposed)
        {
            return;
        }

        if (_leases.Count == 0)
        {
            _leaseExpiry.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        var next = _leases.Values.Min() - _time.GetUtcNow();
        _leaseExpiry.Change(next > TimeSpan.Zero ? next : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private HashSet<EntityId> HeldLocked() => [.. _holds.SelectMany(h => h.Entities)];

    /// <summary>Held entities first, then leased ones, never more than MaxReadoutEntities.</summary>
    private void UpdateTracked()
    {
        ImmutableArray<EntityId> next;
        bool changed;
        lock (_gate)
        {
            var held = _holds.SelectMany(h => h.Entities).Distinct().ToList();
            var leased = _leases.Keys.Where(e => !held.Contains(e)).Order();
            next = [.. held.Concat(leased).Take(_options.MaxReadoutEntities).Order()];
            changed = !next.SequenceEqual(_tracked);
            _tracked = next;
        }

        if (changed)
        {
            TrackedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// A repeated value pushes nothing, so a constant entity would look ever older: one read-only
    /// state read (its last_reported) per ReadoutProbeInterval for a subscribed entity silent that long.
    /// </summary>
    private void ProbeSilent(IEnumerable<EntityId> entities)
    {
        var now = _time.GetUtcNow();
        var signalNow = _signalTime.GetUtcNow();
        foreach (var entity in entities)
        {
            lock (_gate)
            {
                if (_store.LastReported(entity) is not { } reported || signalNow - reported < _options.ReadoutProbeInterval
                    || (_probedAt.TryGetValue(entity, out var probed) && now - probed < _options.ReadoutProbeInterval))
                {
                    continue;
                }

                _probedAt[entity] = now;
            }

            _ = ProbeAsync(entity);
        }
    }

    private async Task ProbeAsync(EntityId entity)
    {
        try
        {
            switch (await _reader.ReadAsync(entity, CancellationToken.None).ConfigureAwait(false))
            {
                case EntityStateRead.Found found:
                    _store.Deliver(new EntityUpdateBatch(BatchOrigin.Probe, [found.Update], [], []));
                    break;
                case EntityStateRead.NotFound:
                    _store.Deliver(new EntityUpdateBatch(BatchOrigin.Probe, [], [], [entity]));
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("State read of candidate {Entity} failed: {Reason}", entity, ex.Message);
        }
    }

    private void Release(HoldHandle hold)
    {
        lock (_gate)
        {
            _holds.Remove(hold);
        }

        UpdateTracked();
    }

    private sealed class HoldHandle(HomeAssistantDiscoveryLink owner, ImmutableArray<EntityId> entities) : IDisposable
    {
        private int _released;

        public ImmutableArray<EntityId> Entities { get; } = entities;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(this);
            }
        }
    }

    private sealed class Session(HomeAssistantClient client, HomeAssistantConnectionState state, CancellationTokenSource stop, Task run)
    {
        public HomeAssistantClient Client { get; } = client;

        public HomeAssistantConnectionState State { get; } = state;

        public async Task CloseAsync()
        {
            try
            {
                await Client.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                await stop.CancelAsync().ConfigureAwait(false);
                await run.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Closing.
            }
            finally
            {
                stop.Dispose();
                Client.Detach();
            }
        }
    }
}
