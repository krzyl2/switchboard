using System.Collections.Immutable;
using System.Text;

namespace Switchboard.Domain.Decisions;

/// <summary>
/// The closed, versioned list of reason codes (04 §6, version 6). Every decision carries exactly
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

    /// <summary>Version 5: the mode decision of an effective active mode (the add-on option, the control switch, the configuration and the state all allow commands). Versions 1 to 4 used manual_override.</summary>
    ModeActive,

    /// <summary>Version 5: a command went out and Home Assistant answered with an error, or gave no answer in time (the command may or may not have been carried out). Versions 1 to 4 used device_unresponsive, which stays the code of a device that does not react to what was carried out.</summary>
    CommandFailed,

    /// <summary>Version 5: a command never left: Home Assistant is not connected, the entity is not a declared actuator (or is one of Switchboard's own), or a newer command replaced it. Versions 1 to 4 used safe_state.</summary>
    CommandNotSent,

    /// <summary>Version 6: a start or increase permitted because the active tariff zone is one of the device's sources and its value covers the price (14-ECONOMICS). Versions 1 to 5 used headroom_available.</summary>
    ZoneAllowed,

    /// <summary>Version 6: a start or increase refused (or a stop) because no energy class that is open now (surplus, the active zone) is among the device's sources. Versions 1 to 5 used headroom_insufficient.</summary>
    SourceNotAllowed,

    /// <summary>Version 6: a start or increase refused (or a stop) because the available energy costs more (zone price, or forgone export) than the device's value per kWh plus min_margin. Versions 1 to 5 used headroom_insufficient.</summary>
    ValueBelowCost,

    /// <summary>Version 6: a start or increase permitted because the instantaneous surplus (net billing) covers the device. Versions 1 to 5 used headroom_available.</summary>
    SurplusAvailable,

    /// <summary>Version 6: a start refused or a stop because the instantaneous surplus (net billing) does not cover the device. Versions 1 to 5 used headroom_insufficient.</summary>
    SurplusLow,
}

public static class ReasonCodes
{
    /// <summary>The version of the list in 04 §6; written with every logged decision.</summary>
    public const int ListVersion = 6;

    /// <summary>The previous version (command path): its lines stay readable for the log's retention.</summary>
    public const int PreviousListVersion = 5;

    /// <summary>The version before that (0.9.x): its lines stay readable too.</summary>
    public const int EarlierListVersion = 4;

    /// <summary>The version before that (0.7.x … 0.8.x): its lines stay readable too.</summary>
    public const int OlderListVersion = 3;

    /// <summary>The oldest version still readable (0.3.x … 0.6.x).</summary>
    public const int OldestListVersion = 2;

    public static ImmutableArray<ReasonCode> All { get; } = [.. Enum.GetValues<ReasonCode>()];

    public static bool IsDefined(ReasonCode code) => Enum.IsDefined(code);

    /// <summary>
    /// Whether a code belongs to a list version: version 3 appended foreign_load and change_limit and
    /// version 4 the three balance codes, version 5 mode_active, command_failed and command_not_sent and
    /// version 6 the five economic codes (zone_allowed … surplus_low), so a line of an older version that
    /// carries one of them is not a decision of that version.
    /// </summary>
    public static bool InVersion(ReasonCode code, int version) =>
        IsDefined(code) && version switch
        {
            ListVersion => true,
            PreviousListVersion => code < ReasonCode.ZoneAllowed,
            EarlierListVersion => code < ReasonCode.ModeActive,
            OlderListVersion => code < ReasonCode.BalanceProjectionLow,
            OldestListVersion => code < ReasonCode.ForeignLoad,
            _ => false,
        };

    /// <summary>The code as 04 §6 writes it: <c>guard_tier_exceeded</c>.</summary>
    public static string Name(ReasonCode code)
    {
        if (!IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code), code, "Not a reason code of list version 6.");
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
