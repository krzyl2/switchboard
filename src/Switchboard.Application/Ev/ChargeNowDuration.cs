namespace Switchboard.Application.Ev;

/// <summary>What a charge-now request's duration comes to before <see cref="IEvControl.StartChargeNow"/> judges it.</summary>
public static class ChargeNowDuration
{
    /// <summary>
    /// Absent means "take the configured value" (null); a duration not above zero (or not finite) is <see cref="TimeSpan.Zero"/>, which the control refuses as out of range;
    /// one too long for a TimeSpan is still a positive one, refused by the horizon check (deadline_beyond_horizon), not called "not above zero".
    /// </summary>
    public static TimeSpan? FromMinutes(double? minutes) => minutes switch
    {
        null => null,
        { } m when double.IsFinite(m) && m > 0 => m <= TimeSpan.MaxValue.TotalMinutes / 2 ? TimeSpan.FromMinutes(m) : TimeSpan.MaxValue / 2,
        _ => TimeSpan.Zero,
    };
}
