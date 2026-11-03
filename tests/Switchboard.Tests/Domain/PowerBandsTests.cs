using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Economics;
using static Switchboard.Tests.Support.EconomicsScenarios;

namespace Switchboard.Tests.Domain;

/// <summary>Power bands in force at an instant: local wall-clock hours, so the answer follows the clock the maintainer set them by.</summary>
public sealed class PowerBandsTests
{
    private static readonly PowerBand Evening = new(new HoursRange(21 * 60, 24 * 60), 2000);
    private static readonly PowerBand Night = new(new HoursRange(0, 7 * 60), 3000);

    [Fact]
    public void A_band_is_in_force_from_its_start_to_just_before_its_end()
    {
        // Why: the end is exclusive like every hours range, so 24:00 hands over to the night band without an instant under neither or both.
        Assert.Equal(2000, PowerBands.CeilingAt([Evening, Night], Local(2026, 10, 27, 21), Warsaw));
        Assert.Equal(2000, PowerBands.CeilingAt([Evening, Night], Local(2026, 10, 27, 23, 59), Warsaw));
        Assert.Equal(3000, PowerBands.CeilingAt([Evening, Night], Local(2026, 10, 28, 0), Warsaw));
        Assert.Null(PowerBands.CeilingAt([Evening, Night], Local(2026, 10, 28, 7), Warsaw));
        Assert.Null(PowerBands.CeilingAt([], Local(2026, 10, 28, 1), Warsaw));
    }

    [Fact]
    public void A_band_that_wraps_past_midnight_holds_on_both_sides_of_it()
    {
        var wrap = new PowerBand(new HoursRange(22 * 60, 6 * 60), 1500);

        Assert.Equal(1500, PowerBands.CeilingAt([wrap], Local(2026, 10, 27, 23), Warsaw));
        Assert.Equal(1500, PowerBands.CeilingAt([wrap], Local(2026, 10, 28, 5, 59), Warsaw));
        Assert.Null(PowerBands.CeilingAt([wrap], Local(2026, 10, 28, 6), Warsaw));
    }

    [Fact]
    public void Overlapping_bands_give_the_lowest_ceiling()
    {
        // Why: two bands that both apply are both limits; the stricter one is the one that holds.
        var wide = new PowerBand(new HoursRange(20 * 60, 24 * 60), 4000);

        Assert.Equal(2000, PowerBands.CeilingAt([wide, Evening], Local(2026, 10, 27, 22), Warsaw));
    }

    [Fact]
    public void On_the_25_hour_night_the_night_band_keeps_its_wall_clock_hours()
    {
        // Why: on 2026-10-25 the clock goes back at 03:00; 00:00-07:00 lasts eight hours, and 06:30 local is still inside it (a band in UTC would have ended).
        Assert.Equal(3000, PowerBands.CeilingAt([Night], Local(2026, 10, 25, 6, 30), Warsaw));
        Assert.Null(PowerBands.CeilingAt([Night], Local(2026, 10, 25, 7), Warsaw));
    }
}
