using System.Collections.Immutable;
using System.Text;

namespace Switchboard.Domain.Decisions;

/// <summary>
/// The closed, versioned list of reason codes (04 §6, version 2). Every decision carries exactly
/// one; the UI renders a sentence per code. The first member is 1, so <c>default</c> is not a code
/// and is refused wherever a decision is built.
/// </summary>
public enum ReasonCode
{
    GuardTierExceeded = 1,
    GuardBudgetLow,
    GuardStaleSignal,
    ReserveThreshold,
    HeadroomInsufficient,
    LockoutMinOn,
    LockoutMinOff,
    SettlingWindowOpen,
    AwaitingConfirmation,
    ValueRankBelowCutoff,
    GoalMet,
    GuardExpressionFalse,
    DeviceUnresponsive,
    ManualOverride,
    ModeShadow,
    SafeState,
    PlanSlotTarget,
    HeadroomAvailable,
    KillSwitch,
}

public static class ReasonCodes
{
    /// <summary>The version of the list in 04 §6; written with every logged decision.</summary>
    public const int ListVersion = 2;

    public static ImmutableArray<ReasonCode> All { get; } = [.. Enum.GetValues<ReasonCode>()];

    public static bool IsDefined(ReasonCode code) => Enum.IsDefined(code);

    /// <summary>The code as 04 §6 writes it: <c>guard_tier_exceeded</c>.</summary>
    public static string Name(ReasonCode code)
    {
        if (!IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code), code, "Not a reason code of list version 2.");
        }

        var text = code.ToString();
        var name = new StringBuilder(text.Length + 4);
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsUpper(text[i]) && i > 0)
            {
                name.Append('_');
            }

            name.Append(char.ToLowerInvariant(text[i]));
        }

        return name.ToString();
    }
}
