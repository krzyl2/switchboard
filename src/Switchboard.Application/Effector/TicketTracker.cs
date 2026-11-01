using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Decisions;
using Switchboard.Application.Signals;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;

namespace Switchboard.Application.Effector;

/// <summary>
/// The commands the gate has sent and not yet seen through (03 EffectorGate): each ticket with its
/// device and what to go back to, the decisions waiting for their commands' outcomes, and the ones
/// completed and waiting to be handed to the step. It turns an outcome into what it means for the
/// device's ledger (confirmation, a failed increase taken back, a protective target marked for
/// re-sending) and a completed decision's "commanded". It never sends: the port stays with the
/// gate. Single-threaded, on the guard runner's thread.
/// </summary>
internal sealed class TicketTracker
{
    private readonly DeviceTargets _targets;
    private readonly LockoutClocks _lockouts;
    private readonly ILogger _logger;
    private readonly Dictionary<long, TicketInfo> _tickets = [];
    private readonly List<PendingDecision> _pending = [];
    private readonly List<Decision> _ready = [];

    public TicketTracker(DeviceTargets targets, LockoutClocks lockouts, ILogger logger)
    {
        _targets = targets;
        _lockouts = lockouts;
        _logger = logger;
    }

    /// <summary>Decisions waiting for the outcome of their commands.</summary>
    public int PendingCount => _pending.Count;

    public void Track(CommandTicket ticket, DeviceName device, CommandPriority priority, bool increase, DeviceLevel? from, DecisionOrigin? previousOrigin, ReasonCode? previousReason) =>
        _tickets[ticket.Sequence] = new TicketInfo(device, priority, increase, from, previousOrigin, previousReason);

    /// <summary>Holds a decision until every command it carries has an outcome; <paramref name="resolved"/> runs then with whether all were acknowledged.</summary>
    public void Pend(Decision decision, IReadOnlyList<CommandTicket> tickets, Action<bool>? resolved)
    {
        var pending = new PendingDecision(decision, [.. tickets.Select(t => t.Sequence)], resolved);
        _pending.Add(pending);
    }

    /// <summary>Decisions that completed (every command answered) and wait to be handed to the step.</summary>
    public void MoveReady(ImmutableArray<Decision>.Builder to)
    {
        to.AddRange(_ready);
        _ready.Clear();
    }

    public ImmutableArray<Decision> TakeReady()
    {
        var ready = _ready.ToImmutableArray();
        _ready.Clear();
        return ready;
    }

    /// <summary>
    /// One outcome that arrived: the decisions waiting for it learn it, and the device its command
    /// belongs to learns what happened to its current target's command.
    /// </summary>
    public void Take(CommandResult result, ControlInputs? inputs, DateTimeOffset now)
    {
        foreach (var pending in _pending.Where(p => p.Tickets.Contains(result.Ticket.Sequence)))
        {
            pending.Results[result.Ticket.Sequence] = result;
        }

        if (_tickets.Remove(result.Ticket.Sequence, out var info) && _targets.TryGet(info.Device, out var state) && state.Commands.OpenTickets.Remove(result.Ticket.Sequence))
        {
            Learn(state, info, result, inputs, now);
        }
    }

    /// <summary>The decisions whose every command has an outcome are completed, in order.</summary>
    public void CompleteAnswered()
    {
        foreach (var pending in _pending.Where(p => p.Tickets.All(p.Results.ContainsKey)).ToList())
        {
            Resolve(pending);
        }
    }

    /// <summary>The shutdown path: a decision still waiting is completed with its unanswered commands timed out.</summary>
    public void TimeOutUnanswered(DateTimeOffset now)
    {
        foreach (var pending in _pending.ToList())
        {
            foreach (var ticket in pending.Tickets.Where(t => !pending.Results.ContainsKey(t)).ToList())
            {
                pending.Results[ticket] = new CommandResult(new CommandTicket(ticket), pending.Draft.Device!.Value, CommandOutcome.TimedOut, null, now, "no answer before the add-on stopped");
            }

            Resolve(pending);
        }
    }

