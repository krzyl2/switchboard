using Switchboard.Application.Configuration;
using Switchboard.Domain.Economics;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Domain;

/// <summary>
/// ECO-01, ECO-16: the tariff says which zone and price are in force on the LOCAL clock. A wrong zone is
/// a wrong price, and later a start from the grid in a peak zone; so the table of the shipped G13 template
/// is held at every boundary minute, on both sides of the month change, on weekends and holidays, and the
/// 23- and 25-hour days must neither lose nor double a zone.
/// </summary>
public sealed class TariffTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    private static readonly string G13Snippet = File.ReadAllText(RepoPaths.Combine("deploy", "examples", "economics-g13.yaml"));

    private static readonly EconomicsDefinition G13Definition =
        TestConfigurations.Valid(TestConfigurations.ThreePhase() + "\n" + G13Snippet).Configuration!.Economics!;

    private static readonly Tariff G13 = new(G13Definition.Tariff!, G13Definition.Holidays, Warsaw);

    /// <summary>A local Warsaw wall-clock time as an instant (an ambiguous time takes the first, summer-time, occurrence).</summary>
    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute)
    {
        var wall = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(wall, Warsaw), TimeSpan.Zero);
    }

    private static string? Zone(DateTimeOffset t) => G13.ZoneAt(t)?.Id;

    [Theory]
    [InlineData(2026, 7, 15, 6, 59, "offpeak")]
    [InlineData(2026, 7, 15, 7, 0, "morning_peak")]
    [InlineData(2026, 7, 15, 12, 59, "morning_peak")]
    [InlineData(2026, 7, 15, 13, 0, "offpeak")]
    [InlineData(2026, 7, 15, 18, 59, "offpeak")]
    [InlineData(2026, 7, 15, 19, 0, "afternoon_peak")]
    [InlineData(2026, 7, 15, 21, 59, "afternoon_peak")]
    [InlineData(2026, 7, 15, 22, 0, "offpeak")]
    [InlineData(2026, 1, 14, 6, 59, "offpeak")]
    [InlineData(2026, 1, 14, 7, 0, "morning_peak")]
    [InlineData(2026, 1, 14, 12, 59, "morning_peak")]
    [InlineData(2026, 1, 14, 13, 0, "offpeak")]
    [InlineData(2026, 1, 14, 15, 59, "offpeak")]
    [InlineData(2026, 1, 14, 16, 0, "afternoon_peak")]
    [InlineData(2026, 1, 14, 20, 59, "afternoon_peak")]
    [InlineData(2026, 1, 14, 21, 0, "offpeak")]
    public void The_G13_table_changes_zone_exactly_on_its_boundary_minutes(int year, int month, int day, int hour, int minute, string zone)
    {
        // A boundary off by one minute prices that minute's energy in the wrong zone.
        Assert.Equal(zone, Zone(Local(year, month, day, hour, minute)));
    }

    [Theory]
    [InlineData(2026, 3, 31, 16, 30, "afternoon_peak")]
    [InlineData(2026, 4, 1, 16, 30, "offpeak")]
    [InlineData(2026, 3, 31, 21, 30, "offpeak")]
    [InlineData(2026, 4, 1, 21, 30, "afternoon_peak")]
    [InlineData(2026, 9, 30, 16, 30, "offpeak")]
    [InlineData(2026, 10, 1, 16, 30, "afternoon_peak")]
    [InlineData(2026, 9, 30, 21, 30, "afternoon_peak")]
    [InlineData(2026, 10, 1, 21, 30, "offpeak")]
    public void The_afternoon_peak_moves_with_the_month_on_both_sides_of_the_change(int year, int month, int day, int hour, int minute, string zone)
    {
        // The summer and winter hours differ; the day the month turns is where a months list is easiest to get wrong.
        Assert.Equal(zone, Zone(Local(year, month, day, hour, minute)));
    }

    [Theory]
    [InlineData(2026, 7, 18, 8, 0)]
    [InlineData(2026, 7, 19, 20, 0)]
    [InlineData(2026, 1, 17, 17, 0)]
    public void A_weekend_is_off_peak_all_day(int year, int month, int day, int hour, int minute)
    {
        // The whole point of a time-of-use tariff for a flexible load: weekends are the cheap zone.
        Assert.Equal("offpeak", Zone(Local(year, month, day, hour, minute)));
    }

    [Theory]
    [InlineData(2026, 11, 11, 8, 0)]
    [InlineData(2026, 12, 24, 17, 0)]
    public void A_holiday_on_a_weekday_is_priced_as_off_peak(int year, int month, int day, int hour, int minute)
    {
        // 2026-11-11 is a Wednesday: only the holiday list keeps it out of the morning peak.
        Assert.Equal(DayOfWeek.Wednesday, new DateOnly(2026, 11, 11).DayOfWeek);
        Assert.Equal("offpeak", Zone(Local(year, month, day, hour, minute)));
    }

    [Fact]
    public void The_day_after_a_holiday_is_an_ordinary_workday()
    {
        Assert.Equal("morning_peak", Zone(Local(2026, 11, 12, 8, 0)));
    }

    [Fact]
    public void Price_at_carries_the_zone_price_and_its_basis()
    {
        var peak = G13.PriceAt(Local(2026, 7, 15, 8, 0));
        var night = G13.PriceAt(Local(2026, 7, 15, 23, 0));

        Assert.Equal(new PricePoint(1.10, PriceBasis.Zone, "morning_peak"), peak);
        Assert.Equal(new PricePoint(0.60, PriceBasis.Zone, "offpeak"), night);
    }

    [Fact]
    public void A_flat_price_has_one_price_no_zone_and_never_changes()
    {
        var flat = new Tariff(new FlatPrice(0.80), HolidayCalendar.Empty, Warsaw);
        var at = Local(2026, 7, 15, 8, 0);

        Assert.Equal(new PricePoint(0.80, PriceBasis.Flat, null), flat.PriceAt(at));
        Assert.Null(flat.ZoneAt(at));
        Assert.Null(flat.NextChange(at));
    }

    [Fact]
    public void Next_change_finds_the_following_boundary_inside_a_day()
    {
        Assert.Equal(new TariffChange(Local(2026, 7, 15, 7, 0), "morning_peak"), G13.NextChange(Local(2026, 7, 15, 6, 0)));
        Assert.Equal(new TariffChange(Local(2026, 7, 15, 13, 0), "offpeak"), G13.NextChange(Local(2026, 7, 15, 12, 59)));
        Assert.Equal(new TariffChange(Local(2026, 7, 15, 22, 0), "offpeak"), G13.NextChange(Local(2026, 7, 15, 19, 30)));
        Assert.Equal(new TariffChange(Local(2026, 1, 14, 16, 0), "afternoon_peak"), G13.NextChange(Local(2026, 1, 14, 13, 0)));
    }

    [Fact]
    public void Next_change_ignores_seconds_and_never_returns_now()
    {
        var at = Local(2026, 7, 15, 12, 59).AddSeconds(41);

        var next = G13.NextChange(at);

        Assert.Equal(Local(2026, 7, 15, 13, 0), next!.Value.At);
        Assert.True(next.Value.At > at);
    }

    [Fact]
    public void Next_change_skips_a_whole_weekend_and_a_holiday()
    {
        // Friday evening: off-peak until Monday 07:00. The 11th is a holiday: Tuesday night runs to Thursday 07:00.
        Assert.Equal(new TariffChange(Local(2026, 7, 20, 7, 0), "morning_peak"), G13.NextChange(Local(2026, 7, 17, 22, 30)));
        Assert.Equal(new TariffChange(Local(2026, 11, 12, 7, 0), "morning_peak"), G13.NextChange(Local(2026, 11, 10, 23, 0)));
    }

    [Fact]
    public void Next_change_crosses_the_month_change_that_moves_the_peak()
    {
        // From the evening of 31 March (winter hours already over) the next afternoon peak is the 1st of April at 19:00, not 16:00.
        Assert.Equal(new TariffChange(Local(2026, 4, 1, 7, 0), "morning_peak"), G13.NextChange(Local(2026, 3, 31, 22, 0)));
        Assert.Equal(new TariffChange(Local(2026, 4, 1, 13, 0), "offpeak"), G13.NextChange(Local(2026, 4, 1, 7, 0)));
        Assert.Equal(new TariffChange(Local(2026, 4, 1, 19, 0), "afternoon_peak"), G13.NextChange(Local(2026, 4, 1, 13, 0)));
    }

    // ----- schedules built directly: wrap past midnight, default zone, DST -----

    private static ZoneRule Rule(string hours, DayClass[]? days = null, int[]? months = null) =>
        new(days is null ? [] : [.. days], months is null ? [] : [.. months], [ParseHours(hours)]);

    private static HoursRange ParseHours(string text)
    {
        Assert.True(HoursRange.TryParse(text, out var range));
        return range;
    }

    private static Tariff Night(string hours, string? defaultZone = "day", TimeZoneInfo? zone = null) =>
        new(
            new ZonedPrice(
                [new TariffZone("night", 0.4, [Rule(hours)]), new TariffZone("day", 1.0, [])],
                defaultZone),
            HolidayCalendar.Empty,
            zone ?? Warsaw);

    [Fact]
    public void A_range_past_midnight_wraps_and_its_end_is_exclusive()
    {
        var night = Night("22:00-06:00");

        Assert.Equal("day", night.ZoneAt(Local(2026, 7, 15, 21, 59))!.Id);
        Assert.Equal("night", night.ZoneAt(Local(2026, 7, 15, 22, 0))!.Id);
        Assert.Equal("night", night.ZoneAt(Local(2026, 7, 15, 23, 59))!.Id);
        Assert.Equal("night", night.ZoneAt(Local(2026, 7, 16, 0, 0))!.Id);
        Assert.Equal("night", night.ZoneAt(Local(2026, 7, 16, 5, 59))!.Id);
        Assert.Equal("day", night.ZoneAt(Local(2026, 7, 16, 6, 0))!.Id);
        Assert.Equal(new TariffChange(Local(2026, 7, 16, 6, 0), "day"), night.NextChange(Local(2026, 7, 15, 23, 0)));
    }

    [Fact]
    public void Twenty_four_hundred_as_an_end_means_the_end_of_the_day()
    {
        var evening = Night("18:00-24:00");

        Assert.Equal("night", evening.ZoneAt(Local(2026, 7, 15, 23, 59))!.Id);
        Assert.Equal("day", evening.ZoneAt(Local(2026, 7, 16, 0, 0))!.Id);
        Assert.Equal(new TariffChange(Local(2026, 7, 16, 0, 0), "day"), evening.NextChange(Local(2026, 7, 15, 20, 0)));
    }

    [Fact]
    public void The_first_listed_zone_wins_and_the_default_zone_takes_the_rest()
    {
        var overlapping = new Tariff(
            new ZonedPrice(
                [new TariffZone("a", 1, [Rule("08:00-12:00")]), new TariffZone("b", 2, [Rule("10:00-14:00")]), new TariffZone("rest", 3, [])],
                "rest"),
            HolidayCalendar.Empty,
            Warsaw);

        Assert.Equal("a", overlapping.ZoneAt(Local(2026, 7, 15, 11, 0))!.Id);
        Assert.Equal("b", overlapping.ZoneAt(Local(2026, 7, 15, 12, 0))!.Id);
        Assert.Equal("rest", overlapping.ZoneAt(Local(2026, 7, 15, 14, 0))!.Id);
    }

    [Fact]
    public void A_weekday_name_matches_that_weekday_even_on_a_holiday()
    {
        var date = new DateOnly(2026, 11, 11);
        var tariff = new Tariff(
            new ZonedPrice([new TariffZone("wed", 1, [Rule("00:00-24:00", [DayClass.Wednesday])]), new TariffZone("rest", 2, [])], "rest"),
            new HolidayCalendar([date]),
            Warsaw);

        Assert.Equal("wed", tariff.ZoneAt(Local(2026, 11, 11, 12, 0))!.Id);
        Assert.Equal("rest", tariff.ZoneAt(Local(2026, 11, 12, 12, 0))!.Id);
    }

    [Fact]
    public void The_lost_hour_of_a_23_hour_day_has_no_wall_clock_minute_and_a_rule_for_it_never_matches()
    {
        // 2026-03-29: 02:00 jumps to 03:00. A 02:00-03:00 zone is not in force for a single minute of that day.
        var night = Night("02:00-03:00");
        var midnight = Local(2026, 3, 29, 0, 0);
        var nextMidnight = Local(2026, 3, 30, 0, 0);

        var minutes = Enumerable.Range(0, (int)(nextMidnight - midnight).TotalMinutes).Select(i => midnight.AddMinutes(i)).ToList();

        Assert.Equal(23 * 60, minutes.Count);
        Assert.DoesNotContain(minutes, t => night.ZoneAt(t)!.Id == "night");
        Assert.Equal("day", night.ZoneAt(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero))!.Id);
    }

    [Fact]
    public void A_rule_for_the_second_hour_still_ends_on_time_after_the_jump()
    {
        // 03:00-04:00 on the short day is the hour after the lost one: 03:00 CEST = 01:00 UTC.
        var zone = Night("03:00-04:00");

        Assert.Equal("day", zone.ZoneAt(new DateTimeOffset(2026, 3, 29, 0, 59, 0, TimeSpan.Zero))!.Id);
        Assert.Equal("night", zone.ZoneAt(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero))!.Id);
        Assert.Equal("night", zone.ZoneAt(new DateTimeOffset(2026, 3, 29, 1, 59, 0, TimeSpan.Zero))!.Id);
        Assert.Equal("day", zone.ZoneAt(new DateTimeOffset(2026, 3, 29, 2, 0, 0, TimeSpan.Zero))!.Id);
    }

    [Fact]
    public void Next_change_over_the_jump_of_a_23_hour_day_skips_the_lost_hour()
    {
        var night = Night("02:00-03:00");

        var change = night.NextChange(Local(2026, 3, 29, 1, 30));

        // The lost hour never happens: the next night zone is the following day's 02:00 (CEST).
        Assert.Equal(new TariffChange(Local(2026, 3, 30, 2, 0), "night"), change);
    }

    [Fact]
    public void The_repeated_hour_of_a_25_hour_day_matches_a_rule_for_it_both_times()
    {
        // 2026-10-25: 02:00-03:00 happens twice. The zone is in force for 120 minutes in a day of 1500.
        var night = Night("02:00-03:00");
        var midnight = Local(2026, 10, 25, 0, 0);
        var nextMidnight = Local(2026, 10, 26, 0, 0);

        var minutes = Enumerable.Range(0, (int)(nextMidnight - midnight).TotalMinutes).Select(i => midnight.AddMinutes(i)).ToList();

        Assert.Equal(25 * 60, minutes.Count);
        Assert.Equal(120, minutes.Count(t => night.ZoneAt(t)!.Id == "night"));
        var first = new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.FromHours(2));
        var second = new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.FromHours(1));
        Assert.Equal("night", night.ZoneAt(first)!.Id);
        Assert.Equal("night", night.ZoneAt(second)!.Id);
    }

    [Fact]
    public void Next_change_in_the_repeated_hour_reports_the_end_of_the_second_occurrence()
    {
        var night = Night("02:00-03:00");

        var enter = night.NextChange(new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.FromHours(2)));
        var leave = night.NextChange(new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.FromHours(2)));
        var inSecond = night.NextChange(new DateTimeOffset(2026, 10, 25, 2, 10, 0, TimeSpan.FromHours(1)));

        // Entering at the first 02:00; leaving at 03:00 CET, an hour of clock time after the first 02:59 and not at the first 03:00.
        Assert.Equal(new TariffChange(new DateTimeOffset(2026, 10, 25, 2, 0, 0, TimeSpan.FromHours(2)), "night"), enter);
        Assert.Equal(new TariffChange(new DateTimeOffset(2026, 10, 25, 3, 0, 0, TimeSpan.FromHours(1)), "day"), leave);
        Assert.Equal(leave, inSecond);
    }

    [Fact]
    public void The_zone_is_read_from_the_injected_time_zone_not_from_the_machine()
    {
        // The same instant is night in one zone and day in another: the schedule only knows the zone it is given.
        var utc = Night("22:00-06:00", zone: TimeZoneInfo.Utc);
        var instant = new DateTimeOffset(2026, 7, 15, 21, 0, 0, TimeSpan.Zero); // 23:00 in Warsaw (CEST)

        Assert.Equal("day", utc.ZoneAt(instant)!.Id);
        Assert.Equal("night", Night("22:00-06:00").ZoneAt(instant)!.Id);
    }

    [Fact]
    public void A_moment_no_zone_claims_without_a_default_has_no_zone_and_no_price()
    {
        var gappy = Night("22:00-06:00", defaultZone: null);

        Assert.Null(gappy.ZoneAt(Local(2026, 7, 15, 12, 0)));
        Assert.Null(gappy.PriceAt(Local(2026, 7, 15, 12, 0)));
    }

    [Fact]
    public void Find_gap_reports_uncovered_time_and_none_when_the_zones_cover_everything()
    {
        var gappy = new ZonedPrice([new TariffZone("n", 1, [Rule("22:00-06:00")])], null);
        var covered = new ZonedPrice([new TariffZone("n", 1, [Rule("22:00-06:00")]), new TariffZone("d", 2, [Rule("06:00-22:00")])], null);

        Assert.Equal(6 * 60, gappy.FindGap()!.Value.MinuteOfDay);
        Assert.Null(covered.FindGap());
    }

    [Fact]
    public void Find_gap_sees_a_hole_that_exists_only_on_holidays_or_in_one_month()
    {
        var workdaysOnly = new ZonedPrice([new TariffZone("w", 1, [Rule("00:00-24:00", [DayClass.Workday]), Rule("00:00-24:00", [DayClass.Weekend])])], null);
        var noJuly = new ZonedPrice([new TariffZone("a", 1, [Rule("00:00-24:00", months: [1, 2, 3, 4, 5, 6, 8, 9, 10, 11, 12])])], null);

        Assert.True(workdaysOnly.FindGap()!.Value.Holiday);
        Assert.Equal(7, noJuly.FindGap()!.Value.Month);
    }

    [Theory]
    [InlineData("07:00-13:00", true, 420, 780)]
    [InlineData("22:00-06:00", true, 1320, 360)]
    [InlineData("00:00-24:00", true, 0, 1440)]
    [InlineData("0:00-6:00", false, 0, 0)]
    [InlineData("07:00-07:00", false, 0, 0)]
    [InlineData("24:00-06:00", false, 0, 0)]
    [InlineData("07:00-24:01", false, 0, 0)]
    [InlineData("07:60-08:00", false, 0, 0)]
    [InlineData("07:00", false, 0, 0)]
    [InlineData("07:00-00:00", false, 0, 0)]
    [InlineData("", false, 0, 0)]
    public void Hours_parse_strictly(string text, bool valid, int start, int end)
    {
        Assert.Equal(valid, HoursRange.TryParse(text, out var range));
        if (valid)
        {
            Assert.Equal((start, end), (range.StartMinute, range.EndMinute));
        }
    }

    [Fact]
    public void Holiday_list_knows_its_years()
    {
        var calendar = new HolidayCalendar([new DateOnly(2026, 11, 1)]);

        Assert.True(calendar.IsHoliday(new DateOnly(2026, 11, 1)));
        Assert.False(calendar.IsHoliday(new DateOnly(2027, 11, 1)));
        Assert.False(calendar.HasNoDateIn(2026));
        Assert.True(calendar.HasNoDateIn(2027));
        Assert.True(HolidayCalendar.Empty.HasNoDateIn(2026));
    }
}
