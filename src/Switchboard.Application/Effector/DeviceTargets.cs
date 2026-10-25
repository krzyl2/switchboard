using System.Collections.Immutable;
using Switchboard.Application.Decisions;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;

namespace Switchboard.Application.Effector;

/// <summary>A device's resolved target and whether a settling window it opened is still open.</summary>
/// <param name="WindowOpen">A command window (ours) is open: in active mode the in-flight change counts only then.</param>
/// <param name="From">The applied level the open window's command started from (its estimate is the change from there).</param>
/// <param name="ContributionAtCommand">The device's contribution per phase when the window opened (the meter has not seen the change yet).</param>
/// <param name="GroupTermAtCommand">The same for an estimated circuit group's worst-case term.</param>
public sealed record InFlightEntry(
    DeviceLevel Target,
    bool WindowOpen,
    DeviceLevel? From,
    ImmutableDictionary<Phase, Amperes> ContributionAtCommand,
    ImmutableDictionary<Phase, Amperes> GroupTermAtCommand);

/// <summary>What the guard reads from the effector's ledger: each device's target and open window (DISP-06).</summary>
public interface IInFlightView
{
    InFlightEntry? For(DeviceName device);
}

/// <summary>A wanted change waiting for a lockout (DISP-09) or, for an override, for headroom.</summary>
/// <param name="RefusedBy">lockout_min_on, lockout_min_off, headroom_insufficient or guard_budget_low.</param>
/// <param name="DueAt">When the lockout expires; null for a refusal re-checked every cycle.</param>
public sealed record Deferral(DeviceLevel Target, ReasonCode Reason, DecisionOrigin Origin, ReasonCode RefusedBy, DateTimeOffset Since, DateTimeOffset? DueAt);

public enum WindowKind
{
    /// <summary>Opened by a target change of ours (applied in active mode, virtual in shadow).</summary>
    Command,

    /// <summary>Opened by a reported transition nobody here commanded (another controller switched the device): history only, never counted.</summary>
    Observation,
}

/// <summary>An open settling window and what verification will compare against.</summary>
/// <param name="Applied">The change was commanded through the port (active mode); only then is it verified and counted.</param>
/// <param name="Before">Per phase at the command: the device's draw current (A), draw power (W) and the phase's house load (W).</param>
/// <param name="ForeignStep">A foreign load event happened on one of the device's phases while the window was open.</param>
/// <param name="Probe">An increase of an unresponsive device after unresponsive_retry_after.</param>
public sealed record OpenWindow(
    WindowKind Kind,
    SettlingWindow Times,
    DeviceLevel From,
    DeviceLevel To,
    bool Increase,
    bool Applied,
    ImmutableDictionary<Phase, Amperes> ContributionAtCommand,
    ImmutableDictionary<Phase, Amperes> GroupTermAtCommand,
    ImmutableDictionary<Phase, WindowBaseline> Before,
    bool ForeignStep,
    bool Probe)
{
    /// <summary>
    /// Set at the start of the step whose inputs closed (or timed out) the window: from then on it
    /// counts nothing in flight; it is verified and removed later in the same step, after dispatch
    /// had its chance to report a foreign step.
    /// </summary>
    public SettlingState? Outcome { get; init; }

    public bool Open => Outcome is null;
}

/// <summary>A phase's measurements when a window opened; null where a signal was not fresh.</summary>
public sealed record WindowBaseline(Amperes? DrawCurrent, Watts? DrawPower, Watts? Load);

public enum HistoryKind
{
    Command,
    Observed,
}

/// <summary>One verification or observation of a device (UI-12 history).</summary>
public sealed record VerificationRecord(DateTimeOffset At, HistoryKind Kind, double Expected, double Measured, MeasureUnit Unit, Verdict Verdict)
{
    /// <summary>When the window opened: the (virtual, in shadow mode) command, or the observed transition. Read-only exposure for the device card.</summary>
    public DateTimeOffset? CommandedAt { get; init; }

    /// <summary>The level the window's change started from.</summary>
    public DeviceLevel? From { get; init; }

    /// <summary>The level the window's change led to.</summary>
    public DeviceLevel? To { get; init; }
}