    /// <summary>
    /// What a command's outcome means for its device. Acknowledged: the reported state must show the
    /// target by ack + reaction time + effector.confirm_timeout. An error counts one mismatch; a
    /// timeout or a closed link counts nothing by itself (confirmation decides). Anything but an
    /// acknowledgement ends the window's relief at once (the guard then judges the measured current
    /// and takes its next candidate), takes a failed ordinary increase back (dispatch plans it again
    /// through every gate), and marks a failed protective or lowering target for re-sending at each
    /// boundary.
    /// </summary>
    private void Learn(DeviceTargets.State state, TicketInfo info, CommandResult result, ControlInputs? inputs, DateTimeOffset now)
    {
        if (result.Outcome == CommandOutcome.Acknowledged)
        {
            if (state.Baselines.BaselinePending is not null)
            {
                state.Baselines.PendingAckedAt ??= now;
            }

            if (state.Commands.OpenTickets.Count == 0 && state.Spec is { StateReportsOnChange: true } spec)
            {
                var confirm = inputs?.DispatchSetup?.Settling.ConfirmTimeout ?? TimeSpan.Zero;
                state.Commands.ConfirmBy = now + spec.ReactionTime + confirm;
            }

            return;
        }

        if (result is { Outcome: CommandOutcome.NotSent, NotSent: CommandNotSentReason.Superseded or CommandNotSentReason.ControlOff })
        {
            return;
        }

        if (result is { Outcome: CommandOutcome.NotSent, NotSent: CommandNotSentReason.NotDeclared or CommandNotSentReason.OwnEntity })
        {
            // The detail says why: not a declared actuator of the configuration loaded now, an
            // option the entity does not offer, or one of Switchboard's own entities.
            _logger.LogError("A command for {Device} was refused at send time: {Detail}", info.Device, result.Detail);
        }
        else if (result.Outcome != CommandOutcome.NotSent)
        {
            _logger.LogWarning("Command for {Device} {Outcome}: {Detail}", info.Device, result.Outcome, result.Detail);
        }

        state.Resolved.TargetCommanded = false;
        if (state.Settling.Window is { Kind: WindowKind.Command, Open: true } window)
        {
            state.Settling.Window = window with { Applied = false, NotDelivered = true, Outcome = SettlingState.Closed };
        }

        if (result.Outcome == CommandOutcome.Failed)
        {
            state.Settling.PendingMismatch = true;
        }
        else if (result.Outcome == CommandOutcome.TimedOut && state.Spec is { StateReportsOnChange: true } spec)
        {
            var confirm = inputs?.DispatchSetup?.Settling.ConfirmTimeout ?? TimeSpan.Zero;
            state.Commands.ConfirmBy = now + spec.ReactionTime + confirm;
        }

        if (info.Priority == CommandPriority.Protective || !info.Increase)
        {
            // A lowering target the device may not show (an off, a lower setpoint, the self-test's
            // off) keeps being sent: the target stays where it is, so a later hold at the same level
            // would otherwise change nothing and never leave.
            state.Commands.ProtectiveFailed = result.Outcome != CommandOutcome.NotSent;
            return;
        }

        if (info.Increase && state.Spec is { } device)
        {
            // A phantom increase would make dispatch believe the device is already there.
            var back = info.From ?? DeviceLevels.Off(device);
            foreach (var stage in device.Stages)
            {
                if (state.Resolved.Target is { } target && DeviceLevels.StageOn(device, target, stage.Index) != DeviceLevels.StageOn(device, back, stage.Index))
                {
                    _lockouts.Commanded(device.Id, stage.Index, DeviceLevels.StageOn(device, back, stage.Index), now);
                }
            }

            state.Resolved.Target = back;
            state.Resolved.Origin = info.PreviousOrigin;
            state.Resolved.Reason = info.PreviousReason;
            state.Commands.ConfirmBy = null;
        }
    }

    private void Resolve(PendingDecision pending)
    {
        _pending.Remove(pending);
        var results = pending.Tickets.Select(t => pending.Results[t]).ToList();
        var acknowledged = results.All(r => r.Outcome == CommandOutcome.Acknowledged);
        ReasonCode? refusedBy = acknowledged
            ? null
            : results.First(r => r.Outcome != CommandOutcome.Acknowledged) switch
            {
                // Home Assistant answered with an error or not at all (list version 5; versions 1 to 4
                // used device_unresponsive, which now means only a device that does not react).
                { Outcome: CommandOutcome.Failed or CommandOutcome.Rejected or CommandOutcome.TimedOut } => ReasonCode.CommandFailed,
                { NotSent: CommandNotSentReason.ControlOff } => ReasonCode.ModeShadow,

                // The command never left: no link, not a declared actuator (or one of Switchboard's own),
                // a device re-pointed at another entity, or a newer command for the same actuator replaced
                // it (list version 5; versions 1 to 4 used safe_state, and change_limit means an increase
                // waiting for another in flight).
                _ => ReasonCode.CommandNotSent,
            };
        _ready.Add(pending.Draft.WithCommand(acknowledged, refusedBy));
        pending.Resolved?.Invoke(acknowledged);
    }

    /// <summary>A submitted command: its device, priority, and what to go back to if an increase fails.</summary>
    private sealed record TicketInfo(DeviceName Device, CommandPriority Priority, bool Increase, DeviceLevel? From, DecisionOrigin? PreviousOrigin, ReasonCode? PreviousReason);

    /// <summary>A decision waiting for the outcomes of its commands.</summary>
    private sealed class PendingDecision(Decision draft, HashSet<long> tickets, Action<bool>? resolved)
    {
        public Decision Draft { get; } = draft;

        public HashSet<long> Tickets { get; } = tickets;

        public Dictionary<long, CommandResult> Results { get; } = [];

        public Action<bool>? Resolved { get; } = resolved;
    }
}
