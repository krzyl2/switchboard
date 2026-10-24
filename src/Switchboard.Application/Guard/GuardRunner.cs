using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Decisions;
using Switchboard.Application.Effector;
using Switchboard.Application.Modes;
using Switchboard.Application.Signals;

namespace Switchboard.Application.Guard;

/// <summary>One guard step as listeners (state persistence) see it.</summary>
public sealed record GuardStep(GuardEvaluation Evaluation, ImmutableArray<Decision> Decisions, GuardMemory Memory);

/// <summary>
/// The guard's only reader. It wakes on every protection snapshot the signal store publishes, on
/// the kill switch, on its own deadline timer (cycle boundaries, band timers, budget thresholds) and
/// at shutdown; each step evaluates the latest inputs once, passes the result through the effector
/// gate and records the decisions. It runs on the signal clock, so the budget follows the meter's
/// timeline even in development replay. Nothing here can reach Home Assistant: the gate's output
/// is a decision record, and the recorder only queues it.
/// </summary>
public sealed class GuardRunner : IDisposable
{
    private readonly GuardInbox _inbox;
    private readonly IProtectionInputSource _protection;
    private readonly TimeProvider _time;
    private readonly GuardEngine _engine;
    private readonly EffectorGate _gate;
    private readonly LockoutClocks _lockouts;
    private readonly IDecisionRecorder _recorder;
    private readonly GuardStatusHolder _holder;
    private readonly IModeSource _mode;
    private readonly IKillSwitchView _killSwitch;
    private readonly ILogger<GuardRunner> _logger;
    private readonly object _sync = new();
    private DateTimeOffset? _lastStep;
    private bool _started;
    private bool _recorderFailing;

    public GuardRunner(
        GuardInbox inbox,
        IProtectionInputSource protection,
        SignalClock clock,
        GuardEngine engine,
        EffectorGate gate,
        LockoutClocks lockouts,
        IDecisionRecorder recorder,
        GuardStatusHolder holder,
        IModeSource mode,
        IKillSwitchView killSwitch,
        ILogger<GuardRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(clock);
        _inbox = inbox;
        _protection = protection;
        _time = clock.Time;
        _engine = engine;
        _gate = gate;
        _lockouts = lockouts;
        _recorder = recorder;
        _holder = holder;
        _mode = mode;
        _killSwitch = killSwitch;
        _logger = logger;
        _protection.Published += OnPublished;
    }

    /// <summary>Raised after every step, on the guard's thread; handlers must not block.</summary>
    public event EventHandler<GuardStep>? Stepped;

    /// <summary>Applies what state.json held at the last stop; call before the first step.</summary>
    public void Restore(GuardMemory memory) => _engine.Restore(memory, _time.GetUtcNow());

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = _time.CreateTimer(_ => _inbox.Post(GuardTrigger.Tick), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        try
        {
            Arm(timer, Step());
            while (await _inbox.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                Arm(timer, Step());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown; the shutdown safe state is recorded by ShutdownAsync.
        }
    }

    /// <summary>
    /// Processes everything queued now as one evaluation; returns its time. The first step records
    /// the mode decision of this start. Public for the replay harness and tests.
    /// </summary>
    public DateTimeOffset Step()
    {
        lock (_sync)
        {
            var now = _time.GetUtcNow();
            var mode = _mode.Current;
            var decisions = ImmutableArray.CreateBuilder<Decision>();
            if (!_started)
            {
                _started = true;
                decisions.Add(_gate.Start(mode, now));
            }

            var trigger = GuardTrigger.Snapshot;
            while (_inbox.Reader.TryRead(out var queued))
            {
                trigger = trigger == GuardTrigger.Shutdown ? trigger : queued;
            }

            if (_lastStep is { } last && now < last)
            {
                // SAFE-07 on a clock step as at start: every lockout restarts from now.
                _lockouts.RestartAll(now);
            }

            _lastStep = now;
            var protection = _protection.Current;
            _lockouts.Observe(protection.Setup ?? _engine.Setup, protection.DeviceStates, now);
            var evaluation = _engine.Evaluate(new GuardInput(trigger, protection, _killSwitch.Status, mode), now);
            decisions.AddRange(_gate.Apply(evaluation, mode, now));
            Record(decisions.ToImmutable());
            _holder.Publish(evaluation.Status);
            Notify(new GuardStep(evaluation, decisions.ToImmutable(), _engine.Export()));
            return now;
        }
    }

    /// <summary>
    /// The graceful-stop path of SAFE-01: evaluates the shutdown safe state (every controlled device
    /// off, recorded) at once, on the caller's thread, whether or not the run loop is still alive.
    /// </summary>
    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _inbox.Post(GuardTrigger.Shutdown);
        Step();
        return Task.CompletedTask;
    }

    public void Dispose() => _protection.Published -= OnPublished;

    private void OnPublished(object? sender, Domain.Signals.ProtectionInputs inputs) => _inbox.Post(GuardTrigger.Snapshot);

    private void Arm(ITimer timer, DateTimeOffset now)
    {
        var deadline = _engine.NextDeadline(now);
        timer.Change(deadline is { } due ? SignalStoreRunner.DueTime(due - now) : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void Record(ImmutableArray<Decision> decisions)
    {
        if (decisions.IsEmpty)
        {
            return;
        }

        try
        {
            _recorder.Record(decisions);
            _recorderFailing = false;
        }
        catch (Exception ex)
        {
            // A broken recorder must never stop protection; it reports itself through its status.
            if (!_recorderFailing)
            {
                _logger.LogError(ex, "Recording {Count} decisions failed; the guard keeps running", decisions.Length);
                _recorderFailing = true;
            }
        }
    }

    private void Notify(GuardStep step)
    {
        foreach (var handler in Stepped?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler<GuardStep>)handler)(this, step);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A guard step listener failed; the guard keeps running");
            }
        }
    }
}
