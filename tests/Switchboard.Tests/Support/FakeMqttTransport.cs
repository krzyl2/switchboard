using System.Threading.Channels;
using Switchboard.Infrastructure.Mqtt;

namespace Switchboard.Tests.Support;

internal sealed record MqttOperation(string Kind, string Topic = "", string Payload = "", bool Retain = false)
{
    public override string ToString() => Kind switch
    {
        "publish" => $"publish {Topic} {Payload}{(Retain ? " retained" : string.Empty)}",
        "subscribe" => $"subscribe {Topic}",
        _ => Kind,
    };
}

/// <summary>
/// Broker stand-in that records every operation in order. It enforces the real topic guard, so a
/// test fails if the code under test ever tries to publish outside the add-on's namespace.
/// </summary>
internal sealed class FakeMqttTransport(MqttTopicGuard guard) : IMqttTransport
{
    private readonly Channel<MqttOperation> _operations = Channel.CreateUnbounded<MqttOperation>();
    private readonly object _gate = new();
    private readonly List<MqttOperation> _log = [];
    private readonly List<MqttConnectSettings> _connects = [];

    public event Func<Task>? Disconnected;

    public event Func<string, string, Task>? MessageReceived;

    public bool IsConnected { get; private set; }

    /// <summary>When set, connection attempts fail.</summary>
    public bool FailConnects { get; set; }

    /// <summary>When set, the broker accepts the connection but refuses publishes to this topic (e.g. an ACL).</summary>
    public string? RefusedTopic { get; set; }

    /// <summary>When set, publishes are never acknowledged; they end only when cancelled.</summary>
    public bool HoldPublishes { get; set; }

    public IReadOnlyList<MqttConnectSettings> Connects
    {
        get
        {
            lock (_gate)
            {
                return [.. _connects];
            }
        }
    }

    public IReadOnlyList<MqttOperation> Log
    {
        get
        {
            lock (_gate)
            {
                return [.. _log];
            }
        }
    }

    public Task ConnectAsync(MqttConnectSettings settings, CancellationToken cancellationToken)
    {
        guard.EnsureAllowed(settings.WillTopic);

        // As MQTTnet does: a client that is still connected refuses another connect.
        if (IsConnected)
        {
            Record(new MqttOperation("connect-refused"));
            return Task.FromException(new InvalidOperationException("not allowed to connect while connected"));
        }

        lock (_gate)
        {
            _connects.Add(settings);
        }

        if (FailConnects)
        {
            Record(new MqttOperation("connect-failed"));
            return Task.FromException(new InvalidOperationException("scripted connect failure"));
        }

        IsConnected = true;
        Record(new MqttOperation("connect"));
        return Task.CompletedTask;
    }

    public Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken)
    {
        guard.EnsureAllowed(topic);
        if (!IsConnected)
        {
            return Task.FromException(new InvalidOperationException("not connected"));
        }

        if (topic == RefusedTopic)
        {
            // The client stays connected, as it does after a PUBACK with an error reason code.
            return Task.FromException(new InvalidOperationException($"Broker refused the publish to '{topic}': NotAuthorized."));
        }

        if (HoldPublishes)
        {
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));
            return pending.Task;
        }

        Record(new MqttOperation("publish", topic, payload, retain));
        return Task.CompletedTask;
    }

    public Task SubscribeAsync(string topicFilter, CancellationToken cancellationToken)
    {
        Record(new MqttOperation("subscribe", topicFilter));
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        IsConnected = false;
        Record(new MqttOperation("disconnect"));

        // MQTTnet raises its disconnected event for a clean disconnect too.
        return Disconnected?.Invoke() ?? Task.CompletedTask;
    }

    /// <summary>Simulates the broker dropping the connection.</summary>
    public Task DropAsync()
    {
        IsConnected = false;
        return Disconnected?.Invoke() ?? Task.CompletedTask;
    }

    public Task DeliverAsync(string topic, string payload) => MessageReceived?.Invoke(topic, payload) ?? Task.CompletedTask;

    public async Task<MqttOperation> NextAsync() => await _operations.Reader.ReadAsync().Soon();

    /// <summary>Reads operations until one matches, returning everything read on the way (inclusive).</summary>
    public async Task<List<MqttOperation>> UntilAsync(Func<MqttOperation, bool> match)
    {
        var seen = new List<MqttOperation>();
        while (true)
        {
            var next = await NextAsync();
            seen.Add(next);
            if (match(next))
            {
                return seen;
            }
        }
    }

    private void Record(MqttOperation operation)
    {
        lock (_gate)
        {
            _log.Add(operation);
        }

        _operations.Writer.TryWrite(operation);
    }
}

internal sealed class FakeCredentialSource : IMqttCredentialSource
{
    private int _calls;

    public string? ConfigurationProblem { get; init; }

    public MqttCredentials Credentials { get; init; } = new("broker.test", 1883, useTls: false, "addons", "s3cret-mqtt-password");

    public int Calls => Volatile.Read(ref _calls);

    public Task<MqttCredentials> GetAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(Credentials);
    }
}
