using System.Collections.Immutable;
using System.Text;

namespace Switchboard.Domain.Decisions;

/// <summary>
/// The closed, versioned list of reason codes (04 §6, version 4). Every decision carries exactly
/// one; the UI renders a sentence per code. The first member is 1, so <c>default</c> is not a code
/// and is refused wherever a decision is built. A new version only appends, so the numeric value of
/// every earlier code stays where it was.
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

    /// <summary>Version 3: a reduction, shed or refused increase whose cause is a detected foreign load (04 §4).</summary>
    ForeignLoad,

    /// <summary>Version 3: an increase deferred because another increase of this settling window is still in flight (DISP-08).</summary>
    ChangeLimit,

    /// <summary>Version 4: a start refused or a stop because the hourly balance projected to the next :00 leaves no room (13-HOURLY-BALANCE).</summary>
    BalanceProjectionLow,

    /// <summary>Version 4: a start or increase permitted because the projected hourly balance is above the band.</summary>
    BalanceSurplus,

    /// <summary>Version 4: no allowance because the hourly balance or the free power is not known.</summary>
    BalanceUnknown,
}

public static class ReasonCodes
{
    /// <summary>The version of the list in 04 §6; written with every logged decision.</summary>
    public const int ListVersion = 4;

    /// <summary>The previous version (0.7.x … 0.8.x): its lines stay readable for the log's retention.</summary>
    public const int PreviousListVersion = 3;

    /// <summary>The oldest version still readable (0.3.x … 0.6.x).</summary>
    public const int OldestListVersion = 2;

    public static ImmutableArray<ReasonCode> All { get; } = [.. Enum.GetValues<ReasonCode>()];

    public static bool IsDefined(ReasonCode code) => Enum.IsDefined(code);

    /// <summary>
    /// Whether a code belongs to a list version: version 3 appended foreign_load and change_limit and
    /// version 4 the three balance codes, so a line of an older version that carries one of them is not
    /// a decision of that version.
    /// </summary>
    public static bool InVersion(ReasonCode code, int version) =>
        IsDefined(code) && version switch
        {
            ListVersion => true,
            PreviousListVersion => code < ReasonCode.BalanceProjectionLow,
            OldestListVersion => code < ReasonCode.ForeignLoad,
            _ => false,
        };

    /// <summary>The code as 04 §6 writes it: <c>guard_tier_exceeded</c>.</summary>
    public static string Name(ReasonCode code)
    {
        if (!IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code), code, "Not a reason code of list version 4.");
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
