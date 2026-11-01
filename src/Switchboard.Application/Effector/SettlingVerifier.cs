using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Decisions;
using Switchboard.Application.Signals;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Effector;

/// <summary>Records a decision of dispatch's own kind about a device (marked unresponsive, cleared, settling timed out) at the target the ledger holds.</summary>
internal delegate void RecordDispatchEvent(DecisionAction action, ReasonCode reason, DispatchDecisionInputs inputs);

/// <summary>
/// What a command did to the measurement (DISP-05, DISP-07, DISP-06): the settling windows (opened
/// with a change, closed once the closing set reported or the hard timeout passed), the verdict on
/// a closed window and the history of verdicts, the acknowledged command's confirmation, and the
/// mismatches that mark a device unresponsive. It owns no state of its own: the windows, counts and
/// history are the ledger's (<see cref="DeviceTargets.SettlingLedger"/>), which the gate shares with
/// the guard and dispatch through the views. Single-threaded, on the guard runner's thread.
/// </summary>
internal sealed class SettlingVerifier
{
    private readonly DeviceTargets _targets;
    private readonly DecisionFactory _decisions;
    private readonly EffectorSettings _settings;
    private readonly ILogger _logger;

    public SettlingVerifier(DeviceTargets targets, DecisionFactory decisions, EffectorSettings settings, ILogger logger)
    {
        _targets = targets;
        _decisions = decisions;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Every open settling window whose closing set has reported after its required time (or whose
    /// hard timeout passed) is closed, so the guard's estimate already follows the measurement
    /// (DISP-06). A closed window is verified later in the same step (<see cref="Settle"/>).
    /// </summary>
    public void CloseWindows(ControlInputs? inputs, DateTimeOffset now)
    {
        foreach (var device in _targets.Devices.ToList())
        {
            _targets.TryGet(device, out var state);
            if (state.Settling.Window is not { Open: true } window || state.Spec is not { } spec)
            {
                continue;
            }

            var outcome = SettlingRule.Evaluate(window.Times, ClosingSet(inputs, spec), now);
            if (outcome.State == SettlingState.TimedOut)
            {
                _logger.LogWarning(
                    "Settling window of {Device} hit its hard timeout; silent inputs: {Silent}",
                    device,
                    string.Join(", ", outcome.Silent.Select(s => $"{s.Input}/{s.Phase} (last {s.ReportedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "never"})")));
            }

            if (outcome.State != SettlingState.Open)
            {
                state.Settling.Window = window with { Outcome = outcome.State };
            }
        }
    }

    /// <summary>
    /// Settling (DISP-05, DISP-07): an open window closes once every balance input its change can
    /// move has reported after command time + reaction time (the device's own state is not in that
    /// set), or at the hard timeout. A closed command window that was applied is verified; in
    /// shadow mode nothing was applied (not applied). An observation window only adds history.
    /// </summary>
    public void Settle(StepContext context, DeviceSpec device, RecordDispatchEvent record)
    {
        var state = _targets.Get(device.Id);
        if (state.Settling.Window is not { Open: false, Outcome: { } outcome } window || context.DispatchSetup?.Device(device.Id) is not { } dispatchDevice)
        {
            return;
        }

        state.Settling.Window = null;
        var timedOut = outcome == SettlingState.TimedOut;
        var (verdict, expected, measured, unit) = timedOut
            ? (Verdict.TimedOut, 0.0, 0.0, MeasureUnit.Watts)
            : window.Kind == WindowKind.Command && !window.Applied ? (window.NotDelivered ? Verdict.NotDelivered : Verdict.NotApplied, 0.0, 0.0, MeasureUnit.Watts)
            : Verify(context, device, dispatchDevice, window);
        AddHistory(state, new VerificationRecord(context.Now, window.Kind == WindowKind.Command ? HistoryKind.Command : HistoryKind.Observed, expected, measured, unit, verdict)
        {
            CommandedAt = window.Times.CommandedAt,
            From = window.From,
            To = window.To,
        });
        if (window.Kind != WindowKind.Command)
        {
            return;
        }

        state.Settling.TimedOut = timedOut;
        var inputs = _decisions.DispatchInputs(context, device, state) with
        {
            Verification = new VerificationInputs(expected, measured, unit, verdict),
            Settling = timedOut ? SettlingState.TimedOut : SettlingState.Closed,
        };
        if (timedOut)
        {
            // Lost sample: no mismatch is counted, the in-flight change ends, measurement rules.
            record(DecisionAction.SettlingTimedOut, ReasonCode.SettlingWindowOpen, inputs);
        }

        if (!window.Applied)
        {
            return;
        }

        var settling = context.DispatchSetup!.Settling;
        switch (verdict)
        {
            case Verdict.Match:
                state.Settling.Mismatches = 0;
                if (state.Settling.UnresponsiveSince is not null && window.Probe)
                {
                    state.Settling.UnresponsiveSince = null;
                    state.Settling.RetryAt = null;
                    record(DecisionAction.UnresponsiveCleared, ReasonCode.DeviceUnresponsive, inputs);
                }

                break;
            case Verdict.Mismatch:
                state.Resolved.CommandRefuted = window.To.Equals(state.Resolved.Target);
                if (state.Settling.CommandMismatchCounted && window.To.Equals(state.Resolved.Target))
                {
                    // At most one mismatch per command: its failure or its confirmation counted it already.
                    break;
                }

                state.Settling.CommandMismatchCounted = window.To.Equals(state.Resolved.Target);
                state.Settling.Mismatches++;
                if (window.Probe || (state.Settling.UnresponsiveSince is null && state.Settling.Mismatches >= settling.MismatchLimit))
                {
                    // A failed probe re-marks at once, without counting again.
                    state.Settling.UnresponsiveSince = window.Probe ? state.Settling.UnresponsiveSince ?? context.Now : context.Now;
                    state.Settling.RetryAt = context.Now + settling.UnresponsiveRetryAfter;
                    record(DecisionAction.MarkedUnresponsive, ReasonCode.DeviceUnresponsive, inputs);
                }

                break;
            default:
                if (window.Probe && state.Settling.UnresponsiveSince is not null)
                {
                    // An inconclusive probe proves nothing: the next one comes after the retry delay.
                    state.Settling.RetryAt = context.Now + settling.UnresponsiveRetryAfter;
                }

                break;
        }
    }

    /// <summary>
    /// The balance inputs a change on this device can move: per phase the main power and current and
    /// a sub-metered group current. Never the device's own state (a relay that reports only on
    /// change, or ignores the command, would hold the window to its timeout), never a generation
    /// input (an inverter asleep at night reports nothing), never the device's own draws: a draw
    /// that reports only on change (the go-e's currents) falls silent once the draw is steady, and
    /// would hold every window to its timeout. A draw is read when the window closes.
    /// </summary>
    private static List<ClosingSignal> ClosingSet(ControlInputs? inputs, DeviceSpec device)
    {
        var set = new List<ClosingSignal>();
        foreach (var phase in device.Phases)
        {
            if (inputs?.Dispatch.For(phase) is { InvalidReason: not DispatchInvalidReason.PowerUnmapped } dispatch)
            {
                set.Add(new ClosingSignal(ClosingInput.PhasePower, phase, dispatch.PowerReportedAt));
            }

            set.Add(new ClosingSignal(ClosingInput.PhaseCurrent, phase, inputs?.Protection.For(phase)?.Current?.ReportedAt));
            if (device.CircuitGroup is { } group && inputs?.Protection.Setup?.Group(group) is { SubMetered: true } spec && spec.Phases.Contains(phase))
            {
                var meter = inputs.Protection.Groups.IsDefault ? null : inputs.Protection.Groups.FirstOrDefault(g => g.Group == group && g.Phase == phase);
                set.Add(new ClosingSignal(ClosingInput.PhaseCurrent, phase, meter?.Current?.ReportedAt));
            }

        }

        return set;
    }

    /// <summary>
    /// DISP-07: the device's own draw first (A, else W); a relay without one against the phase's
    /// house load (net import plus generation share, so clouds do not count) and its rated power; a
    /// setpoint device without a draw cannot be verified. A setpoint is a ceiling: a decrease must
    /// bring the draw down to it, an increase the car does not take is inconclusive.
    /// </summary>
    private static (Verdict Verdict, double Expected, double Measured, MeasureUnit Unit) Verify(StepContext context, DeviceSpec device, DispatchDevice dispatchDevice, OpenWindow window)
    {
        var settling = context.DispatchSetup!.Settling;
        var phases = device.Phases;
        if (phases.All(device.HasCurrentDraw))
        {
            var now = phases.Select(p => DrawSample(context, device.Id, p) is { Fresh: true, Current: { } c } ? c.Value : (double?)null).ToList();
            if (now.Any(v => v is null) || (device is RelaySpec && phases.Any(p => window.Before.GetValueOrDefault(p)?.DrawCurrent is null)))
            {
                return (Verdict.Inconclusive, 0, 0, MeasureUnit.Amperes);
            }

            var ceilingTo = phases.Sum(p => DeviceLevels.Ceiling(device, window.To, p).Value);
            var ceilingFrom = phases.Sum(p => DeviceLevels.Ceiling(device, window.From, p).Value);
            var tolerance = Verification.Tolerance(ceilingTo - ceilingFrom, settling.VerificationTolerance, settling.VerificationSigmaMultiplier, dispatchDevice.CurrentDrawSigma.Value);
            if (device is SetpointSpec)
            {
                var drawn = now.Sum(v => v!.Value);
                var kind = ceilingTo < ceilingFrom ? VerificationKind.SetpointDecrease : VerificationKind.SetpointIncrease;
                return (Verification.Judge(new VerificationCase(kind, ceilingTo, drawn, tolerance, window.ForeignStep)), ceilingTo, drawn, MeasureUnit.Amperes);
            }

            var measured = phases.Select((p, i) => now[i]!.Value - window.Before[p].DrawCurrent!.Value.Value).Sum();
            var expected = ceilingTo - ceilingFrom;
            return (Verification.Judge(new VerificationCase(VerificationKind.Step, expected, measured, tolerance, window.ForeignStep)), expected, measured, MeasureUnit.Amperes);
        }

        if (device is SetpointSpec)
        {
            return (Verdict.Unverifiable, 0, 0, MeasureUnit.Amperes);
        }

        var expectedW = phases.Sum(p => dispatchDevice.RatedPower(window.To, p).Value - dispatchDevice.RatedPower(window.From, p).Value);
        if (phases.All(dispatchDevice.HasPowerDraw))
        {
            var after = phases.Select(p => PowerSample(context, device.Id, p) is { Fresh: true, Power: { } w } ? w.Value : (double?)null).ToList();
            if (after.Any(v => v is null) || phases.Any(p => window.Before.GetValueOrDefault(p)?.DrawPower is null))
            {
                return (Verdict.Inconclusive, expectedW, 0, MeasureUnit.Watts);
            }

            var measuredW = phases.Select((p, i) => after[i]!.Value - window.Before[p].DrawPower!.Value.Value).Sum();
            var toleranceW = Verification.Tolerance(expectedW, settling.VerificationTolerance, settling.VerificationSigmaMultiplier, dispatchDevice.PowerDrawSigma.Value);
            return (Verification.Judge(new VerificationCase(VerificationKind.Step, expectedW, measuredW, toleranceW, window.ForeignStep)), expectedW, measuredW, MeasureUnit.Watts);
        }

        var loads = phases.Select(p => context.Inputs?.Dispatch.For(p) is { DataValid: true, Load: { } l } ? l.Value : (double?)null).ToList();
        if (loads.Any(v => v is null) || phases.Any(p => window.Before.GetValueOrDefault(p)?.Load is null))
        {
            return (Verdict.Inconclusive, expectedW, 0, MeasureUnit.Watts);
        }

        var measuredLoad = phases.Select((p, i) => loads[i]!.Value - window.Before[p].Load!.Value.Value).Sum();
        var sigma = phases.Sum(p => context.DispatchSetup!.LoadSigma.GetValueOrDefault(p).Value);
        var toleranceLoad = Verification.Tolerance(expectedW, settling.VerificationTolerance, settling.VerificationSigmaMultiplier, sigma);
        return (Verification.Judge(new VerificationCase(VerificationKind.Step, expectedW, measuredLoad, toleranceLoad, window.ForeignStep)), expectedW, measuredLoad, MeasureUnit.Watts);
    }

    internal static OpenWindow OpenWindow(StepContext context, DeviceSpec device, WindowKind kind, DeviceLevel from, DeviceLevel to, bool applied, bool probe)
    {
        var reaction = device.ReactionTime;
        var timeout = context.DispatchSetup?.Settling.HardTimeout ?? reaction;
        var load = context.Evaluation.Envelope.Device(device.Id);
        var before = device.Phases.ToImmutableDictionary(p => p, p => new WindowBaseline(
            DrawSample(context, device.Id, p) is { Fresh: true, Current: { } c } ? c : null,
            PowerSample(context, device.Id, p) is { Fresh: true, Power: { } w } ? w : null,
            context.Inputs?.Dispatch.For(p) is { DataValid: true, Load: { } l } ? l : null));
        return new OpenWindow(
            kind,
            new SettlingWindow(context.Now, context.Now + reaction, context.Now + Max(timeout, reaction)),
            from,
            to,
            DeviceLevels.Total(device, to) > DeviceLevels.Total(device, from),
            applied,
            load?.Contribution ?? ImmutableDictionary<Phase, Amperes>.Empty,
            device.Phases.ToImmutableDictionary(p => p, p => LoadModel.GroupTerm(device, p, load?.Reported, DrawSample(context, device.Id, p))),
            before,
            false,
            probe);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;

    private static DeviceDrawSample? DrawSample(StepContext context, DeviceName device, Phase phase) =>
        context.Inputs?.Protection.DeviceDraws is { IsDefault: false } draws ? draws.FirstOrDefault(d => d.Device == device && d.Phase == phase) : null;

    private static DevicePowerSample? PowerSample(StepContext context, DeviceName device, Phase phase) =>
        context.Inputs?.Dispatch.DevicePowers is { IsDefault: false } powers ? powers.FirstOrDefault(d => d.Device == device && d.Phase == phase) : null;

    private void AddHistory(DeviceTargets.State state, VerificationRecord record)
    {
        state.Settling.History.Enqueue(record);
        while (state.Settling.History.Count > _settings.HistoryLength)
        {
            state.Settling.History.Dequeue();
        }
    }

    /// <summary>A foreign load step on a phase makes an excess in the commanded direction of an open window inconclusive.</summary>
    public void MarkForeignSteps(StepContext context, ProtectionSetup setup)
    {
        var phases = context.Dispatch?.Events.Where(e => e.Action == DecisionAction.ForeignLoadDetected && e.Phase is not null).Select(e => e.Phase!.Value).ToHashSet() ?? [];
        if (phases.Count == 0)
        {
            return;
        }

        foreach (var device in setup.Devices.Where(d => d.Phases.Any(phases.Contains)))
        {
            var state = _targets.Get(device.Id);
            if (state.Settling.Window is { } window)
            {
                state.Settling.Window = window with { ForeignStep = true };
            }
        }
    }

    /// <summary>
    /// An acknowledged command to a device whose state reports on change must show in the reported
    /// state by ack + reaction time + effector.confirm_timeout. If it does not (a fresh report still differs), the device is
    /// flagged unconfirmed (a warning; whether Home Assistant marks a Modbus or go-e entity
    /// unavailable after a link loss is not known), the window's relief ends, and one mismatch counts
    /// unless the command's verification counted one already. The flag clears once it shows.
    /// </summary>
    public void Confirm(StepContext context, DeviceSpec device, DeviceTargets.State state, DeviceLevel? reported, RecordDispatchEvent record)
    {
        if (state.Commands.UnconfirmedSince is not null && reported is not null && reported.Equals(state.Resolved.Target))
        {
            state.Commands.UnconfirmedSince = null;
            _logger.LogInformation("Device {Device} now reports the commanded level {Target}", device.Id, state.Resolved.Target);
        }

        if (state.Commands.ConfirmBy is not { } by || context.Now < by)
        {
            return;
        }

        state.Commands.ConfirmBy = null;
        if (reported is null || state.Resolved.Target is not { } target || reported.Equals(target) || (device is SetpointSpec setpointDevice && reported is SetpointLevel shown && target is SetpointLevel commanded && TransitionObserver.SameSetpoint(shown.Setpoint, commanded.Setpoint, setpointDevice) && shown.Enabled == commanded.Enabled))
        {
            return;
        }

        state.Commands.UnconfirmedSince ??= context.Now;
        _logger.LogWarning(
            "Device {Device} acknowledged the command to {Target} but still reports {Reported} after its reaction time and confirm timeout; check that its entity follows commands",
            device.Id,
            target,
            reported);
        if (state.Settling.Window is { Kind: WindowKind.Command, Open: true, Applied: true } window)
        {
            state.Settling.Window = window with { Outcome = SettlingState.Closed };
        }

        if (!state.Settling.CommandMismatchCounted)
        {
            CountMismatch(context, device, state, record);
        }
    }

    /// <summary>One mismatch of a command (its failure, or a confirmation that never came): at most one per command, toward settling.mismatch_limit.</summary>
    public void CountMismatch(StepContext context, DeviceSpec device, DeviceTargets.State state, RecordDispatchEvent record)
    {
        if (state.Settling.CommandMismatchCounted || context.DispatchSetup is not { } setup)
        {
            return;
        }

        state.Settling.CommandMismatchCounted = true;
        state.Settling.Mismatches++;
        if (state.Settling.UnresponsiveSince is null && state.Settling.Mismatches >= setup.Settling.MismatchLimit)
        {
            state.Settling.UnresponsiveSince = context.Now;
            state.Settling.RetryAt = context.Now + setup.Settling.UnresponsiveRetryAfter;
            record(DecisionAction.MarkedUnresponsive, ReasonCode.DeviceUnresponsive, _decisions.DispatchInputs(context, device, state));
        }
    }
}
