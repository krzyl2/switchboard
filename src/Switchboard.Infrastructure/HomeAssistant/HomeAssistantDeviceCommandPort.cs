using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Switchboard.Application.Configuration;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Options;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;

namespace Switchboard.Infrastructure.HomeAssistant;

/// <summary>
/// The only command port (SAFE-05): the effector gate's commands become Home Assistant service
/// calls here, and nowhere else.
/// <list type="bullet">
/// <item>One slot per actuator (device, kind, stage): a newer command replaces one not sent yet,
/// whatever its priority (the older one is answered superseded), so a stale "on" can never follow a
/// protective "off" for the same relay, and the queue never holds more than one command per
/// actuator. Priority only orders different actuators: protective first.</item>
/// <item>At most one command per actuator in flight; different actuators are pipelined (sent
/// without waiting for each other's answer), each with its own timeout on the injected clock, so
/// an unresponsive entity never delays another device's protective off.</item>
/// <item>At send time, the mode is checked again (control off: nothing leaves; an invalid
/// configuration: decreases only) and the command is resolved through the configuration loaded now;
/// the serialized call is checked once more by the allow-list against the declared actuators. A
/// device re-pointed at another entity since the command was decided is not redirected. A command
/// to one of Switchboard's own MQTT entities is refused (answered own_entity), before the call is
/// built and again by the allow-list check, whatever the configuration declares.</item>
/// <item>Switching control off drops every queued command at once (answered control_off), before
/// the guard is woken.</item>
/// </list>
/// Outcomes go to a queue the gate drains, and wake the guard.
/// </summary>
public sealed class HomeAssistantDeviceCommandPort : IDeviceCommandPort, ICommandPathStatus
{
    private readonly ICallServiceChannel _channel;
    private readonly IActuatorDirectory _directory;
    private readonly IModeSource _mode;
    private readonly IOwnEntityCheck? _ownEntities;
    private readonly GuardInbox _inbox;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _time;
    private readonly ILogger<HomeAssistantDeviceCommandPort> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<Slot, Queued> _queued = [];
    private readonly HashSet<Slot> _inFlight = [];
    private readonly ConcurrentQueue<CommandResult> _results = new();
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private long _sequence;
    private DateTimeOffset? _lastAcknowledged;
    private CommandResult? _lastFailure;
    private CancellationToken _stopping;
    private TaskCompletionSource? _idle;
    private TaskCompletionSource? _passed;

    public HomeAssistantDeviceCommandPort(
        ICallServiceChannel channel,
        IActuatorDirectory directory,
        IModeSource mode,
        GuardInbox inbox,
        IOptions<HomeAssistantOptions> options,
        TimeProvider time,
        ILogger<HomeAssistantDeviceCommandPort> logger,
        ControlSwitchState? control = null,
        IOwnEntityCheck? ownEntities = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _channel = channel;
        _directory = directory;
        _mode = mode;
        _inbox = inbox;
        _timeout = options.Value.CommandTimeout;
        _time = time;
        _logger = logger;
        _ownEntities = ownEntities;
        if (control is not null)
        {
            control.Closing += (_, _) => Close();
        }
    }

    public long LinkGeneration => _channel.Generation;

    public CommandPathStatus Status
    {
        get
        {
            lock (_gate)
            {
                return new CommandPathStatus(_channel.Ready, _queued.Count + _inFlight.Count, _lastAcknowledged, _lastFailure);
            }
        }
    }

