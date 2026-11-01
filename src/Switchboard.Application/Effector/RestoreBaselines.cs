using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Switchboard.Application.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;

namespace Switchboard.Application.Effector;

/// <summary>
/// The restore baselines of start: restore_only (maintainer survey 2026-10-25): the level Switchboard
/// reduced a device from, restored at most up to, and nothing beyond what Switchboard shed. It takes
/// a baseline at a reduction, decides a pending one by the first fresh report, lets a persisted one
/// stand only while the device still shows the level it was shed to, ends it at a restore, and
/// hands the commanded ones (with the devices dispatch started under start: headroom) to state.json.
/// The state itself is the ledger's (<see cref="DeviceTargets.BaselineLedger"/>). Single-threaded, on the
/// guard runner's thread.
/// </summary>
internal sealed class RestoreBaselines
{
    private readonly DeviceTargets _targets;
    private readonly ILogger _logger;

    public RestoreBaselines(DeviceTargets targets, ILogger logger)
    {
        _targets = targets;
        _logger = logger;
    }

    /// <summary>
    /// A target change is accepted: a reduction takes the baseline (and a select stop notes the
    /// option it stops from); an increase that restores up to the baseline ends it, one that only
    /// resumes below it names an undeclared option. Called by <c>EffectorGate.Accept</c>, which stays
    /// one atomic operation.
    /// </summary>
    public void OnChange(StepContext context, DeviceSpec device, DeviceTargets.State state, DecisionOrigin origin, DeviceLevel from, DeviceLevel level, DeviceLevel? reported, bool increase, bool decrease, bool commanding)
    {
        if (decrease)
        {
            TakeBaseline(context, device, state, origin, level, reported, commanding);
            if (device is SetpointSpec { StopOptions: { } options } && from.Enabled && !level.Enabled)
            {
                NoteOptionBefore(context, device, state, options, commanding);
            }
        }
        else if (increase && (commanding || state.Baselines.Baseline is { Commanded: false }) && state.Baselines.Baseline is { } baseline
            && DeviceLevels.Total(device, level) >= DeviceLevels.Total(device, baseline.Level) - DeviceLevel.SetpointTolerance)
        {
            // Restored up to the level the device had before Switchboard reduced it. A reduction that
            // was sent ends only with a restore that is sent too: a "would" restore in shadow (control
            // switched off after the shed) leaves the device where it is, still to be given back.
            WarnUndeclaredResume(device, from, level, baseline, commanding);
            state.Baselines.ClearBaseline();
        }
        else if (increase && state.Baselines.Baseline is { } partial)
        {
            // Enabled again below the level to give back (the rest waits for headroom): the resume
            // option leaves now all the same.
            WarnUndeclaredResume(device, from, level, partial, commanding);
        }
    }

    /// <summary>
    /// The restore baselines and the devices dispatch started under start: headroom, as state.json
    /// held them at the last stop; applied before the first step. A baseline stands only while the
    /// device's first fresh report still shows the level it was shed to (otherwise the user acted
    /// while the add-on was down).
    /// </summary>
    public void Restore(ImmutableDictionary<DeviceName, StartBaseline> baselines, ImmutableDictionary<DeviceName, DateTimeOffset> selfStarted)
    {
        ArgumentNullException.ThrowIfNull(baselines);
        ArgumentNullException.ThrowIfNull(selfStarted);
        foreach (var (device, baseline) in baselines)
        {
            var state = _targets.Get(device);
            state.Baselines.Baseline = baseline with { Commanded = true };
            state.Baselines.BaselineRestored = true;
        }

        foreach (var (device, at) in selfStarted)
        {
            _targets.Get(device).Baselines.SelfStarted = at;
        }
    }

    /// <summary>The commanded baselines and the devices started under start: headroom, for state.json (guard thread).</summary>
    public (ImmutableDictionary<DeviceName, StartBaseline> Baselines, ImmutableDictionary<DeviceName, DateTimeOffset> SelfStarted) Persisted()
    {
        var baselines = ImmutableDictionary.CreateBuilder<DeviceName, StartBaseline>();
        var started = ImmutableDictionary.CreateBuilder<DeviceName, DateTimeOffset>();
        foreach (var device in _targets.Devices)
        {
            _targets.TryGet(device, out var state);
            if (state.Baselines.Baseline is { Commanded: true } baseline)
            {
                baselines[device] = baseline;
            }

            if (state.Baselines.SelfStarted is { } at)
            {
                started[device] = at;
            }
        }

        return (baselines.ToImmutable(), started.ToImmutable());
    }

