using Switchboard.Application.Ev;

namespace Switchboard.Tests.Application.Ev;

/// <summary>
/// A charge-now duration is either "the configured one" (absent), a real span, or zero, which the control refuses as out of range. A span too long for a
/// TimeSpan must stay positive so the horizon check, not "not above zero", answers it: a wrong word there tells the user to ask for more, not less.
/// </summary>
public sealed class ChargeNowDurationTests
{
    [Fact]
    public void Absent_takes_the_configured_value()
    {
        Assert.Null(ChargeNowDuration.FromMinutes(null));
    }

    [Fact]
    public void A_positive_number_of_minutes_is_that_span()
    {
        Assert.Equal(TimeSpan.FromMinutes(90), ChargeNowDuration.FromMinutes(90));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    public void Not_above_zero_is_zero_so_the_control_refuses_it(double minutes)
    {
        Assert.Equal(TimeSpan.Zero, ChargeNowDuration.FromMinutes(minutes));
    }

    [Fact]
    public void A_span_too_long_for_a_timespan_stays_positive()
    {
        var huge = ChargeNowDuration.FromMinutes(1e300);

        Assert.True(huge > TimeSpan.Zero);
        Assert.Equal(TimeSpan.MaxValue / 2, huge);
    }
}
