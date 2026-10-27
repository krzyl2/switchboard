using Switchboard.Application.Goals;

namespace Switchboard.Tests.Application.Goals;

/// <summary>
/// A goal's deadline is a local wall-clock time (a 06:30 departure), so it must land on the right instant on every local day, the 23-hour
/// and the 25-hour ones included, and must never come from the machine's zone. The zone is injected; Warsaw stands in for the maintainer.
/// </summary>
public sealed class GoalDeadlinesTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void A_recurring_time_is_the_next_occurrence_strictly_after_now()
    {
        // Why: a goal "by 06:30" asked at 18:00 is due tomorrow morning; asked at exactly 06:30 the deadline is reached, so the next cycle
        // is the next day's (a recurring goal rolls and is never reported missed).
        var evening = GoalDeadlines.NextOccurrence(new TimeOnly(6, 30), Utc(10, 26, 17), Warsaw);
        var atTheMoment = GoalDeadlines.NextOccurrence(new TimeOnly(6, 30), Utc(10, 27, 5, 30), Warsaw);
        var beforeIt = GoalDeadlines.NextOccurrence(new TimeOnly(6, 30), Utc(10, 27, 5, 29), Warsaw);

        Assert.Equal(Utc(10, 27, 5, 30), evening);
        Assert.Equal(Utc(10, 28, 5, 30), atTheMoment);
        Assert.Equal(Utc(10, 27, 5, 30), beforeIt);
    }

    [Fact]
    public void On_the_25_hour_day_a_local_time_is_one_hour_later_in_utc_than_the_day_before()
    {
        // Why: 2026-10-25 is the day the clocks go back. 06:30 local on the 25th is 05:30 UTC (CET), on the 24th it is 04:30 UTC (CEST):
        // a deadline computed with a fixed offset would be an hour off for the whole night around the change.
        var onThe24th = GoalDeadlines.NextOccurrence(new TimeOnly(6, 30), Utc(10, 24, 0), Warsaw);
        var onThe25th = GoalDeadlines.NextOccurrence(new TimeOnly(6, 30), Utc(10, 24, 12), Warsaw);

        Assert.Equal(Utc(10, 24, 4, 30), onThe24th);
        Assert.Equal(Utc(10, 25, 5, 30), onThe25th);
        Assert.Equal(Utc(10, 26, 5, 30), GoalDeadlines.NextOccurrence(new TimeOnly(6, 30), Utc(10, 25, 12), Warsaw));
    }

    [Fact]
    public void A_time_that_happens_twice_is_the_first_one_and_a_time_that_does_not_exist_moves_to_after_the_gap()
    {
        // Why: 02:30 occurs twice on 2026-10-25 and not at all on 2026-03-29. A deadline must be one definite instant; the earlier of two
        // (the sooner deadline is the safe side for a charge) and, for the gap, the first valid moment after it.
        var twice = GoalDeadlines.NextOccurrence(new TimeOnly(2, 30), Utc(10, 24, 12), Warsaw);
        var gap = GoalDeadlines.NextOccurrence(new TimeOnly(2, 30), Utc(3, 28, 12), Warsaw);

        Assert.Equal(Utc(10, 25, 0, 30), twice);
        Assert.Equal(Utc(3, 29, 1, 30), gap);
    }

    [Theory]
    [InlineData("2026-10-27T06:30:00+01:00", 10, 27, 5, 30)]
    [InlineData("2026-10-27T05:30:00Z", 10, 27, 5, 30)]
    [InlineData("2026-10-27 06:30:00", 10, 27, 5, 30)]
    [InlineData("2026-10-27 06:30", 10, 27, 5, 30)]
    [InlineData("2026-07-27 06:30:00", 7, 27, 4, 30)]
    [InlineData("06:30:00", 10, 27, 5, 30)]
    [InlineData("06:30", 10, 27, 5, 30)]
    public void A_deadline_entity_may_state_an_instant_a_local_date_and_time_or_a_time_of_day(string state, int month, int day, int hour, int minute)
    {
        // Why: an input_datetime reports a local date and time without an offset (or a time alone), a timestamp sensor reports an ISO
        // instant. The same goal must work with either, and a local one must be read in the injected zone, summer or winter.
        var deadline = GoalDeadlines.Parse(state, Utc(10, 26, 17), Warsaw);

        Assert.Equal(Utc(month, day, hour, minute), deadline);
    }

    [Theory]
    [InlineData("")]
    [InlineData("tomorrow")]
    [InlineData("2026-10-27")]
    [InlineData("2026-13-40 06:30:00")]
    [InlineData("25:61")]
    [InlineData("6:30")]
    [InlineData("1788000000")]
    public void Anything_else_is_not_a_deadline_and_never_a_guess(string state)
    {
        // Why: an unreadable deadline makes the goal inactive with a reason; guessing "tomorrow" would charge to a time nobody asked for.
        Assert.Null(GoalDeadlines.Parse(state, Utc(10, 26, 17), Warsaw));
    }
}
