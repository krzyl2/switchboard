using Switchboard.Application.Configuration;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Hashing;
using Switchboard.Domain.Time;
using Switchboard.Host.Web;

namespace Switchboard.Tests.Domain;

/// <summary>
/// The helpers the domain's former copies became (UPGRADE.md, Duplikaty). Why it matters: a fingerprint is stored and compared across runs, so the hash must be
/// the published FNV-1a and nothing else; a local time must resolve the same way wherever a deadline, a series key or a day boundary is read.
/// </summary>
public sealed class SharedHelperTests
{
    [Theory]
    [InlineData("", 0xcbf29ce484222325UL)]
    [InlineData("a", 0xaf63dc4c8601ec8cUL)]
    [InlineData("foobar", 0x85944171f73967e8UL)]
    public void Fnv1a_is_the_published_64_bit_function(string text, ulong expected)
    {
        Assert.Equal(expected, Fnv1a.Hash(text));
        Assert.Equal(unchecked((long)expected), Fnv1a.Signed(text));
    }

    [Fact]
    public void Fnv1a_hashes_the_utf8_bytes_so_a_separator_changes_the_hash()
    {
        Assert.Equal(Fnv1a.Hash("zażółć\n"), Fnv1a.Hash("zażółć" + "\n"));
        Assert.NotEqual(Fnv1a.Hash("ab\nc\n"), Fnv1a.Hash("a\nbc\n"));
    }

    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    [Fact]
    public void A_local_time_keeps_the_offset_of_the_zone_and_a_gap_or_an_ambiguity_resolves_to_one_instant()
    {
        var plain = LocalTime.At(new DateTime(2026, 10, 27, 6, 30, 0), Warsaw);
        var gap = LocalTime.At(new DateTime(2026, 3, 29, 2, 30, 0), Warsaw);
        var twice = LocalTime.At(new DateTime(2026, 10, 25, 2, 30, 0), Warsaw);

        Assert.Equal((new DateTimeOffset(2026, 10, 27, 6, 30, 0, TimeSpan.FromHours(1)), TimeSpan.FromHours(1)), (plain, plain.Offset));
        Assert.Equal((new DateTimeOffset(2026, 3, 29, 3, 30, 0, TimeSpan.FromHours(2)), TimeSpan.FromHours(2)), (gap, gap.Offset));
        Assert.Equal((new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.FromHours(2)), TimeSpan.FromHours(2)), (twice, twice.Offset));
    }

    [Fact]
    public void A_time_in_a_gap_longer_than_an_hour_is_refused_by_try_and_read_with_the_standard_offset_by_at()
    {
        // Why: no zone of the tz database has such a gap, so the zone is made here. IsInvalidTime stays true one hour after the start of a two-hour gap;
        // the series reader refuses it, the deadline readers never throw.
        var rule = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 29);
        var end = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 10, 25);
        var adjustment = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(2), rule, end);
        var zone = TimeZoneInfo.CreateCustomTimeZone("gap2", TimeSpan.Zero, "gap2", "gap2", "gap2", [adjustment]);
        var inside = new DateTime(2026, 3, 29, 2, 30, 0);

        Assert.False(LocalTime.TryAt(inside, zone, out var refused));
        Assert.Equal(default, refused);
        Assert.Equal(new DateTime(2026, 3, 29, 3, 30, 0), LocalTime.At(inside, zone).DateTime);
    }

    [Fact]
    public void A_local_day_that_begins_in_a_repeated_midnight_begins_at_the_first_one()
    {
        // Why: the day of a zone that falls back at midnight holds the repeated hour (25 hours); the second midnight would leave it to the day before.
        TimeZoneInfo havana;
        try
        {
            havana = TimeZoneInfo.FindSystemTimeZoneById("America/Havana");
        }
        catch (TimeZoneNotFoundException)
        {
            return;
        }

        var day = EconomicsEndpoint.LocalDay(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.FromHours(-5)), havana);

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero), day.From);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 5, 0, 0, TimeSpan.Zero), day.To);
    }

    [Theory]
    [InlineData(ValueFollows.Static, 0.7, 0.2, 0.35)]
    [InlineData(ValueFollows.ImportPrice, 0.7, 0.2, 1.4)]
    [InlineData(ValueFollows.ExportPrice, 0.7, 0.2, 0.4)]
    [InlineData(ValueFollows.ImportPrice, null, 0.2, 0.35)]
    [InlineData(ValueFollows.ExportPrice, 0.7, null, 0.35)]
    public void A_value_follows_the_price_it_names_and_falls_back_to_its_static_value(ValueFollows follows, double? import, double? export, double expected)
    {
        // Why: the plan reads this per slot and dispatch per instant; one rule keeps a plan's idea of "worth it" the same as dispatch's.
        var spec = new ValueSpec(0.35, follows, 2);

        Assert.Equal(expected, spec.PerKwhAt(import, export), 12);
    }

    [Fact]
    public void A_class_is_worth_using_at_exactly_the_margin_whatever_the_float_noise()
    {
        // Why: 0.7 - 0.2 is 0.4999999999999999; a margin of 0.5 must still pass, or a slot is refused for an error of one bit.
        Assert.True(ValueTest.Worth(0.7, 0.2, 0.5));
        Assert.False(ValueTest.Worth(0.7, 0.2, 0.500001));
    }

    [Fact]
    public void A_spelling_is_exact_and_one_table_serves_every_section()
    {
        Assert.Equal(ConfigSpelling.Phases["L2"], ConfigSpelling.Of(("L2", ConfigSpelling.Phases["L2"]))["L2"]);
        Assert.False(ConfigSpelling.Phases.ContainsKey("l2"));
        Assert.False(ConfigSpelling.StateReports.ContainsKey("On_change"));
        Assert.Equal(["B", "C", "unknown"], ConfigSpelling.Characteristics.Keys.Order(StringComparer.Ordinal));
    }
}
