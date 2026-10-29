using System.Collections.Immutable;
using Switchboard.Application.Configuration;
using Switchboard.Application.Planning;
using Switchboard.Application.Signals;
using Switchboard.Domain.Forecast;
using Switchboard.Domain.Planning;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Signals;

/// <summary>
/// PLAN-06 closed for a Home Assistant that has only the core forecast_solar sensors and <c>sun.sun</c> (15-PLANNER, "As built"): a timestamp signal
/// reads its instant from one named attribute (<c>next_rising</c>), because the state of <c>sun.sun</c> is above_horizon. Why it matters: reading the state
/// would reject every sample and the totals would never be spread; reading any other attribute than the declared one would be an unbounded whitelist.
/// Freshness stays the entity's <c>last_reported</c>. The entities are the maintainer's: <c>sensor.energy_production_today</c> 9.655 kWh,
/// <c>sensor.energy_production_tomorrow</c> 12.903 kWh.
/// </summary>
public sealed class SunAttributeSignalTests
{
    private const string Sun = "sun.sun";
    private const string TodayEntity = "sensor.energy_production_today";
    private const string TomorrowEntity = "sensor.energy_production_tomorrow";

    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    private const string Signals = """
          pv_today:
            entity: sensor.energy_production_today
            kind: energy_forecast
            unit: kWh
            max_age: 3h
            role: pv_forecast_today
          pv_tomorrow:
            entity: sensor.energy_production_tomorrow
            kind: energy_forecast
            unit: kWh
            max_age: 3h
            role: pv_forecast_tomorrow
          sun_rising:
            entity: sun.sun
            kind: timestamp
            attribute: next_rising
            max_age: 1h
            role: sun_rising
          sun_setting:
            entity: sun.sun
            kind: timestamp
            attribute: next_setting
            max_age: 1h
            role: sun_setting

        """;

    private static StoreHarness Store() => new(TestConfigurations.ThreePhase(Signals));

    private static SignalDefinition Definition(string? attribute) =>
        new(SignalName.Parse("sun_rising"), EntityId.Parse(Sun), SignalKind.Timestamp, null, null, null, false, TimeSpan.FromHours(1), null, SignalRole.SunRising, null, false) { Attribute = attribute };

    private static void PushSun(StoreHarness store, string rising, string setting, string state = "above_horizon", DateTimeOffset? at = null)
    {
        // One entity feeds both signals: the transport hands over the text of each declared attribute by its name.
        var attributes = ImmutableDictionary<string, string>.Empty.Add("next_rising", rising).Add("next_setting", setting);
        var update = new EntityUpdate(EntityId.Parse(Sun), state, null, at ?? store.Now, at ?? store.Now) { Attributes = attributes };
        store.Store.Apply(EntityUpdateBatch.Of(BatchOrigin.Change, update), store.Now);
    }

