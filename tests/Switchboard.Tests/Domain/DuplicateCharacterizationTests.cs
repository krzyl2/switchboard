using System.Globalization;
using Switchboard.Application.Goals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Ev;
using Switchboard.Domain.Goals;
using Switchboard.Domain.Signals;
using Switchboard.Host.Web;
using static Switchboard.Tests.Domain.Ev.EvTestParameters;

namespace Switchboard.Tests.Domain;

/// <summary>
/// The domain's former copies, pinned before they became one (UPGRADE.md, Duplikaty): the FNV-1a fingerprints of goals and the EV composer, and the
/// conversion of a local wall-clock time to an instant in the four places that did it (goal deadlines, series keys, the EV composer's midnight,
/// the economics day). Why it matters: a fingerprint that moves makes every stored plan entry foreign, and a deadline that moves by an hour
/// charges a car at the wrong time on the 23- and 25-hour days.
/// </summary>
public sealed class DuplicateCharacterizationTests
{
    private static readonly TimeZoneInfo Havana = Find("America/Havana");

    private static TimeZoneInfo Find(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static DateTimeOffset Utc(int y, int m, int d, int h, int min = 0) => new(y, m, d, h, min, 0, TimeSpan.Zero);

    [Fact]
    public void A_manual_goal_revision_is_the_fnv_of_its_canonical_text()
    {
        var goal = new ManualGoal(GoalKind.Energy, 12.5, Utc(2026, 10, 30, 6), true, Utc(2026, 10, 29, 20), 3, Utc(2026, 10, 29, 21));
        var device = DeviceName.Parse("ev_charger");

        Assert.Equal(ManualRevision, goal.Revision(device));
        Assert.Equal(goal.Revision(device), (goal with { DeliveredKwh = 9 }).Revision(device));
    }

    [Fact]
    public void A_goal_spec_revision_is_the_fnv_of_its_canonical_text()
    {
        var energy = new GoalSpec(
            DeviceName.Parse("ev_charger"), GoalKind.Energy, true, 0.1, null, null, GoalNumber.Of(10),
            new GoalDeadlineSpec(new TimeOnly(6, 30), null, TimeSpan.FromMinutes(15)), null, SourceSet.Any, false, GoalOnMet.Stop);
        var soc = new GoalSpec(
            DeviceName.Parse("ev_charger"), GoalKind.Soc, false, 0, 60, GoalNumber.From(SignalName.Parse("ev_soc")), GoalNumber.Of(80),
            new GoalDeadlineSpec(null, SignalName.Parse("ev_departure"), TimeSpan.Zero), SignalName.Parse("ev_want"), SourceSet.PvOnly, true, GoalOnMet.Opportunistic);

        Assert.Equal(EnergySpecRevision, energy.Revision);
        Assert.Equal(SocSpecRevision, soc.Revision);
    }

    [Fact]
    public void An_ev_goal_revision_is_the_fnv_of_what_the_goal_is()
    {
        var now = new DateTimeOffset(2026, 10, 30, 10, 0, 0, TimeSpan.FromHours(1));
        var composition = EvComposer.Compose(new EvComposerInput
        {
            Now = now,
            Zone = Warsaw,
            Device = DeviceName.Parse("ev_charger"),
            CapacityKwh = CapacityKwh,
            Parameters = Defaults,
            Mode = EvMode.Auto,
            Plugged = true,
            SocPercent = 40,
            KwhPerKm = 0.2,
            NextTrip = null,
            Frozen = null,
            LastFullAt = null,
            FullChargeRequested = false,
            IsHoliday = _ => false,
            CheapZone = "offpeak",
            DeviceSources = SourceSet.Any,
        });

        Assert.Equal(EvRevision, Assert.Single(composition.Goals).Request.Revision);
    }

    public static TheoryData<string, string, string, string> LocalTimes => new()
    {
        // zone, local text, expected UTC, offset a series key carries
        { "Europe/Warsaw", "2026-10-27 06:30:00", "2026-10-27T05:30:00Z", "01:00:00" },
        { "Europe/Warsaw", "2026-03-29 02:30:00", "2026-03-29T01:30:00Z", "02:00:00" },
        { "Europe/Warsaw", "2026-03-29 02:00:00", "2026-03-29T01:00:00Z", "02:00:00" },
        { "Europe/Warsaw", "2026-03-29 03:00:00", "2026-03-29T01:00:00Z", "02:00:00" },
        { "Europe/Warsaw", "2026-10-25 02:30:00", "2026-10-25T00:30:00Z", "02:00:00" },
        { "Europe/Warsaw", "2026-10-25 02:00:00", "2026-10-25T00:00:00Z", "02:00:00" },
        { "Europe/Warsaw", "2026-10-25 03:00:00", "2026-10-25T02:00:00Z", "01:00:00" },
        { "America/Havana", "2026-11-01 00:30:00", "2026-11-01T04:30:00Z", "-04:00:00" },
        { "America/Havana", "2026-03-08 00:30:00", "2026-03-08T05:30:00Z", "-04:00:00" },
        { "America/New_York", "2026-11-01 01:30:00", "2026-11-01T05:30:00Z", "-04:00:00" },
        { "America/New_York", "2026-03-08 02:30:00", "2026-03-08T07:30:00Z", "-04:00:00" },
    };

    [Theory]
    [MemberData(nameof(LocalTimes))]
    public void A_goal_deadline_and_a_series_key_read_a_local_time_as_the_first_valid_instant(string zoneId, string text, string utc, string offset)
    {
        // Why: a time that happens twice is the first of the two (the sooner deadline is the safe side), one that does not exist moves one hour on.
        var zone = Find(zoneId);
        if (zone == TimeZoneInfo.Utc)
        {
            return;
        }

        var expected = DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture);
        var deadline = GoalDeadlines.Parse(text, Utc(2026, 1, 1, 0), zone)!.Value;

        Assert.Equal(expected, deadline);
        Assert.Equal(TimeSpan.Zero, deadline.Offset);
        Assert.True(IsoInstant.TryParse(text, zone, out var instant));
        Assert.Equal(expected, instant);
        Assert.Equal(TimeSpan.Parse(offset, CultureInfo.InvariantCulture), instant.Offset);
    }

