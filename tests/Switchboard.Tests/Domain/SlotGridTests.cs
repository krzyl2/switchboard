using Switchboard.Domain.Planning;

namespace Switchboard.Tests.Domain;

/// <summary>
/// The planner's slot grid (15-PLANNER, "The slot grid"). Why it matters: every planned kWh is placed in a slot, so a grid that loses the 25th hour or
/// invents a 24th on a 23-hour day shifts every price, forecast bin and deadline by an hour on exactly the days the clocks change.
/// </summary>
public sealed class SlotGridTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly TimeSpan Hourly = TimeSpan.FromMinutes(60);

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute = 0) => new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    /// <summary>The local calendar day of a zone as [00:00, next 00:00), found from the zone rules, not from a 24-hour assumption.</summary>
    private static (DateTimeOffset From, DateTimeOffset To) LocalDay(TimeZoneInfo zone, int year, int month, int day)
    {
        var start = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Unspecified);
        var next = start.AddDays(1);
        return (new DateTimeOffset(start, zone.GetUtcOffset(start)), new DateTimeOffset(next, zone.GetUtcOffset(next)));
    }

    private static SlotGrid Day(TimeZoneInfo zone, int year, int month, int day, TimeSpan slot)
    {
        var (from, to) = LocalDay(zone, year, month, day);
        return SlotGrid.Build(from, to, slot, zone);
    }

    [Fact]
    public void A_23_hour_day_has_23_hourly_slots_and_the_lost_hour_is_not_one_of_them()
    {
        // Why: 2026-03-29 in Warsaw jumps 02:00 to 03:00. A grid with 24 slots would plan energy in an hour that never happens.
        var grid = Day(Warsaw, 2026, 3, 29, Hourly);

        Assert.Equal(23, grid.Count);
        Assert.Equal(TimeSpan.FromHours(23), grid.Slots.Aggregate(TimeSpan.Zero, (sum, s) => sum + s.Duration));
        Assert.All(grid.Slots, s => Assert.Equal(Hourly, s.Duration));
        Assert.DoesNotContain(grid.Slots, s => s.Start.Hour == 2);
        Assert.Equal(new[] { 0, 1, 3 }, grid.Slots.Take(3).Select(s => s.Start.Hour));
        Assert.Equal(TimeSpan.FromHours(2), grid.Slots[2].Start.Offset);
    }

    [Fact]
    public void A_25_hour_day_has_25_hourly_slots_and_02_00_is_there_twice()
    {
        // Why: 2026-10-25 repeats 02:00-03:00 (CEST then CET). Both are real hours with their own price and PV; dropping one loses an hour of the plan.
        var grid = Day(Warsaw, 2026, 10, 25, Hourly);

        Assert.Equal(25, grid.Count);
        Assert.Equal(TimeSpan.FromHours(25), grid.Slots.Aggregate(TimeSpan.Zero, (sum, s) => sum + s.Duration));
        var twice = grid.Slots.Where(s => s.Start.Hour == 2).ToList();
        Assert.Equal(2, twice.Count);
        Assert.Equal(TimeSpan.FromHours(2), twice[0].Start.Offset);
        Assert.Equal(TimeSpan.FromHours(1), twice[1].Start.Offset);
        Assert.Equal(Utc(2026, 10, 25, 0), twice[0].Start);
        Assert.Equal(Utc(2026, 10, 25, 1), twice[1].Start);
    }

    [Fact]
    public void An_ordinary_day_has_24_slots_and_finer_slots_divide_it_exactly()
    {
        // Why: the 15 m and 30 m slot sizes use the same code (open question 4); the counts prove the boundaries stay on the local clock.
        Assert.Equal(24, Day(Warsaw, 2026, 7, 15, Hourly).Count);
        Assert.Equal(48, Day(Warsaw, 2026, 7, 15, TimeSpan.FromMinutes(30)).Count);
        Assert.Equal(96, Day(Warsaw, 2026, 7, 15, TimeSpan.FromMinutes(15)).Count);
        Assert.Equal(92, Day(Warsaw, 2026, 3, 29, TimeSpan.FromMinutes(15)).Count);
        Assert.Equal(100, Day(Warsaw, 2026, 10, 25, TimeSpan.FromMinutes(15)).Count);
    }

    [Fact]
    public void A_zone_with_a_half_hour_offset_still_cuts_on_its_local_hours()
    {
        // Why: slots are local slots, not UTC ones. In UTC+5:30 an hourly slot starts at :30 UTC; a UTC-aligned grid would straddle every local hour.
        var kolkata = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var grid = Day(kolkata, 2026, 7, 15, Hourly);

        Assert.Equal(24, grid.Count);
        Assert.All(grid.Slots, s => Assert.Equal(0, s.Start.Minute));
        Assert.All(grid.Slots, s => Assert.Equal(30, s.Start.UtcDateTime.Minute));
    }

    [Fact]
    public void A_half_hour_DST_step_is_cut_at_the_transition_and_leaves_a_30_minute_slot()
    {
        // Why: Lord Howe moves 02:00 to 02:30 on 2026-10-04 (a 23.5-hour day). The grid must cut at the transition and the day must add up to 23.5 hours.
        var lordHowe = TimeZoneInfo.FindSystemTimeZoneById("Australia/Lord_Howe");
        var grid = Day(lordHowe, 2026, 10, 4, Hourly);

        Assert.Equal(24, grid.Count);
        Assert.Equal(TimeSpan.FromHours(23.5), grid.Slots.Aggregate(TimeSpan.Zero, (sum, s) => sum + s.Duration));
        var short1 = Assert.Single(grid.Slots, s => s.Duration != Hourly);
        Assert.Equal(TimeSpan.FromMinutes(30), short1.Duration);
        Assert.Equal(2, short1.Start.Hour);
        Assert.Equal(30, short1.Start.Minute);
    }

    [Fact]
    public void The_first_slot_is_shortened_to_now_and_the_rest_start_on_local_boundaries()
    {
        // Why: the part of the current hour that is already over cannot be planned; the first slot is [now, next boundary).
        var now = new DateTimeOffset(2026, 7, 15, 10, 20, 0, TimeSpan.FromHours(2));
        var grid = SlotGrid.Build(now, now.AddHours(3), Hourly, Warsaw);

        Assert.Equal(4, grid.Count);
        Assert.Equal(now, grid.Slots[0].Start);
        Assert.Equal(TimeSpan.FromMinutes(40), grid.Slots[0].Duration);
        Assert.Equal(new[] { 11, 12, 13 }, grid.Slots.Skip(1).Select(s => s.Start.Hour));
        Assert.Equal(TimeSpan.FromMinutes(20), grid.Slots[3].Duration);
    }

    [Fact]
    public void A_deadline_cuts_its_slot_in_two()
    {
        // Why: a 06:30 deadline leaves half of the 06:00 slot (15-PLANNER): energy placed in the second half would arrive after the car left.
        var now = new DateTimeOffset(2026, 10, 26, 22, 0, 0, TimeSpan.FromHours(1));
        var deadline = new DateTimeOffset(2026, 10, 27, 6, 30, 0, TimeSpan.FromHours(1));
        var grid = SlotGrid.Build(now, now.AddHours(12), Hourly, Warsaw, [deadline]);

        var before = Assert.Single(grid.Slots, s => s.End == deadline);
        Assert.Equal(TimeSpan.FromMinutes(30), before.Duration);
        Assert.Equal(6, before.Start.Hour);
        var after = Assert.Single(grid.Slots, s => s.Start == deadline);
        Assert.Equal(TimeSpan.FromMinutes(30), after.Duration);
        Assert.Equal(13, grid.Count);
    }

    [Fact]
    public void A_cut_on_an_existing_edge_or_outside_the_horizon_changes_nothing()
    {
        // Why: a deadline that is already a boundary, or past the horizon, must not create empty or duplicate slots.
        var now = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.FromHours(2));
        var plain = SlotGrid.Build(now, now.AddHours(4), Hourly, Warsaw);
        var cut = SlotGrid.Build(now, now.AddHours(4), Hourly, Warsaw, [now.AddHours(2), now.AddHours(40), now.AddHours(-3), now]);

        Assert.Equal(plain.Slots, cut.Slots);
    }

    [Fact]
    public void The_horizon_end_cuts_the_last_slot_and_a_horizon_not_after_now_is_an_empty_grid()
    {
        // Why: a 48 h horizon from 10:20 ends at 10:20; the plan must not own energy beyond it.
        var now = new DateTimeOffset(2026, 7, 15, 10, 20, 0, TimeSpan.FromHours(2));
        var grid = SlotGrid.Build(now, now.AddHours(48), Hourly, Warsaw);

        Assert.Equal(49, grid.Count);
        Assert.Equal(now.AddHours(48), grid.Slots[^1].End);
        Assert.Equal(TimeSpan.FromMinutes(20), grid.Slots[^1].Duration);
        Assert.Equal(0, SlotGrid.Build(now, now, Hourly, Warsaw).Count);
        Assert.Equal(0, SlotGrid.Build(now, now.AddHours(-1), Hourly, Warsaw).Count);
    }

    [Fact]
    public void Slots_are_contiguous_across_the_clock_change_and_IndexOf_finds_each_instant()
    {
        // Why: a gap or an overlap between slots would double-count or drop energy; IndexOf is how a clock reading finds its slot.
        var now = new DateTimeOffset(2026, 10, 24, 12, 30, 0, TimeSpan.FromHours(2));
        var grid = SlotGrid.Build(now, now.AddHours(48), Hourly, Warsaw);

        for (var i = 1; i < grid.Count; i++)
        {
            Assert.Equal(grid.Slots[i - 1].End, grid.Slots[i].Start);
        }

        Assert.Equal(0, grid.IndexOf(now));
        Assert.Equal(grid.Count - 1, grid.IndexOf(now.AddHours(48).AddTicks(-1)));
        Assert.Equal(-1, grid.IndexOf(now.AddHours(48)));
        Assert.Equal(-1, grid.IndexOf(now.AddTicks(-1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(90)]
    [InlineData(120)]
    public void A_slot_that_does_not_divide_an_hour_is_refused(int minutes)
    {
        // Why: slots are cut on local boundaries; a 7-minute slot has no boundary that survives the next hour.
        var now = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.FromHours(2));

        Assert.Throws<ArgumentOutOfRangeException>(() => SlotGrid.Build(now, now.AddHours(2), TimeSpan.FromMinutes(minutes), Warsaw));
    }
}
