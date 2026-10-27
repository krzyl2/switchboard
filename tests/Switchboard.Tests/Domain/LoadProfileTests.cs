using System.Collections.Immutable;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Planning;
using Switchboard.Domain.Units;

namespace Switchboard.Tests.Domain;

/// <summary>
/// The hour-of-week load profile and the forecast taken from it (15-PLANNER, "Load forecast from statistics"). Why it matters: the plan's surplus is PV
/// minus this base load, so a bin filled twice by the repeated 02:00 hour, a holiday read as a workday, or an unobserved hour counted as zero load would
/// each put the surplus (and the car's charge slots) in the wrong place.
/// </summary>
public sealed class LoadProfileTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly ForecastParameters Parameters = new(TimeSpan.FromMinutes(60), 0.85, TimeSpan.FromDays(14), 0.8, 400);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute = 0) => new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute = 0) =>
        new(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified), Warsaw.GetUtcOffset(new DateTime(year, month, day, hour, minute, 0)));

    /// <summary>Twelve 5-minute buckets of one hour, each carrying <paramref name="kwh"/> / 12.</summary>
    private static IEnumerable<LoadObservation> HourOf(DateTimeOffset start, double kwh, double observedFraction = 1.0) =>
        Enumerable.Range(0, 12).Select(i => new LoadObservation(
            start.AddMinutes(5 * i),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(5 * observedFraction),
            new KilowattHours(kwh / 12 * observedFraction)));

    private static int Bin(int dayMondayFirst, int hour) => (dayMondayFirst * 24) + hour;

    [Fact]
    public void A_fully_observed_hour_fills_its_bin_with_its_kwh_per_hour()
    {
        // Why: the basic unit. Monday 2026-10-26 10:00 local with 1.2 kWh of uncontrolled load is a 1.2 kW sample in the Monday-10 bin and no other.
        var start = Local(2026, 10, 26, 10);

        var profile = LoadProfile.Empty.Feed(HourOf(start, 1.2), Local(2026, 10, 26, 11), Parameters, Warsaw, HolidayCalendar.Empty);

        var bin = profile.Bins[Bin(0, 10)];
        Assert.Equal(1.2, bin.MeanKw, 9);
        Assert.Equal(1, bin.Samples);
        Assert.Equal(1.0, bin.Weight, 9);
        Assert.Equal(1, profile.FilledBins);
        Assert.Equal(Local(2026, 10, 26, 11), profile.FedThrough);
    }

    [Fact]
    public void The_mean_is_exponentially_weighted_with_the_configured_half_life()
    {
        // Why: the profile must follow a changed household without forgetting the weekly shape. With a 14-day half-life a sample from 14 days ago has weight 0.5:
        // (1.0 x 0.5 + 2.0 x 1) / (0.5 + 1) = 1.6667, and from 7 days ago weight 0.5^0.5 = 0.70711: (0.70711 + 2) / 1.70711 = 1.58579.
        var first = Local(2026, 11, 2, 10);
        var profile = LoadProfile.Empty.Observe(first, Hour, Hour, new KilowattHours(1.0), Parameters, Warsaw, HolidayCalendar.Empty);

        var after14 = profile.Observe(first.AddDays(14), Hour, Hour, new KilowattHours(2.0), Parameters, Warsaw, HolidayCalendar.Empty);
        var after7 = profile.Observe(first.AddDays(7), Hour, Hour, new KilowattHours(2.0), Parameters, Warsaw, HolidayCalendar.Empty);

        Assert.Equal((0.5 + 2.0) / 1.5, after14.Bins[Bin(0, 10)].MeanKw, 9);
        Assert.Equal(1.5, after14.Bins[Bin(0, 10)].Weight, 9);
        Assert.Equal(1.585786437, after7.Bins[Bin(0, 10)].MeanKw, 8);
        Assert.Equal(2, after7.Bins[Bin(0, 10)].Samples);
    }

    [Fact]
    public void An_hour_observed_less_than_the_minimum_fraction_is_not_counted()
    {
        // Why: min_observed_fraction 0.8. Unknown time is never counted as zero load; an hour with 47 of 60 minutes known (0.783) is too thin to learn from, 48 of 60 (0.8) is enough.
        var start = Local(2026, 10, 26, 10);
        var thin = LoadProfile.Empty.Observe(start, Hour, TimeSpan.FromMinutes(47), new KilowattHours(1.0), Parameters, Warsaw, HolidayCalendar.Empty);
        var enough = LoadProfile.Empty.Observe(start, Hour, TimeSpan.FromMinutes(48), new KilowattHours(0.96), Parameters, Warsaw, HolidayCalendar.Empty);

        Assert.Equal(0, thin.FilledBins);
        Assert.Equal(1, enough.FilledBins);
    }

    [Fact]
    public void The_sample_is_the_energy_per_observed_hour_so_a_gap_does_not_bias_the_load_low()
    {
        // Why: energy is integrated over observed time only. 0.9 kWh over 54 observed minutes is 1.0 kW; taking 0.9 as the hour's load would read the gap as 6 minutes of nothing.
        var start = Local(2026, 10, 26, 10);

        var profile = LoadProfile.Empty.Feed(HourOf(start, 1.0, observedFraction: 0.9), Local(2026, 10, 26, 11), Parameters, Warsaw, HolidayCalendar.Empty);

        Assert.Equal(1.0, profile.Bins[Bin(0, 10)].MeanKw, 9);
    }

    [Fact]
    public void A_negative_balance_counts_as_no_load_and_a_non_number_is_ignored()
    {
        // Why: the uncontrolled load is a difference of measurements and can dip below zero by noise; a negative base load would invent surplus. NaN never enters a mean.
        var start = Local(2026, 10, 26, 10);

        var negative = LoadProfile.Empty.Observe(start, Hour, Hour, new KilowattHours(-0.2), Parameters, Warsaw, HolidayCalendar.Empty);
        var nan = LoadProfile.Empty.Observe(start, Hour, Hour, new KilowattHours(double.NaN), Parameters, Warsaw, HolidayCalendar.Empty);

        Assert.Equal(0, negative.Bins[Bin(0, 10)].MeanKw);
        Assert.Equal(1, negative.Bins[Bin(0, 10)].Samples);
        Assert.Equal(0, nan.FilledBins);
    }

    [Fact]
    public void The_repeated_02_00_hour_of_a_25_hour_day_updates_the_same_bin_twice()
    {
        // Why: 2026-10-25 (Sunday) has two 02:00 hours. Both are real hours of that hour-of-week, so Sunday-02 gets two samples (1.0 and 3.0 kW); a feed that treated the second as a duplicate would lose half the evidence.
        var first = Utc(2026, 10, 25, 0);
        var second = Utc(2026, 10, 25, 1);

        var profile = LoadProfile.Empty.Feed(HourOf(first, 1.0).Concat(HourOf(second, 3.0)), Utc(2026, 10, 25, 2), Parameters, Warsaw, HolidayCalendar.Empty);

        var bin = profile.Bins[Bin(6, 2)];
        Assert.Equal(2, bin.Samples);
        Assert.Equal(2.0, bin.MeanKw, 2);
        Assert.Equal(Utc(2026, 10, 25, 2), profile.FedThrough);
    }

    [Fact]
    public void The_lost_hour_of_a_23_hour_day_updates_no_bin()
    {
        // Why: 2026-03-29 (Sunday) has no 02:00. Feeding that whole local day must fill Sunday bins 0, 1 and 3 to 23, and leave Sunday-02 empty,
        // so it is not filled by an hour that never existed.
        var start = new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.FromHours(1));
        var buckets = Enumerable.Range(0, 23).SelectMany(h => HourOf(start.AddHours(h), 1.0)).ToList();
        var next = new DateTimeOffset(2026, 3, 30, 0, 0, 0, TimeSpan.FromHours(2));

        var profile = LoadProfile.Empty.Feed(buckets, next, Parameters, Warsaw, HolidayCalendar.Empty);

        Assert.Equal(23, profile.FilledBins);
        Assert.Equal(0, profile.Bins[Bin(6, 2)].Samples);
        Assert.All(Enumerable.Range(0, 24).Where(h => h != 2), h => Assert.Equal(1, profile.Bins[Bin(6, h)].Samples));
    }

    [Fact]
    public void A_local_holiday_goes_to_the_Sunday_bin()
    {
        // Why: 2026-11-11 is a Wednesday and a public holiday (economics.holidays); the house behaves like a Sunday, so the sample belongs in Sunday-10, not Wednesday-10.
        var holidays = new HolidayCalendar([new DateOnly(2026, 11, 11)]);
        var start = Local(2026, 11, 11, 10);

        var profile = LoadProfile.Empty.Feed(HourOf(start, 1.5), Local(2026, 11, 11, 11), Parameters, Warsaw, holidays);

        Assert.Equal(1, profile.Bins[Bin(6, 10)].Samples);
        Assert.Equal(0, profile.Bins[Bin(2, 10)].Samples);
        Assert.Equal(Bin(6, 10), LoadProfile.BinIndex(start, Warsaw, holidays));
        Assert.Equal(Bin(2, 10), LoadProfile.BinIndex(start, Warsaw, HolidayCalendar.Empty));
    }

    [Fact]
    public void An_hour_that_has_not_ended_is_not_fed_and_feeding_twice_counts_once()
    {
        // Why: the hourly feed runs every few minutes. A half-finished hour would be a biased sample, and a second call over the same buckets (a restart, a retry) must not double the evidence.
        var start = Local(2026, 10, 26, 10);
        var buckets = HourOf(start, 1.2).ToList();

        var midHour = LoadProfile.Empty.Feed(buckets.Take(6), Local(2026, 10, 26, 10, 30), Parameters, Warsaw, HolidayCalendar.Empty);
        var done = midHour.Feed(buckets, Local(2026, 10, 26, 11), Parameters, Warsaw, HolidayCalendar.Empty);
        var again = done.Feed(buckets, Local(2026, 10, 26, 11, 20), Parameters, Warsaw, HolidayCalendar.Empty);

        Assert.Equal(0, midHour.FilledBins);
        Assert.Equal(start, midHour.FedThrough);
        Assert.Equal(1, done.Bins[Bin(0, 10)].Samples);
        Assert.Equal(1, again.Bins[Bin(0, 10)].Samples);
        Assert.Equal(Local(2026, 10, 26, 11), again.FedThrough);
    }

    [Fact]
    public void Seeding_from_26_hours_of_retained_buckets_fills_about_a_day_of_bins()
    {
        // Why: first start. The statistics keep 26 hours (1.02:00:00); the profile is seeded from them: 26 consecutive hours, 26 bins, DaysFilled 1 (26 / 24 rounded down),
        // and the rest empty. The first bucket starts mid-hour (10:20), so that hour is observed 40 of 60 minutes (0.667 < 0.8) and is skipped: 25 bins.
        var first = Local(2026, 10, 26, 10, 20);
        var buckets = Enumerable.Range(0, 26 * 12).Select(i => new LoadObservation(first.AddMinutes(5 * i), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), new KilowattHours(0.1)));
        var now = first.AddHours(26);

        var profile = LoadProfile.Empty.Feed(buckets, now, Parameters, Warsaw, HolidayCalendar.Empty);

        Assert.Equal(25, profile.FilledBins);
        Assert.All(profile.Bins.Where(b => b.Filled), b => Assert.Equal(1.2, b.MeanKw, 9));
        Assert.Equal(0, profile.Bins[Bin(0, 10)].Samples);
        Assert.Equal(Local(2026, 10, 27, 12), profile.FedThrough);
    }

    [Fact]
    public void A_profile_must_have_168_bins()
    {
        // Why: the shape is the contract of the file and of the forecast; anything else is not a profile.
        Assert.Throws<ArgumentException>(() => new LoadProfile([.. Enumerable.Repeat(LoadBin.Empty, 167)], null));
        Assert.Throws<ArgumentException>(() => new LoadProfile(default, null));
    }

    [Fact]
    public void Parameters_outside_their_range_are_refused()
    {
        // Why: no literal and no silent default for a control parameter; a bad configuration is refused where it is bound.
        Assert.Throws<ArgumentOutOfRangeException>(() => new ForecastParameters(TimeSpan.FromMinutes(45), 0.85, TimeSpan.FromDays(14), 0.8, 400));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ForecastParameters(TimeSpan.FromMinutes(60), 0.85, TimeSpan.Zero, 0.8, 400));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ForecastParameters(TimeSpan.FromMinutes(60), 0.85, TimeSpan.FromDays(14), 0, 400));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ForecastParameters(TimeSpan.FromMinutes(60), 0.85, TimeSpan.FromDays(14), 0.8, -1));
    }

    // ----- the forecast taken from a profile -----

    private static LoadProfile WithBins(params (int Index, double Kw)[] filled)
    {
        var bins = Enumerable.Repeat(LoadBin.Empty, LoadProfile.BinCount).ToArray();
        foreach (var (index, kw) in filled)
        {
            bins[index] = new LoadBin(kw, 1, 1, Utc(2026, 10, 1, 0));
        }

        return new LoadProfile([.. bins], Utc(2026, 10, 2, 0));
    }

    private static SlotGrid Hours(DateTimeOffset from, int count) => SlotGrid.Build(from, from.AddHours(count), Hour, Warsaw);

    [Fact]
    public void With_no_filled_bin_the_forecast_is_the_configured_fallback_and_says_so()
    {
        // Why: a fresh install has no history. fallback_w 400 W is 0.4 kWh per hour, the basis is "fallback" and no day is filled; nothing else may stand in.
        var load = LoadForecast.Slots(LoadProfile.Empty, Hours(Local(2026, 10, 26, 10), 3), Parameters, HolidayCalendar.Empty);

        Assert.Equal([0.4, 0.4, 0.4], load.Kwh.ToArray());
        Assert.Equal(LoadBasis.Fallback, load.Basis);
        Assert.Equal(0, load.FilledBins);
        Assert.Equal(0, load.DaysFilled);
    }

    [Fact]
    public void An_empty_bin_takes_the_mean_of_the_filled_ones_and_the_basis_is_partial()
    {
        // Why: after the first day only some bins have data. Monday 10:00 = 1.2 kW and 11:00 = 0.6 kW are known; 12:00 is not, so it gets their mean 0.9, not the 400 W fallback.
        var profile = WithBins((Bin(0, 10), 1.2), (Bin(0, 11), 0.6));

        var load = LoadForecast.Slots(profile, Hours(Local(2026, 10, 26, 10), 3), Parameters, HolidayCalendar.Empty);

        Assert.Equal([1.2, 0.6, 0.9], load.Kwh.Select(k => Math.Round(k, 9)).ToArray());
        Assert.Equal(LoadBasis.Partial, load.Basis);
        Assert.Equal(2, load.FilledBins);
    }

    [Fact]
    public void When_every_bin_a_slot_needs_is_filled_the_basis_is_profile_and_days_filled_counts_whole_days()
    {
        // Why: a full week of bins is the target state. 168 filled bins is 7 days; the forecast uses them as they are.
        var profile = WithBins([.. Enumerable.Range(0, LoadProfile.BinCount).Select(i => (i, 0.5 + (i / 100.0)))]);

        var load = LoadForecast.Slots(profile, Hours(Local(2026, 10, 26, 10), 2), Parameters, HolidayCalendar.Empty);

        Assert.Equal([Math.Round(0.5 + (Bin(0, 10) / 100.0), 9), Math.Round(0.5 + (Bin(0, 11) / 100.0), 9)], load.Kwh.Select(k => Math.Round(k, 9)).ToArray());
        Assert.Equal(LoadBasis.Profile, load.Basis);
        Assert.Equal(7, load.DaysFilled);
    }

    [Fact]
    public void A_holiday_slot_uses_the_Sunday_bin_and_the_same_slot_on_a_workday_does_not()
    {
        // Why: the forecast must read what the feed wrote. Sunday-10 = 2.0 kW, Wednesday-10 = 1.0 kW: Wednesday 2026-11-11 (holiday) 10:00 is 2.0, Wednesday 2026-11-18 10:00 is 1.0.
        var profile = WithBins((Bin(6, 10), 2.0), (Bin(2, 10), 1.0));
        var holidays = new HolidayCalendar([new DateOnly(2026, 11, 11)]);

        var holiday = LoadForecast.Slots(profile, Hours(Local(2026, 11, 11, 10), 1), Parameters, holidays);
        var workday = LoadForecast.Slots(profile, Hours(Local(2026, 11, 18, 10), 1), Parameters, holidays);

        Assert.Equal(2.0, holiday.Kwh[0], 9);
        Assert.Equal(1.0, workday.Kwh[0], 9);
    }

    [Fact]
    public void On_a_25_hour_day_both_02_00_slots_read_the_same_bin_and_there_are_25_of_them()
    {
        // Why: the forecast of 2026-10-25 must have 25 slots whose two 02:00 entries are the one Sunday-02 bin (0.8 kW), the other hours their own bin (0.5 kW),
        // so the day totals 23 x 0.5 + 2 x 0.8 = 13.1 kWh. The lost-hour day, 2026-03-29, has 23 slots.
        var profile = WithBins([.. Enumerable.Range(0, 24).Select(h => (Bin(6, h), h == 2 ? 0.8 : 0.5))]);
        var start = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.FromHours(2));
        var end = new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.FromHours(1));
        var grid = SlotGrid.Build(start, end, Hour, Warsaw);

        var load = LoadForecast.Slots(profile, grid, Parameters, HolidayCalendar.Empty);

        Assert.Equal(25, load.Kwh.Length);
        Assert.Equal(0.8, load.Kwh[2], 9);
        Assert.Equal(0.8, load.Kwh[3], 9);
        Assert.Equal(13.1, load.Kwh.Sum(), 9);
        Assert.Equal(LoadBasis.Profile, load.Basis);
    }

    [Fact]
    public void A_shortened_slot_gets_energy_for_its_own_length()
    {
        // Why: the first slot [10:40, 11:00) is a third of an hour: 1.2 kW x 1/3 h = 0.4 kWh, not the whole hour's 1.2 kWh.
        var profile = WithBins((Bin(0, 10), 1.2), (Bin(0, 11), 0.6));
        var shortened = SlotGrid.Build(Local(2026, 10, 26, 10, 40), Local(2026, 10, 26, 11), Hour, Warsaw);

        var load = LoadForecast.Slots(profile, shortened, Parameters, HolidayCalendar.Empty);

        Assert.Equal(0.4, load.Kwh[0], 9);
    }
}