    /// <summary>
    /// A restore baseline taken while the level was unknown is decided by the first fresh report
    /// (set when it is above the shed level and was reported before the first acknowledgement of a
    /// command since: a report older than that is the user's level, even when the acknowledgement
    /// and the report arrive in the same step after a restart); one restored from state.json stands
    /// only while the device still shows the level it was shed to.
    /// When it does not (the user acted while the add-on was down, or the shutdown's off never
    /// landed) and a reduction of Switchboard holds the device below what it reports now (the
    /// startup safe state, taken while its level was unknown), the reported level is the one to give
    /// back, exactly as for a device without a persisted baseline.
    /// </summary>
    public void Resolve(StepContext context, DeviceSpec device, DeviceTargets.State state, DeviceLevel? reported, bool commanding)
    {
        if (reported is null)
        {
            return;
        }

        if (state.Baselines.BaselinePending is { } pending)
        {
            state.Baselines.BaselinePending = null;
            var reportedAt = ReportedAt(context, device);
            var usersLevel = state.Baselines.PendingAckedAt is not { } acked || (reportedAt is { } at && at < acked);
            state.Baselines.Baseline = usersLevel && DeviceLevels.Total(device, reported) > DeviceLevels.Total(device, pending.Shed) + DeviceLevel.SetpointTolerance
                ? pending with { Level = reported, OptionBefore = OptionNow(context, device) }
                : null;
            state.Baselines.PendingAckedAt = null;
        }

        if (state.Baselines.BaselineRestored)
        {
            state.Baselines.BaselineRestored = false;
            if (state.Baselines.Baseline is { } restored && !reported.Equals(restored.Shed))
            {
                state.Baselines.Baseline = state.Resolved.Target is { } target && state.Resolved.Origin is DecisionOrigin.Guard or DecisionOrigin.Dispatch
                    && DeviceLevels.Total(device, target) < DeviceLevels.Total(device, reported) - DeviceLevel.SetpointTolerance
                    ? new StartBaseline(reported, target, context.Now, commanding) { OptionBefore = OptionNow(context, device) }
                    : null;
            }
        }
    }

    /// <summary>
    /// The level before Switchboard's first reduction of a device is what start: restore_only
    /// restores. Only a reduction by protection or dispatch takes one, and only when the device's
    /// level is the user's own: never while the self-test or a start under start: headroom put it
    /// there (undoing that brings it back to the user's level, which is not restored).
    /// </summary>
    private static void TakeBaseline(StepContext context, DeviceSpec device, DeviceTargets.State state, DecisionOrigin origin, DeviceLevel level, DeviceLevel? reported, bool commanded)
    {
        if (origin is not (DecisionOrigin.Guard or DecisionOrigin.Dispatch) || state.Resolved.Origin == DecisionOrigin.SelfTest || state.Baselines.SelfStarted is not null
            || state.Baselines.Baseline is not null || state.Baselines.BaselinePending is not null)
        {
            return;
        }

        if (reported is null)
        {
            state.Baselines.BaselinePending = new StartBaseline(level, level, context.Now, commanded);
            state.Baselines.PendingAckedAt = null;
            return;
        }

        if (DeviceLevels.Total(device, reported) > DeviceLevels.Total(device, level) + DeviceLevel.SetpointTolerance)
        {
            state.Baselines.Baseline = new StartBaseline(reported, level, context.Now, commanded) { OptionBefore = OptionNow(context, device) };
        }
    }

    /// <summary>
    /// A select stop is about to stop the device: the option it reports now is the one it is
    /// stopped from (the baseline may be older, taken at a setpoint reduction before the user changed
    /// the option). An option not known now (its state not fresh, the first step after a restart
    /// before Home Assistant delivered it) keeps the one captured earlier. Protection stops it
    /// whatever that option is; when it is not a declared one, the log says that the device will
    /// come back with the resume option instead.
    /// </summary>
    private void NoteOptionBefore(StepContext context, DeviceSpec device, DeviceTargets.State state, StopOptions options, bool commanding)
    {
        var now = OptionNow(context, device);
        OptionText? option;
        if (state.Baselines.Baseline is { } baseline)
        {
            option = now ?? baseline.OptionBefore;
            state.Baselines.Baseline = baseline with { OptionBefore = option };
        }
        else if (state.Baselines.BaselinePending is { } pending)
        {
            option = now ?? pending.OptionBefore;
            state.Baselines.BaselinePending = pending with { OptionBefore = option };
        }
        else
        {
            return;
        }

        if (commanding && option is { } before && before != options.ResumeOption && before != options.StopOption)
        {
            _logger.LogWarning(
                "Stopping {Device} from the undeclared option '{Before}'; it will be resumed with its resume option '{Resume}'",
                device.Id,
                before,
                options.ResumeOption);
        }
    }

    /// <summary>
    /// A select stop is resumed (enabled again by a command that leaves) after it was stopped from an
    /// option nobody declared: only declared options are ever sent, so the user's own option is not
    /// restored, and the log names it.
    /// </summary>
    private void WarnUndeclaredResume(DeviceSpec device, DeviceLevel from, DeviceLevel level, StartBaseline baseline, bool commanding)
    {
        if (commanding && device is SetpointSpec { StopOptions: { } options } && !from.Enabled && level.Enabled
            && baseline.OptionBefore is { } before && before != options.ResumeOption && before != options.StopOption)
        {
            _logger.LogWarning(
                "Resuming {Device} with its resume option '{Resume}': before Switchboard stopped it, it reported '{Before}', an option that is not declared and is not restored",
                device.Id,
                options.ResumeOption,
                before);
        }
    }

    /// <summary>The option a select stop actuator reports now (fresh), or null: unknown, or a switch stop.</summary>
    private static OptionText? OptionNow(StepContext context, DeviceSpec device) =>
        device is SetpointSpec { StopOptions: not null } && context.Inputs?.Protection.DeviceStates is { IsDefault: false } samples
        && samples.FirstOrDefault(s => s.Device == device.Id && s.Stage == DeviceStateSample.StopStage) is { Fresh: true, Applied: RelayApplied { Option: { } option } }
            ? option
            : null;

    /// <summary>When the device's fresh state was last reported (the newest of its stages and stop actuator), or null.</summary>
    private static DateTimeOffset? ReportedAt(StepContext context, DeviceSpec device) =>
        context.Inputs?.Protection.DeviceStates is { IsDefault: false } samples
            ? samples.Where(s => s.Device == device.Id && s.Fresh && s.ReportedAt is not null).Select(s => s.ReportedAt).Max()
            : null;
}