/// <summary>The ledger's view of one device, for dispatch and the status.</summary>
/// <param name="Target">The resolved target; null while unset (it then follows the reported state).</param>
/// <param name="SettlingTimedOut">The last command window closed by its hard timeout.</param>
public sealed record LedgerEntry(
    DeviceLevel? Target,
    DecisionOrigin? Origin,
    ReasonCode? Reason,
    Deferral? Deferred,
    OpenWindow? Window,
    bool SettlingTimedOut,
    int Mismatches,
    DateTimeOffset? UnresponsiveSince,
    DateTimeOffset? RetryAt,
    ImmutableArray<VerificationRecord> History)
{
    public bool Unresponsive => UnresponsiveSince is not null;

    /// <summary>The current target was commanded through the port (active mode): until the reported state shows it, it counts as ours.</summary>
    public bool TargetCommanded { get; init; }

    /// <summary>The verification of the current target's command was a mismatch: the device measurably did not follow, so it no longer counts as ours.</summary>
    public bool CommandRefuted { get; init; }
}

/// <summary>What dispatch reads from the ledger: targets, windows, deferrals, unresponsive devices.</summary>
public interface IDispatchLedgerView : IInFlightView
{
    LedgerEntry? Entry(DeviceName device);
}

/// <summary>
/// The effector's ledger (03 EffectorGate): per device the resolved target, a deferred intent,
/// the open settling window, the mismatch count, the unresponsive flag and a short history.
/// Nothing here is persisted (SAFE-07): a restart starts every device unset. Owned by the gate and
/// touched only on the guard runner's thread; the guard and dispatch read it through the views.
/// </summary>
public sealed class DeviceTargets : IDispatchLedgerView
{
    private readonly Dictionary<DeviceName, State> _devices = [];

    public InFlightEntry? For(DeviceName device) =>
        _devices.TryGetValue(device, out var state) && state.Target is { } target
            ? state.Window is { Kind: WindowKind.Command, Open: true } open
                ? new InFlightEntry(target, true, open.From, open.ContributionAtCommand, open.GroupTermAtCommand)
                : new InFlightEntry(target, false, null, ImmutableDictionary<Phase, Amperes>.Empty, ImmutableDictionary<Phase, Amperes>.Empty)
            : null;

    public LedgerEntry? Entry(DeviceName device) =>
        _devices.TryGetValue(device, out var s)
            ? new LedgerEntry(s.Target, s.Origin, s.Reason, s.Deferred, s.Window is { Open: true } w ? w : null, s.TimedOut, s.Mismatches, s.UnresponsiveSince, s.RetryAt, [.. s.History]) { TargetCommanded = s.TargetCommanded, CommandRefuted = s.CommandRefuted }
            : null;

    internal IEnumerable<DeviceName> Devices => _devices.Keys;

    internal State Get(DeviceName device)
    {
        if (!_devices.TryGetValue(device, out var state))
        {
            state = new State();
            _devices[device] = state;
        }

        return state;
    }

    internal bool TryGet(DeviceName device, out State state) => _devices.TryGetValue(device, out state!);

    internal void Remove(DeviceName device) => _devices.Remove(device);

    /// <summary>Everything the ledger holds for one device; mutated by the gate only.</summary>
    internal sealed class State
    {
        public DeviceSpec? Spec { get; set; }

        public DeviceLevel? Target { get; set; }

        /// <summary>The target was commanded through the port (active mode).</summary>
        public bool TargetCommanded { get; set; }

        /// <summary>The current target's command was verified as a mismatch.</summary>
        public bool CommandRefuted { get; set; }

        public DecisionOrigin? Origin { get; set; }

        public ReasonCode? Reason { get; set; }

        /// <summary>The last known applied state (unknown samples leave it), for transition detection.</summary>
        public DeviceLevel? LastReported { get; set; }

        public Deferral? Deferred { get; set; }

        /// <summary>The deferral was recorded (once per deferral).</summary>
        public bool DeferredRecorded { get; set; }

        /// <summary>The last deferral recorded, so one lockout episode is recorded once even when dispatch stops wanting the change in between.</summary>
        public Deferral? LastDeferralRecord { get; set; }

        public OpenWindow? Window { get; set; }

        public bool TimedOut { get; set; }

        public int Mismatches { get; set; }

        public DateTimeOffset? UnresponsiveSince { get; set; }

        public DateTimeOffset? RetryAt { get; set; }

        public Queue<VerificationRecord> History { get; } = new();

        /// <summary>The override last recorded for this device (Auto until one is set).</summary>
        public Dispatch.OverrideMode Override { get; set; }

        public void Unset()
        {
            Target = null;
            TargetCommanded = false;
            CommandRefuted = false;
            Origin = null;
            Reason = null;
            Deferred = null;
            DeferredRecorded = false;
            Window = null;
            TimedOut = false;
            Mismatches = 0;
            UnresponsiveSince = null;
            RetryAt = null;
        }
    }
}