    public CommandTicket Submit(DeviceCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            var ticket = new CommandTicket(++_sequence);
            if (!Open(command))
            {
                Complete(ticket, command.Device, CommandOutcome.NotSent, CommandNotSentReason.ControlOff, "control is not allowed");
                return ticket;
            }

            var slot = new Slot(command.Device, command.Kind, command.Stage);
            if (_queued.Remove(slot, out var older))
            {
                // Newest wins for one actuator: the older command never leaves.
                Complete(older.Ticket, older.Command.Device, CommandOutcome.NotSent, CommandNotSentReason.Superseded, "a newer command for the same actuator replaced it");
            }

            _queued[slot] = new Queued(ticket, command);
            return ticket;
        }
    }

    public void Flush() => _wake.Writer.TryWrite(true);

    public bool TryTakeResult(out CommandResult result) => _results.TryDequeue(out result!);

    /// <summary>The sender: runs for the life of the process (the command path's hosted service).</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _stopping = cancellationToken;
        try
        {
            while (await _wake.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                _wake.Reader.TryRead(out _);
                SendReady();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping: the guard's shutdown already waited for its commands (WhenIdleAsync).
        }
    }

    /// <summary>
    /// The shutdown path: waits until nothing is queued or in flight, at most <paramref name="limit"/>,
    /// so the shutdown safe state's offs leave before the link closes.
    /// </summary>
    public async Task WhenIdleAsync(TimeSpan limit, CancellationToken cancellationToken)
    {
        Task idle;
        lock (_gate)
        {
            if (_queued.Count == 0 && _inFlight.Count == 0)
            {
                return;
            }

            _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            idle = _idle.Task;
        }

        Flush();
        try
        {
            await idle.WaitAsync(limit, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Commands were still waiting for Home Assistant when the add-on stopped");
        }
    }

    /// <summary>Completes once a send pass that started after this call has handed out its calls: tests prove "nothing more left" without sleeping.</summary>
    internal Task NextSendPassAsync()
    {
        lock (_gate)
        {
            _passed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _passed.Task;
        }
    }

    /// <summary>Control was switched off (or locked): everything not sent yet is dropped at once.</summary>
    private void Close()
    {
        lock (_gate)
        {
            foreach (var (slot, queued) in _queued.Where(q => !(q.Value.Command.Final && q.Value.Command.Decrease)).ToList())
            {
                _queued.Remove(slot);
                Complete(queued.Ticket, queued.Command.Device, CommandOutcome.NotSent, CommandNotSentReason.ControlOff, "control was switched off before it was sent");
            }

            SignalIdle();
        }
    }

    /// <summary>Commands may leave: every one in active mode, decreases only during an invalid configuration, and the self-test's own final off.</summary>
    private bool Open(DeviceCommand command)
    {
        if (command.Final && command.Decrease)
        {
            return true;
        }

        var mode = _mode.Current;
        return mode.Commands && (!mode.DecreasesOnly || command.Decrease);
    }

    private void SendReady()
    {
        List<(Slot Slot, Queued Queued, ActuatorCall Call)> sending = [];
        lock (_gate)
        {
            List<(Slot Slot, Queued Queued)> waiting = [];
            foreach (var (slot, queued) in _queued
                .Where(q => !_inFlight.Contains(q.Key))
                .OrderBy(q => q.Value.Command.Priority)
                .ThenBy(q => q.Value.Ticket.Sequence)
                .ToList())
            {
                _queued.Remove(slot);
                var command = queued.Command;
                if (!Open(command))
                {
                    Complete(queued.Ticket, command.Device, CommandOutcome.NotSent, CommandNotSentReason.ControlOff, "control is not allowed");
                    continue;
                }

                // The link first: a command that cannot leave never uses up a removed device's
                // single-use final off. That final off has no other chance (the device left the
                // ledger), so it waits in its slot for the link; anything else is answered at once
                // and re-asserted by the gate when the link is back.
                if (!_channel.Ready)
                {
                    if (command.Retiring)
                    {
                        waiting.Add((slot, queued));
                    }
                    else
                    {
                        Complete(queued.Ticket, command.Device, CommandOutcome.NotSent, CommandNotSentReason.LinkDown, "Home Assistant is not connected");
                    }

                    continue;
                }

                var resolution = _directory.Resolve(command);
                if (resolution.Call is not { } call)
                {
                    var reason = resolution.Refusal == ActuatorRefusal.Superseded ? CommandNotSentReason.Superseded : CommandNotSentReason.NotDeclared;
                    Complete(queued.Ticket, command.Device, CommandOutcome.NotSent, reason, resolution.Detail);
                    continue;
                }

                if (IsOwn(call.Entity))
                {
                    // Looping back into Switchboard instead of reaching the device: never sent.
                    _logger.LogError("A command for {Device} was refused: {Entity} is one of Switchboard's own MQTT entities", command.Device, call.Entity);
                    Complete(queued.Ticket, command.Device, CommandOutcome.NotSent, CommandNotSentReason.OwnEntity, $"{call.Entity} is one of Switchboard's own MQTT entities and is never commanded");
                    continue;
                }

                _inFlight.Add(slot);
                sending.Add((slot, queued, call));
            }

            foreach (var (slot, queued) in waiting)
            {
                _queued[slot] = queued;
            }

            SignalIdle();
        }

        foreach (var (slot, queued, call) in sending)
        {
            _ = SendOneAsync(slot, queued, call);
        }

        lock (_gate)
        {
            if (_passed is { } passed)
            {
                _passed = null;
                passed.TrySetResult();
            }
        }
    }

    private async Task SendOneAsync(Slot slot, Queued queued, ActuatorCall call)
    {
        CallOutcome outcome;
        try
        {
            outcome = await _channel.SendAsync(call, DeclaredAndNotOwn, _timeout, _stopping).ConfigureAwait(false);
        }
        catch (HomeAssistantCommandRefusedException ex)
        {
            _logger.LogError("A command for {Device} was refused by the outbound allow-list: {Reason}", queued.Command.Device, ex.Message);
            outcome = new CallOutcome(CommandOutcome.NotSent, ex.Message);
            lock (_gate)
            {
                _inFlight.Remove(slot);
                Complete(queued.Ticket, queued.Command.Device, CommandOutcome.NotSent, CommandNotSentReason.NotDeclared, ex.Message);
                SignalIdle();
            }

            Flush();
            return;
        }
        catch (OperationCanceledException)
        {
            outcome = new CallOutcome(CommandOutcome.TimedOut, "the add-on stopped before the answer");
        }

        lock (_gate)
        {
            _inFlight.Remove(slot);
            var notSent = outcome.Outcome == CommandOutcome.NotSent ? CommandNotSentReason.LinkDown : (CommandNotSentReason?)null;
            Complete(queued.Ticket, queued.Command.Device, outcome.Outcome, notSent, outcome.Detail);
            SignalIdle();
        }

        // The next queued command of this actuator (if any) may leave now.
        Flush();
    }

    private bool IsOwn(EntityId entity) => _ownEntities?.IsOwn(entity) == true;

    /// <summary>The allow-list's second check: a declared actuator of the configuration loaded now, and not one of Switchboard's own entities.</summary>
    private bool DeclaredAndNotOwn(EntityId entity, string domain, ActuatorService service, double? value, string? option) =>
        !IsOwn(entity) && _directory.IsDeclared(entity, domain, service, value, option);

    /// <summary>One outcome: queued for the gate, the status updated, the guard woken. Called under the lock.</summary>
    private void Complete(CommandTicket ticket, DeviceName device, CommandOutcome outcome, CommandNotSentReason? notSent, string? detail)
    {
        var result = new CommandResult(ticket, device, outcome, notSent, _time.GetUtcNow(), detail);
        if (outcome == CommandOutcome.Acknowledged)
        {
            _lastAcknowledged = result.At;
        }
        else if (notSent is not (CommandNotSentReason.Superseded or CommandNotSentReason.ControlOff))
        {
            _lastFailure = result;
        }

        _results.Enqueue(result);
        _inbox.Post(GuardTrigger.Command);
    }

    /// <summary>Wakes a shutdown waiting for an idle command path. Called under the lock.</summary>
    private void SignalIdle()
    {
        if (_queued.Count == 0 && _inFlight.Count == 0 && _idle is { } idle)
        {
            _idle = null;
            idle.TrySetResult();
        }
    }

    private readonly record struct Slot(DeviceName Device, DeviceCommandKind Kind, int Stage);

    private sealed record Queued(CommandTicket Ticket, DeviceCommand Command);
}
