using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Configuration;

namespace Switchboard.Application.Signals;

/// <summary>
/// The store's only reader. Each step drains everything queued (a burst of samples from one meter
/// poll yields one snapshot), applies it in order, publishes one immutable snapshot, then tells the
/// source what to track and what to probe, and arms one timer at the store's next deadline.
/// </summary>
public sealed class SignalStoreRunner
{
    private readonly SignalInbox _inbox;
    private readonly SignalStore _store;
    private readonly ISignalSource _source;
    private readonly SignalSnapshotHolder _holder;
    private readonly TimeProvider _time;
    private readonly ILogger<SignalStoreRunner> _logger;
    private readonly ActuatorDirectoryHolder? _actuators;
    private ImmutableHashSet<string> _loggedWarnings = [];
    private ImmutableDictionary<EntityId, ImmutableArray<string>> _options = ImmutableDictionary<EntityId, ImmutableArray<string>>.Empty;
    private DateTimeOffset? _lastStep;
    private (SwitchboardConfiguration? Configuration, ImmutableDictionary<Domain.Signals.SignalName, EntityId> Entities) _entities =
        (null, ImmutableDictionary<Domain.Signals.SignalName, EntityId>.Empty);

    public SignalStoreRunner(
        SignalInbox inbox,
        SignalStore store,
        ISignalSource source,
        SignalSnapshotHolder holder,
        SignalClock clock,
        ILogger<SignalStoreRunner> logger,
        ActuatorDirectoryHolder? actuators = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _actuators = actuators;
        _inbox = inbox;
        _store = store;
        _source = source;
        _holder = holder;
        _time = clock.Time;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = _time.CreateTimer(_ => _inbox.PostTick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        try
        {
            Publish(_time.GetUtcNow());
            while (await _inbox.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var now = Step();
                var deadline = _store.NextDeadline(now);
                timer.Change(deadline is { } due ? DueTime(due - now) : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// The longest due time a platform timer accepts (System.Threading.Timer: 2^32 − 2 ms). A platform
    /// limit, not a control parameter; a later deadline is reached by waking early and re-arming.
    /// </summary>
    public static TimeSpan LongestTimerDue { get; } = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>Processes everything queued now, as one cycle; returns the cycle's time. Public for the replay harness.</summary>
    public DateTimeOffset Step()
    {
        var now = _time.GetUtcNow();
        var steppedBack = _lastStep is { } last && now < last ? last - now : (TimeSpan?)null;
        _lastStep = now;
        while (_inbox.Reader.TryRead(out var input))
        {
            switch (input)
            {
                case ConfigurationInput configuration:
                    _store.ApplyConfiguration(configuration.Result, now);
                    break;
                case SourceStatusInput status:
                    _store.ApplySourceStatus(status.Status);
                    break;
                case BatchInput batch:
                    _store.Apply(batch.Batch, now);
                    break;
            }
        }

        // After the queue is drained: what was queued carries report times from before the step.
        if (steppedBack is { } step)
        {
            _logger.LogWarning("The clock stepped back by {Step}; every signal is read again", step);
            _store.ForgetSamplesAfterClockStep();
        }

        Publish(now);
        if (_store.TryTakeTrackedChange(out var tracked))
        {
            _source.Track(tracked);
        }

        foreach (var entity in _store.TakeDueProbes(now))
        {
            _source.RequestProbe(entity);
        }

        return now;
    }

    internal static TimeSpan DueTime(TimeSpan untilDeadline) => untilDeadline < LongestTimerDue ? untilDeadline : LongestTimerDue;

    private void Publish(DateTimeOffset now)
    {
        var configuration = _store.ConfigurationResult?.Configuration;

        // The command path's actuators follow the same reload as the protection setup, published
        // before the snapshot so a command decided on the new setup is resolved against it; so do
        // the options each select reports, for the send-time options check.
        _actuators?.Observe(configuration);
        var options = _store.ReportedOptions();
        if (!SameOptions(options, _options))
        {
            _options = options;
            _actuators?.ObserveOptions(options);
        }
        if (!ReferenceEquals(configuration, _entities.Configuration))
        {
            _entities = (configuration, configuration is null
                ? ImmutableDictionary<Domain.Signals.SignalName, EntityId>.Empty
                : configuration.Signals.ToImmutableDictionary(s => s.Name, s => s.Entity));
        }

        var warnings = _store.RuntimeWarnings();
        LogNewWarnings(warnings);
        _holder.Publish(new SignalDiagnostics(_store.Snapshot(now), _store.ConfigurationResult, _entities.Entities, warnings));
    }

    private static bool SameOptions(ImmutableDictionary<EntityId, ImmutableArray<string>> a, ImmutableDictionary<EntityId, ImmutableArray<string>> b) =>
        a.Count == b.Count && a.All(entry => b.TryGetValue(entry.Key, out var other) && entry.Value.SequenceEqual(other, StringComparer.Ordinal));

    private void LogNewWarnings(ImmutableArray<ConfigurationIssue> warnings)
    {
        var current = warnings.Select(w => w.Path).ToImmutableHashSet();
        foreach (var warning in warnings.Where(w => !_loggedWarnings.Contains(w.Path)))
        {
            _logger.LogWarning("Configuration: {Issue}", warning);
        }

        _loggedWarnings = current;
    }
}
