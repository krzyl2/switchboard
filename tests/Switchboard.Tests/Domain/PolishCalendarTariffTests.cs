using Switchboard.Application.Configuration;
using Switchboard.Domain.Economics;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Domain;

/// <summary>
/// ECO-16 preparation: the G13 template on the Polish calendar. The add-on has no holiday knowledge of its own
/// (14-ECONOMICS: a dated list in configuration), so these tests hold two things: a list that names the Polish
/// statutory days off (movable ones, 24 December from 2025) makes those weekdays off-peak, and the shipped
/// example's list for the rest of 2026 is complete. A missing date prices a day off at the morning peak.
/// </summary>
public sealed class PolishCalendarTariffTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    private static readonly string G13Snippet = File.ReadAllText(RepoPaths.Combine("deploy", "examples", "economics-g13.yaml"));

    private static readonly EconomicsDefinition Example =
        TestConfigurations.Valid(TestConfigurations.ThreePhase() + "\n" + G13Snippet).Configuration!.Economics!;

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute)
    {
        var wall = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(wall, Warsaw), TimeSpan.Zero);
    }

    /// <summary>Western Easter Sunday (anonymous Gregorian algorithm): the movable holidays are computed, not typed, so the test is an oracle.</summary>
    private static DateOnly EasterSunday(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = ((19 * a) + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + (2 * e) + (2 * i) - h - k) % 7;
        var m = (a + (11 * h) + (22 * l)) / 451;
        var month = (h + l - (7 * m) + 114) / 31;
        var day = ((h + l - (7 * m) + 114) % 31) + 1;
        return new DateOnly(year, month, day);
    }

    /// <summary>The statutory days off of a year, ustawa o dniach wolnych od pracy, with 24 December from 2025.</summary>
    private static List<DateOnly> PolishHolidays(int year)
    {
        var easter = EasterSunday(year);
        var list = new List<DateOnly>
        {
            new(year, 1, 1), new(year, 1, 6), easter, easter.AddDays(1), new(year, 5, 1), new(year, 5, 3),
            easter.AddDays(49), easter.AddDays(60), new(year, 8, 15), new(year, 11, 1), new(year, 11, 11),
            new(year, 12, 25), new(year, 12, 26),
        };
        if (year >= 2025)
        {
            list.Add(new DateOnly(year, 12, 24));
        }

        return list;
    }

    private static Tariff G13With(IEnumerable<DateOnly> holidays) =>
        new(Example.Tariff!, new HolidayCalendar(holidays), Warsaw);

    [Fact]
    public void Easter_oracle_matches_known_dates()
    {
        // 2026-04-05 and 2027-03-28 are the published Easter Sundays; the other tests lean on this oracle.
        Assert.Equal(new DateOnly(2026, 4, 5), EasterSunday(2026));
        Assert.Equal(new DateOnly(2027, 3, 28), EasterSunday(2027));
    }

    [Theory]
    [InlineData(2026)]
    [InlineData(2027)]
    [InlineData(2028)]
    public void Every_weekday_holiday_of_a_Polish_list_is_off_peak_all_day(int year)
    {
        // Easter Monday, Corpus Christi (a Thursday), Christmas Eve from 2025: weekdays the weekend rule cannot catch.
        var tariff = G13With(PolishHolidays(year));
        var weekdayHolidays = PolishHolidays(year).Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).ToList();

        Assert.Contains(EasterSunday(year).AddDays(1), weekdayHolidays);
        Assert.Contains(EasterSunday(year).AddDays(60), weekdayHolidays);
        foreach (var date in weekdayHolidays)
        {
            foreach (var hour in new[] { 8, 12, 17, 20 })
            {
                Assert.True(
                    tariff.ZoneAt(Local(date.Year, date.Month, date.Day, hour, 0))!.Id == "offpeak",
                    $"{date:yyyy-MM-dd} {hour}:00 must be off-peak");
            }
        }
    }

    [Fact]
    public void Without_the_date_a_weekday_holiday_is_priced_as_a_workday()
    {
        // Why the list matters and why a stale one is warned about: Easter Monday 2027 left out is the morning peak price.
        var easterMonday = EasterSunday(2027).AddDays(1);
        var at = Local(easterMonday.Year, easterMonday.Month, easterMonday.Day, 8, 0);

        Assert.Equal("morning_peak", G13With([]).ZoneAt(at)!.Id);
        Assert.Equal("offpeak", G13With([easterMonday]).ZoneAt(at)!.Id);
    }

    [Fact]
    public void Pentecost_is_a_sunday_and_needs_no_list_entry()
    {
        var pentecost = EasterSunday(2027).AddDays(49);

        Assert.Equal(DayOfWeek.Sunday, pentecost.DayOfWeek);
        Assert.Equal("offpeak", G13With([]).ZoneAt(Local(pentecost.Year, pentecost.Month, pentecost.Day, 8, 0))!.Id);
    }

    [Fact]
    public void The_shipped_example_lists_every_Polish_holiday_left_in_2026()
    {
        // The example is pasted into a live file: a missing date prices a day off at a peak rate.
        var expected = PolishHolidays(2026).Where(d => d >= new DateOnly(2026, 11, 1));

        Assert.All(expected, d => Assert.True(Example.Holidays.IsHoliday(d), $"{d:yyyy-MM-dd} missing from the example holidays"));
    }

    [Fact]
    public void The_day_before_and_after_a_weekday_holiday_keep_their_peaks()
    {
        // Christmas Eve 2026 is a Thursday: Wednesday 23rd is a peak day, Monday 28th too (27th is a Sunday).
        var tariff = G13With(PolishHolidays(2026));

        Assert.Equal("afternoon_peak", tariff.ZoneAt(Local(2026, 12, 23, 17, 0))!.Id);
        Assert.Equal("offpeak", tariff.ZoneAt(Local(2026, 12, 24, 17, 0))!.Id);
        Assert.Equal("morning_peak", tariff.ZoneAt(Local(2026, 12, 28, 8, 0))!.Id);
    }

    // ----- the month change and the clock change on the real G13 table -----

    [Theory]
    [InlineData(2027, 4, 1)]   // Thursday: first day of summer hours
    [InlineData(2027, 10, 1)]  // Friday: first day of winter hours
    public void The_midnight_of_a_season_change_does_not_carry_the_old_season_over(int year, int month, int day)
    {
        var tariff = G13With([]);
        var summer = month == 4;

        // 16:00-19:00 is a peak only in winter; 21:00-22:00 only in summer.
        Assert.Equal(summer ? "offpeak" : "afternoon_peak", tariff.ZoneAt(Local(year, month, day, 17, 0))!.Id);
        Assert.Equal(summer ? "afternoon_peak" : "offpeak", tariff.ZoneAt(Local(year, month, day, 21, 30))!.Id);
        // The evening before is the other season.
        var before = new DateOnly(year, month, day).AddDays(-1);
        Assert.Equal(summer ? "afternoon_peak" : "offpeak", tariff.ZoneAt(Local(before.Year, before.Month, before.Day, 17, 0))!.Id);
    }

    [Fact]
    public void The_23_hour_Sunday_is_off_peak_all_day_and_the_week_starts_on_time_after_it()
    {
        // 2026-03-29, Sunday: clocks go forward. 1380 minutes, no zone change; Monday 07:00 CEST is 05:00 UTC, not 06:00.
        var tariff = G13With([]);
        var start = Local(2026, 3, 29, 0, 0);
        var minutes = Enumerable.Range(0, 23 * 60).Select(i => start.AddMinutes(i)).ToList();

        Assert.Equal(start.AddHours(23), Local(2026, 3, 30, 0, 0));
        Assert.All(minutes, t => Assert.Equal("offpeak", tariff.ZoneAt(t)!.Id));
        Assert.Equal(
            new TariffChange(new DateTimeOffset(2026, 3, 30, 5, 0, 0, TimeSpan.Zero), "morning_peak"),
            tariff.NextChange(Local(2026, 3, 29, 12, 0)));
        Assert.Equal("offpeak", tariff.ZoneAt(new DateTimeOffset(2026, 3, 30, 4, 59, 0, TimeSpan.Zero))!.Id);
    }

    [Fact]
    public void The_25_hour_Sunday_is_off_peak_all_day_and_the_week_starts_on_time_after_it()
    {
        // 2026-10-25, Sunday: clocks go back. 1500 minutes, no zone change; Monday 07:00 CET is 06:00 UTC, not 05:00.
        var tariff = G13With([]);
        var start = Local(2026, 10, 25, 0, 0);
        var minutes = Enumerable.Range(0, 25 * 60).Select(i => start.AddMinutes(i)).ToList();

        Assert.Equal(start.AddHours(25), Local(2026, 10, 26, 0, 0));
        Assert.All(minutes, t => Assert.Equal("offpeak", tariff.ZoneAt(t)!.Id));
        Assert.Equal(
            new TariffChange(new DateTimeOffset(2026, 10, 26, 6, 0, 0, TimeSpan.Zero), "morning_peak"),
            tariff.NextChange(Local(2026, 10, 25, 12, 0)));
        Assert.Equal("offpeak", tariff.ZoneAt(new DateTimeOffset(2026, 10, 26, 5, 59, 0, TimeSpan.Zero))!.Id);
    }

    [Fact]
    public void The_first_winter_Monday_after_the_clock_change_has_its_peak_at_16_local()
    {
        // 2026-10-26 is the day after the 25-hour Sunday, already winter hours: 16:00 CET = 15:00 UTC.
        var tariff = G13With([]);

        Assert.Equal("offpeak", tariff.ZoneAt(new DateTimeOffset(2026, 10, 26, 14, 59, 0, TimeSpan.Zero))!.Id);
        Assert.Equal("afternoon_peak", tariff.ZoneAt(new DateTimeOffset(2026, 10, 26, 15, 0, 0, TimeSpan.Zero))!.Id);
        Assert.Equal("offpeak", tariff.ZoneAt(new DateTimeOffset(2026, 10, 26, 20, 0, 0, TimeSpan.Zero))!.Id);
    }
}
