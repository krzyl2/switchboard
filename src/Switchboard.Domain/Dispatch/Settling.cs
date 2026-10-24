using System.Collections.Immutable;
using Switchboard.Domain.Signals;

namespace Switchboard.Domain.Dispatch;

/// <summary>A balance input a command can move, whose next report closes a settling window (DISP-05).</summary>
public enum ClosingInput
{
    /// <summary>The main meter's power on the phase (net import).</summary>
    PhasePower,

    /// <summary>The main meter's current on the phase (or a circuit group's sub-meter).</summary>
    PhaseCurrent,

    /// <summary>The device's own draw-current signal.</summary>
    DrawCurrent,

    /// <summary>The device's own draw-power signal.</summary>
    DrawPower,
}

/// <summary>One member of a window's closing set and when it was last reported (null: never).</summary>
public readonly record struct ClosingSignal(ClosingInput Input, Phase Phase, DateTimeOffset? ReportedAt);

/// <summary>
/// The window opened by one command (04 §3): it closes when every balance input the command can
/// move has reported after <see cref="Required"/> (= command time + the device's reaction time),
/// and is closed anyway at <see cref="Timeout"/> (command time + settling.hard_timeout).
/// </summary>
public sealed record SettlingWindow(DateTimeOffset CommandedAt, DateTimeOffset Required, DateTimeOffset Timeout);

public enum SettlingState
{
    Open,
    Closed,

    /// <summary>The hard timeout came first; the silent inputs are named.</summary>
    TimedOut,
}

public sealed record SettlingOutcome(SettlingState State, ImmutableArray<ClosingSignal> Silent);

/// <summary>
/// The closing rule of DISP-05, pure. The device's own state signal is not in the closing set: a
/// relay that reports only on change (or ignores the command) would hold every window open until
/// the timeout; nor is a generation input that is valid without reports (an inverter asleep at
/// night). Both are left out by whoever builds the set.
/// </summary>
public static class SettlingRule
{
    public static SettlingOutcome Evaluate(SettlingWindow window, IReadOnlyCollection<ClosingSignal> closingSet, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(closingSet);
        var silent = closingSet.Where(s => s.ReportedAt is not { } at || at <= window.Required).ToImmutableArray();
        if (silent.IsEmpty && now >= window.Required)
        {
            return new SettlingOutcome(SettlingState.Closed, []);
        }

        return now >= window.Timeout ? new SettlingOutcome(SettlingState.TimedOut, silent) : new SettlingOutcome(SettlingState.Open, silent);
    }
}

/// <summary>The outcome of comparing a measured change with the expected one (DISP-07).</summary>
public enum Verdict
{
    Match,
    Mismatch,

    /// <summary>The measurement cannot be blamed on the device: a foreign step in the window, or a setpoint raised but not drawn (the car limits itself).</summary>
    Inconclusive,

    /// <summary>Shadow mode: nothing was applied, so there is nothing to verify.</summary>
    NotApplied,

    /// <summary>No measurement can show the change (a modulating device without a draw signal).</summary>
    Unverifiable,

    /// <summary>An input of the closing set stayed silent until the hard timeout; no mismatch is counted.</summary>
    TimedOut,
}

/// <summary>What a verification compares: a relay's expected step, or a setpoint's new ceiling against the device's own draw.</summary>
public enum VerificationKind
{
    /// <summary>|measured − expected| within tolerance.</summary>
    Step,

    /// <summary>A setpoint lowered: the draw must fall to at most the new ceiling.</summary>
    SetpointDecrease,

    /// <summary>A setpoint raised: a draw above the new ceiling is a mismatch, one below it is the device's own choice.</summary>
    SetpointIncrease,
}

/// <param name="Expected">Step: the expected change. Setpoint kinds: the new ceiling.</param>
/// <param name="Measured">Step: the measured change. Setpoint kinds: the draw after the window.</param>
/// <param name="Tolerance">max(fraction · |expected change|, σ multiplier · σ).</param>
/// <param name="ForeignStep">A foreign load event happened on the device's phases while the window was open.</param>
public sealed record VerificationCase(VerificationKind Kind, double Expected, double Measured, double Tolerance, bool ForeignStep);

public static class Verification
{
    public static Verdict Judge(VerificationCase check)
    {
        ArgumentNullException.ThrowIfNull(check);
        switch (check.Kind)
        {
            case VerificationKind.SetpointDecrease:
                return check.Measured <= check.Expected + check.Tolerance ? Verdict.Match : Verdict.Mismatch;
            case VerificationKind.SetpointIncrease:
                return check.Measured > check.Expected + check.Tolerance ? Verdict.Mismatch
                    : check.Measured >= check.Expected - check.Tolerance ? Verdict.Match
                    : Verdict.Inconclusive;
        }

        var excess = check.Measured - check.Expected;
        if (Math.Abs(excess) <= check.Tolerance)
        {
            return Verdict.Match;
        }

        // A foreign load step (only rises are flagged) explains a measured change above the
        // expected one in either commanded direction: more than a switch-on adds, or less than a
        // switch-off removes. The excess belongs to the house, not to the device.
        return check.ForeignStep && excess > check.Tolerance ? Verdict.Inconclusive : Verdict.Mismatch;
    }

    /// <summary>The tolerance of 04 §3: the larger of a fraction of the expected change and a multiple of the measurement noise.</summary>
    public static double Tolerance(double expectedChange, double fraction, double sigmaMultiplier, double sigma) =>
        Math.Max(fraction * Math.Abs(expectedChange), sigmaMultiplier * sigma);
}
