using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Switchboard.Application.Signals;
using Switchboard.Infrastructure.HomeAssistant;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// The compressed state stream of subscribe_entities (HA 2026.10): the report time of every sample
/// must be exactly what HA means by it, because freshness (SIG-06) is computed from it.
/// </summary>
public sealed class SubscribeEntitiesParserTests
{
    private const decimal Lc = 1792836054.020123m;
    private static readonly DateTimeOffset LcTime = DateTimeOffset.UnixEpoch.AddTicks(17928360540201230);

    private readonly SubscribeEntitiesParser _parser = new([EntityId.Parse("sensor.meter_current_a"), EntityId.Parse("sensor.meter_power_a")]);

    [Fact]
    public void Initial_additions_carry_state_unit_reported_time()
    {
        var batch = Parse(Json("""{"a":{"sensor.meter_current_a":{"s":"5.437","a":{"unit_of_measurement":"A","friendly_name":"L1"},"c":"01J","lc":@T0,"lu":@T30}}}"""));

        Assert.Equal(BatchOrigin.Initial, batch.Origin);
        var update = Assert.Single(batch.Updates);
        Assert.Equal(("5.437", "A"), (update.State, update.Unit));
        Assert.Equal(LcTime.AddSeconds(30), update.LastUpdated);
        Assert.Equal(LcTime.AddSeconds(30), update.Reported);
    }

    [Fact]
    public void Initial_state_without_lu_uses_lc_as_HA_defines()
    {
        // HA leaves lu out when it equals lc.
        var update = Parse(Json("""{"a":{"sensor.meter_current_a":{"s":"5.4","a":{"unit_of_measurement":"A"},"c":"x","lc":@T0}}}""")).Updates.Single();

        Assert.Equal(LcTime, update.LastUpdated);
        Assert.Equal(LcTime, update.Reported);
    }

    [Fact]
    public void Tracked_entities_absent_from_the_initial_state_are_missing()
    {
        var batch = Parse(Json("""{"a":{"sensor.meter_current_a":{"s":"5.4","a":{},"c":"x","lc":@T0}}}"""));

        Assert.Equal([EntityId.Parse("sensor.meter_power_a")], batch.Missing);
    }

    [Fact]
    public void Change_with_lc_updates_state_and_time()
    {
        Parse(Json("""{"a":{"sensor.meter_current_a":{"s":"5.4","a":{"unit_of_measurement":"A"},"c":"x","lc":@T0}}}"""));

        var batch = Parse(Json("""{"c":{"sensor.meter_current_a":{"+":{"s":"6.1","lc":@T30,"c":"y"}}}}"""));

        Assert.Equal(BatchOrigin.Change, batch.Origin);
        var update = batch.Updates.Single();
        Assert.Equal(("6.1", "A", LcTime.AddSeconds(30)), (update.State, update.Unit, update.Reported));
    }

    [Fact]
    public void Initial_with_lu_then_change_with_only_lc_uses_new_lc()
    {
        // A new last_changed implies last_updated moved with it; keeping the older cached lu would
        // date the new value before its own change.
        Parse(Json("""{"a":{"sensor.meter_current_a":{"s":"5.4","a":{},"c":"x","lc":@T0,"lu":@T10}}}"""));

        var update = Parse(Json("""{"c":{"sensor.meter_current_a":{"+":{"s":"6.1","lc":@T30}}}}""")).Updates.Single();

        Assert.Equal(LcTime.AddSeconds(30), update.LastUpdated);
        Assert.Equal(LcTime.AddSeconds(30), update.Reported);
    }

    [Fact]
    public void Change_with_only_lu_keeps_state_advances_time()
    {
        Parse(Json("""{"a":{"sensor.meter_current_a":{"s":"5.4","a":{"unit_of_measurement":"A"},"c":"x","lc":@T0}}}"""));

        var update = Parse(Json("""{"c":{"sensor.meter_current_a":{"+":{"lu":@T45,"a":{"friendly_name":"Phase 1"}}}}}""")).Updates.Single();

        Assert.Equal(("5.4", "A", LcTime.AddSeconds(45)), (update.State, update.Unit, update.Reported));
    }

    [Fact]
    public void Attribute_removal_clears_unit()
    {
        Parse(Json("""{"a":{"sensor.meter_current_a":{"s":"5.4","a":{"unit_of_measurement":"A"},"c":"x","lc":@T0}}}"""));

        var update = Parse(Json("""{"c":{"sensor.meter_current_a":{"+":{"lu":@T1},"-":{"a":["unit_of_measurement"]}}}}""")).Updates.Single();

        Assert.Null(update.Unit);
    }

    [Fact]
    public void Removal_marks_entity_removed()
    {
        Parse(Json("""{"a":{"sensor.meter_current_a":{"s":"5.4","a":{},"c":"x","lc":@T0}}}"""));

        var batch = Parse("""{"r":["sensor.meter_current_a"]}""");

        Assert.Equal([EntityId.Parse("sensor.meter_current_a")], batch.Removed);
    }

    [Fact]
    public void Entity_appearing_later_is_an_addition_not_an_initial_state()
    {
        Parse("""{"a":{}}""");

        var batch = Parse(Json("""{"a":{"sensor.meter_power_a":{"s":"-1089.2","a":{"unit_of_measurement":"W"},"c":"x","lc":@T0}}}"""));

        Assert.Equal(BatchOrigin.Added, batch.Origin);
        Assert.Empty(batch.Missing);
    }

