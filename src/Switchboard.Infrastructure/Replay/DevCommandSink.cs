using System.Collections.Immutable;
using System.Text.Json;
using Switchboard.Application.Configuration;
using Switchboard.Application.Effector;
using Switchboard.Application.Signals;
using Switchboard.Infrastructure.HomeAssistant;

namespace Switchboard.Infrastructure.Replay;

/// <summary>One service call the development sink received, as Home Assistant would have.</summary>
/// <param name="ServiceData">set_value's data ({ value }); null for turn_on and turn_off.</param>
public sealed record SinkEntry(DateTimeOffset At, string Domain, string Service, string EntityId, IReadOnlyDictionary<string, double>? ServiceData);

/// <summary>
/// The command path's channel in standalone development replay: it never reaches a Home Assistant.
/// Every call is serialized and checked exactly as on the real link (the allow-list's shape and the
/// declared actuators), recorded for GET api/dev/sink, handed to the simulated plant if one is
/// loaded, and acknowledged. Refused in add-on mode, like replay itself.
/// </summary>
public sealed class DevCommandSink : ICallServiceChannel
{
    // A development bound on what GET api/dev/sink keeps, not a control parameter.
    private const int MaxEntries = 1000;

    private readonly TimeProvider _signalTime;
    private readonly SimulatedPlant? _plant;
    private readonly object _gate = new();
    private readonly Queue<SinkEntry> _entries = new();
    private int _lastId;

    public DevCommandSink(SignalClock clock, SimulatedPlant? plant = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _signalTime = clock.Time;
        _plant = plant;
    }

    public bool Ready => true;

    /// <summary>One development "link" for the life of the process.</summary>
    public long Generation => 1;

    public ImmutableArray<SinkEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public Task<CallOutcome> SendAsync(ActuatorCall call, Func<EntityId, string, ActuatorService, double?, bool> isDeclared, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        var payload = CallServicePayload.Build(Interlocked.Increment(ref _lastId), call);
        HomeAssistantCommandAllowList.EnsureCallServiceAllowed(payload.Span, isDeclared);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var entry = new SinkEntry(
            _signalTime.GetUtcNow(),
            root.GetProperty("domain").GetString()!,
            root.GetProperty("service").GetString()!,
            root.GetProperty("target").GetProperty("entity_id").GetString()!,
            root.TryGetProperty("service_data", out var data) ? new Dictionary<string, double> { ["value"] = data.GetProperty("value").GetDouble() } : null);
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > MaxEntries)
            {
                _entries.Dequeue();
            }
        }

        _plant?.Apply(call);
        return Task.FromResult(new CallOutcome(CommandOutcome.Acknowledged, null));
    }
}