    [Fact]
    public void The_next_local_midnight_keeps_the_zone_offset()
    {
        // Why: the composer's midnight is compared and printed as the local wall time; its offset is the zone's, not UTC.
        var warsaw = EvComposer.NextLocalMidnight(Utc(2026, 10, 24, 12), Warsaw);

        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.FromHours(2)), warsaw);
        Assert.Equal(TimeSpan.FromHours(2), warsaw.Offset);
        if (Havana != TimeZoneInfo.Utc)
        {
            var gap = EvComposer.NextLocalMidnight(Utc(2026, 3, 7, 12), Havana);
            var twice = EvComposer.NextLocalMidnight(Utc(2026, 10, 31, 12), Havana);
            Assert.Equal(new DateTimeOffset(2026, 3, 8, 1, 0, 0, TimeSpan.FromHours(-4)), gap);
            Assert.Equal(TimeSpan.FromHours(-4), gap.Offset);
            Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(-4)), twice);
            Assert.Equal(TimeSpan.FromHours(-4), twice.Offset);
        }
    }

    [Fact]
    public void The_local_day_is_23_24_or_25_hours_in_utc_instants()
    {
        var spring = EconomicsEndpoint.LocalDay(Utc(2026, 3, 29, 12), Warsaw);
        var autumn = EconomicsEndpoint.LocalDay(Utc(2026, 10, 25, 12), Warsaw);
        var plain = EconomicsEndpoint.LocalDay(Utc(2026, 10, 27, 12), Warsaw);

        Assert.Equal((Utc(2026, 3, 28, 23), Utc(2026, 3, 29, 22)), spring);
        Assert.Equal((Utc(2026, 10, 24, 22), Utc(2026, 10, 25, 23)), autumn);
        Assert.Equal((Utc(2026, 10, 26, 23), Utc(2026, 10, 27, 23)), plain);
        Assert.Equal(TimeSpan.Zero, plain.From.Offset);
    }

    [Fact]
    public void A_local_day_that_starts_in_a_missing_midnight_starts_one_hour_on()
    {
        if (Havana == TimeZoneInfo.Utc)
        {
            return;
        }

        var day = EconomicsEndpoint.LocalDay(Utc(2026, 3, 8, 12), Havana);

        Assert.Equal(Utc(2026, 3, 8, 5), day.From);
    }

    private const long ManualRevision = -873753939965429282;
    private const long EnergySpecRevision = 8262823617709890398;
    private const long SocSpecRevision = -6705574796556029327;
    private const long EvRevision = -3201153173313218398;
}
