using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Dispatch;

/// <summary>Holds the latest dispatch status; written by the guard runner, read by anyone without locking.</summary>
public sealed class DispatchStatusHolder : IDispatchStatusSource
{
    private readonly ILogger<DispatchStatusHolder> _logger;
    private DispatchStatus _current;

    public DispatchStatusHolder(DispatchStatus initial, ILogger<DispatchStatusHolder> logger)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
        _logger = logger;
    }

    public event EventHandler<DispatchStatus>? Published;

    public DispatchStatus Current => Volatile.Read(ref _current);

    /// <summary>Publishes on the guard runner's thread; a failing listener is logged and skipped.</summary>
    public void Publish(DispatchStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        Volatile.Write(ref _current, status);
        foreach (var handler in Published?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler<DispatchStatus>)handler)(this, status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A dispatch status listener failed; the guard keeps running");
            }
        }
    }
}

/// <summary>Assembles the dispatch status of one step from the guard's envelope, dispatch's evaluation and the effector's ledger.</summary>
public static class DispatchStatusFactory
{
    public static DispatchStatus Build(
        GuardEvaluation guard,
        DispatchEvaluation dispatch,
        IDispatchLedgerView ledger,
        OverrideSnapshot overrides,
        IReadOnlyLockouts lockouts,
        ControlInputs inputs,
        ModeStatus mode,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentNullException.ThrowIfNull(lockouts);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(mode);
        if (inputs.DispatchSetup is not { } setup || guard.Setup is not { } protection)
        {
            return new DispatchStatus(now, mode, mode.Commands, [], []);
        }

        var envelope = guard.Envelope;
        var phases = setup.Phases.Select(p =>
        {
            var main = envelope.For(ConstraintKey.Main(p));
            var state = dispatch.Phases.FirstOrDefault(s => s.Phase == p);
            var input = inputs.Dispatch.For(p);
            return new DispatchPhaseStatus(
                p,
                main?.Available,
                main?.InFlight ?? Amperes.Zero,
                input is { DataValid: true },
                input?.InvalidReason,
                state?.Foreign,
                state?.Baseline,
                state?.Rank,
                [.. protection.Groups.Where(g => g.Phases.Contains(p)).Select(g => envelope.For(new ConstraintKey(g.Id, p)) is { } e
                    ? new DispatchGroupEnvelope(g.Id, e.Available, e.InFlight, e.IncreasesAllowed)
                    : new DispatchGroupEnvelope(g.Id, null, Amperes.Zero, false))]);
        });

        var devices = setup.Devices.Select(d =>
        {
            var spec = d.Spec;
            var entry = ledger.Entry(spec.Id);
            var load = envelope.Device(spec.Id);
            var state = dispatch.Devices.FirstOrDefault(s => s.Device == spec.Id);
            var reported = load?.Reported;
            var intended = entry?.Target;
            var draws = spec.Phases.Where(p => spec.HasCurrentDraw(p) || d.HasPowerDraw(p)).ToList();
            DrawStatus? draw = draws.Count == 0 ? null : DrawOf(spec, d, draws, inputs);
            return new DispatchDeviceStatus(
                spec.Id,
                spec.Type,
                spec.Phases,
                d.RotationGroup,
                spec.CircuitGroup,
                intended,
                reported,
                reported is not null,
                intended is null || intended.Equals(reported),
                state?.Reason ?? entry?.Reason,
                entry?.Origin,
                state?.StartRank,
                state?.Pending,
                spec.Stages.Select(s => lockouts.MinOnRemaining(spec.Id, s.Index, now, LockoutTimeline.Target)).Max(),
                spec.Stages.Select(s => lockouts.MinOffRemaining(spec.Id, s.Index, now, LockoutTimeline.Target)).Max(),
                entry?.Deferred is { } deferred ? new DeferredStatus(deferred.Target, deferred.Reason, deferred.RefusedBy, deferred.Since, deferred.DueAt) : null,
                entry?.Window is { Kind: WindowKind.Command } ? SettlingStatus.Open : entry?.SettlingTimedOut == true ? SettlingStatus.TimedOut : SettlingStatus.None,
                entry is { UnresponsiveSince: { } since } ? new UnresponsiveStatus(since, entry.RetryAt) : null,
                entry?.Mismatches ?? 0,
                overrides.For(spec.Id),
                draw,
                entry?.History ?? [])
            {
                Start = spec.Start,
                Baseline = entry?.Baseline,
                UnconfirmedSince = entry?.UnconfirmedSince,
                SelfStarted = entry?.SelfStarted,
            };
        });

        return new DispatchStatus(now, mode, mode.Commands, [.. phases], [.. devices]);
    }

    private static DrawStatus DrawOf(DeviceSpec spec, DispatchDevice device, List<Phase> phases, ControlInputs inputs)
    {
        var currents = spec.CurrentDrawPhases.IsDefault ? [] : spec.CurrentDrawPhases.Select(p => inputs.Protection.DeviceDraws.IsDefault ? null : inputs.Protection.DeviceDraws.FirstOrDefault(s => s.Device == spec.Id && s.Phase == p)).ToList();
        var powers = device.PowerDrawPhases.IsDefault ? [] : device.PowerDrawPhases.Select(p => inputs.Dispatch.DevicePowers.IsDefault ? null : inputs.Dispatch.DevicePowers.FirstOrDefault(s => s.Device == spec.Id && s.Phase == p)).ToList();
        var fresh = currents.All(c => c is { Fresh: true }) && powers.All(w => w is { Fresh: true }) && phases.Count > 0;
        Amperes? current = currents.Count > 0 && currents.All(c => c?.Current is not null) ? new Amperes(currents.Sum(c => c!.Current!.Value.Value)) : null;
        Watts? power = powers.Count > 0 && powers.All(w => w?.Power is not null) ? new Watts(powers.Sum(w => w!.Power!.Value.Value)) : null;
        return new DrawStatus(current, power, fresh);
    }
}