    [Fact]
    public void A_sun_time_is_read_from_the_attribute_and_the_state_of_the_entity_says_nothing_about_it()
    {
        var result = SignalNormalizer.Normalize(Definition("next_rising"), "above_horizon", null, attributeText: "2026-10-30T05:32:11.123456+00:00");

        Assert.Equal(new TimestampValue(new DateTimeOffset(2026, 10, 30, 5, 32, 11, 123, TimeSpan.Zero).AddTicks(4560)), result.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("below_horizon")]
    [InlineData("2026-10-30T05:32:11")]
    public void An_attribute_that_is_absent_or_not_an_instant_with_its_offset_is_refused_and_the_state_is_never_a_fallback(string? attributeText)
    {
        // Why: with a declared attribute the state is above_horizon or below_horizon; falling back to it would not be a time anyway, and a time
        // without an offset is never read as local.
        var result = SignalNormalizer.Normalize(Definition("next_rising"), "2026-10-30T05:32:11+00:00", null, attributeText: attributeText);

        Assert.Equal((NormalizationOutcome.Rejected, SignalDetail.NotTimestamp), (result.Outcome, result.Detail));
    }

    [Fact]
    public void A_timestamp_without_an_attribute_is_still_the_state()
    {
        var result = SignalNormalizer.Normalize(Definition(null), "2026-10-30T05:32:11+00:00", null);

        Assert.Equal(new TimestampValue(new DateTimeOffset(2026, 10, 30, 5, 32, 11, TimeSpan.Zero)), result.Value);
    }

    [Fact]
    public void An_unavailable_sun_is_unavailable_whatever_its_attribute_says()
    {
        var result = SignalNormalizer.Normalize(Definition("next_rising"), "unavailable", null, attributeText: "2026-10-30T05:32:11+00:00");

        Assert.Equal(NormalizationOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public void The_maintainers_entities_close_the_loop_to_planned_pv_on_the_totals_basis()
    {
        // Why: the end-to-end claim of PLAN-06 for a core-sensors-only HA: pushed states become fresh forecast samples, and the planner's PV is the two
        // day totals spread over the daylight the sun's next events place. After sunset both next events are tomorrow's.
        var store = Store();
        store.Push(TodayEntity, "9.655", unit: "kWh");
        store.Push(TomorrowEntity, "12.903", unit: "kWh");
        PushSun(store, "2026-10-24T05:32:00+00:00", "2026-10-24T15:41:00+00:00");

        var forecast = store.Snapshot().Forecast!;
        var pv = PlanSourceMapper.Pv(forecast, Warsaw, store.Now);

        Assert.Equal((9.655, 12.903), (forecast.Usable(SignalRole.PvForecastToday)!.EnergyKwh!.Value, forecast.Usable(SignalRole.PvForecastTomorrow)!.EnergyKwh!.Value));
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 5, 32, 0, TimeSpan.Zero), forecast.Usable(SignalRole.SunRising)!.At);
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 15, 41, 0, TimeSpan.Zero), forecast.Usable(SignalRole.SunSetting)!.At);
        Assert.Equal((PvBasis.Totals, new DateTimeOffset(2026, 10, 24, 15, 41, 0, TimeSpan.Zero)), (pv.Basis, pv.CoversUntil!.Value));
        var tomorrowLocal = new DateTimeOffset(2026, 10, 23, 22, 0, 0, TimeSpan.Zero); // 2026-10-24 00:00 in Warsaw (UTC+2 until the 25th)
        var grid = SlotGrid.Build(tomorrowLocal, tomorrowLocal.AddHours(24), TimeSpan.FromMinutes(30), Warsaw);
        Assert.Equal(12.903, pv.Slots(grid, new ForecastParameters(TimeSpan.FromMinutes(30), 1.0, TimeSpan.FromDays(14), 0.8, 400)).Kwh.Sum(), 6);
    }

    [Fact]
    public void A_sun_that_stops_reporting_goes_stale_at_max_age_and_the_totals_are_no_longer_spread()
    {
        // Why: PLAN-06, never the last known curve: the sun entity's last_reported is the freshness, and without it there is no daylight, so zero PV.
        var store = Store();
        store.Push(TodayEntity, "9.655", unit: "kWh");
        store.Push(TomorrowEntity, "12.903", unit: "kWh");
        PushSun(store, "2026-10-24T05:32:00+00:00", "2026-10-24T15:41:00+00:00");
        Assert.Equal(PvBasis.Totals, PlanSourceMapper.Pv(store.Snapshot().Forecast, Warsaw, store.Now).Basis);

        store.Advance(TimeSpan.FromHours(1));
        store.Push(TodayEntity, "9.655", unit: "kWh");
        store.Push(TomorrowEntity, "12.903", unit: "kWh");

        var forecast = store.Snapshot().Forecast!;
        Assert.Equal(SignalState.Stale, forecast.ForRole(SignalRole.SunRising)!.State);
        Assert.Equal(PvBasis.None, PlanSourceMapper.Pv(forecast, Warsaw, store.Now).Basis);
    }

    [Fact]
    public void A_probe_answer_carries_no_attributes_so_it_confirms_a_sun_time_but_never_replaces_it()
    {
        // Why: as for a series, a probe answer for another state must not blank the instant in use; the push of the new state brings the new attribute.
        var store = Store();
        PushSun(store, "2026-10-24T05:32:00+00:00", "2026-10-24T15:41:00+00:00");
        var first = store.Now;
        store.Advance(TimeSpan.FromMinutes(30));

        store.ProbeAnswer(Sun, "below_horizon", store.Now, store.Now, unit: null);

        var rising = store.Snapshot().Forecast!.Usable(SignalRole.SunRising)!;
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 5, 32, 0, TimeSpan.Zero), rising.At);
        Assert.Equal(first, store.Reading("sun_rising").LastReported);
    }

    [Fact]
    public void The_tracked_entity_carries_the_attribute_so_the_transport_keeps_it_and_only_it()
    {
        var store = Store();

        Assert.True(store.Store.TryTakeTrackedChange(out var all));

        var tracked = all.Where(t => t.Entity == EntityId.Parse(Sun)).ToList();
        Assert.Equal(["next_rising", "next_setting"], tracked.Select(t => t.Attribute).Order());
        Assert.All(all.Where(t => t.Entity != EntityId.Parse(Sun)), t => Assert.Null(t.Attribute));
    }
}