    [Fact]
    public void Lr_preferred_when_present()
    {
        var update = Parse(Json("""{"a":{"sensor.meter_current_a":{"s":"5.4","a":{},"c":"x","lc":@T0,"lr":@T120}}}""")).Updates.Single();

        Assert.Equal(LcTime, update.LastUpdated);
        Assert.Equal(LcTime.AddSeconds(120), update.Reported);
    }

    [Fact]
    public void Untracked_entities_are_ignored()
    {
        var batch = Parse(Json("""{"a":{"switch.anything_else":{"s":"on","a":{},"c":"x","lc":@T0}}}"""));

        Assert.Empty(batch.Updates);
    }

    [Theory]
    [InlineData("""{"a":{"sensor.meter_current_a":{"s":5.4,"lc":1}}}""")]
    [InlineData("""{"a":{"sensor.meter_current_a":{"s":"5.4"}}}""")]
    [InlineData("""{"a":{"sensor.meter_current_a":{"s":"5.4","lc":"yesterday"}}}""")]
    [InlineData("""{"c":[]}""")]
    [InlineData("""[]""")]
    public void Malformed_event_dropped_and_the_cache_untouched(string body)
    {
        Parse(Json("""{"a":{"sensor.meter_current_a":{"s":"5.4","a":{},"c":"x","lc":@T0}}}"""));
        using var document = JsonDocument.Parse(body);

        Assert.Null(_parser.Parse(document.RootElement, out var problem));
        Assert.NotNull(problem);

        var update = Parse(Json("""{"c":{"sensor.meter_current_a":{"+":{"lu":@T5}}}}""")).Updates.Single();
        Assert.Equal("5.4", update.State);
    }

    [Fact]
    public void Options_follow_initial_state_and_diffs()
    {
        // 0.7.1: a select's options attribute is kept like the unit: from the initial state, replaced
        // by a diff that carries it, kept by a diff that does not, cleared when HA removes it.
        var parser = new SubscribeEntitiesParser([EntityId.Parse("select.charger_force")]);
        EntityUpdate Next(string body)
        {
            using var document = JsonDocument.Parse(Json(body));
            return parser.Parse(document.RootElement, out var problem)!.Updates.Single();
        }

        Assert.Equal(["0", "1", "2"], Next("""{"a":{"select.charger_force":{"s":"0","a":{"options":["0","1","2"],"friendly_name":"Force"},"c":"x","lc":@T0}}}""").Options!.Value);
        Assert.Equal(["0", "1", "2"], Next("""{"c":{"select.charger_force":{"+":{"s":"1","lc":@T10}}}}""").Options!.Value);
        Assert.Equal(["0", "1"], Next("""{"c":{"select.charger_force":{"+":{"lu":@T20,"a":{"options":["0","1"]}}}}}""").Options!.Value);
        Assert.Null(Next("""{"c":{"select.charger_force":{"+":{"lu":@T30},"-":{"a":["options"]}}}}""").Options);
    }

    [Theory]
    [InlineData("""{"options":"0,1,2"}""")]
    [InlineData("""{"options":["0",1]}""")]
    [InlineData("""{"options":[]}""")]
    [InlineData("""{"options":["0",""]}""")]
    public void Oversized_or_misshapen_options_read_as_unknown(string attributes)
    {
        // Never a refusal of the event: the state itself is still read, only its options are not known.
        var parser = new SubscribeEntitiesParser([EntityId.Parse("select.charger_force")]);
        using var document = JsonDocument.Parse(Json("""{"a":{"select.charger_force":{"s":"0","a":ATTRIBUTES,"c":"x","lc":@T0}}}""".Replace("ATTRIBUTES", attributes, StringComparison.Ordinal)));

        var update = parser.Parse(document.RootElement, out _)!.Updates.Single();

        Assert.Equal("0", update.State);
        Assert.Null(update.Options);
    }

    [Fact]
    public void Options_beyond_the_schema_bound_read_as_unknown()
    {
        var parser = new SubscribeEntitiesParser([EntityId.Parse("select.charger_force")]);
        var many = string.Join(",", Enumerable.Range(0, EntityUpdate.MaxOptions + 1).Select(i => $"\"{i}\""));
        using var document = JsonDocument.Parse(Json("""{"a":{"select.charger_force":{"s":"0","a":{"options":[MANY]},"c":"x","lc":@T0}}}""".Replace("MANY", many, StringComparison.Ordinal)));

        Assert.Null(parser.Parse(document.RootElement, out _)!.Updates.Single().Options);
    }

    // HA writes epoch seconds with microseconds; formatted invariantly so the JSON never depends on the test machine's culture.
    private static string T(int offsetSeconds) => (Lc + offsetSeconds).ToString(CultureInfo.InvariantCulture);

    /// <summary>Fills @T&lt;n&gt; placeholders with the timestamp Lc + n seconds.</summary>
    private static string Json(string template) => Regex.Replace(template, "@T([0-9]+)", m => T(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)));

    private EntityUpdateBatch Parse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var batch = _parser.Parse(document.RootElement, out var problem);
        Assert.True(batch is not null, problem);
        return batch!;
    }
}
