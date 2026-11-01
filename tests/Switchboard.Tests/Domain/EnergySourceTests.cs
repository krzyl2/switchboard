using Switchboard.Domain.Economics;

namespace Switchboard.Tests.Domain;

/// <summary>
/// An energy source is surplus or grid energy in a zone; its text exists at the edges only. The plan JSON and the decision JSON are read by the panel and
/// by logs written by older versions, so the spellings below are a contract: a changed one is a broken log, not a refactoring.
/// </summary>
public sealed class EnergySourceTests
{
    [Theory]
    [InlineData("pv")]
    [InlineData("zone")]
    [InlineData("zone:offpeak")]
    public void A_plan_class_reads_back_as_it_was_written(string text)
    {
        Assert.True(EnergySourceText.TryParsePlan(text, out var source));

        Assert.Equal(text, EnergySourceText.Plan(source!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("PV")]
    [InlineData("grid")]
    [InlineData("zones:a")]
    [InlineData("pv:a")]
    public void Anything_else_is_not_a_plan_class(string text)
    {
        Assert.False(EnergySourceText.TryParsePlan(text, out _));
        Assert.False(EnergySourceText.TryParsePlan(null, out _));
    }

    [Fact]
    public void An_economics_class_names_the_kind_and_never_a_zone()
    {
        Assert.Equal(["pv", "zone", "zone"], new EnergySource[] { EnergySource.Surplus.Instance, new EnergySource.Grid(null), new EnergySource.Grid(new ZoneId("offpeak")) }.Select(EnergySourceText.Class));
        Assert.Equal(new EnergySource.Grid(null), EnergySourceText.TryParseClass("zone", out var grid) ? grid : null);
        Assert.False(EnergySourceText.TryParseClass("zone:offpeak", out _));
    }

    [Fact]
    public void Surplus_sorts_before_grid_and_zones_sort_ordinally()
    {
        // Why: the planner breaks ties by this order (surplus first, then the zone id), so a plan is the same on every machine and culture.
        EnergySource[] shuffled = [new EnergySource.Grid(new ZoneId("b")), new EnergySource.Grid(new ZoneId("B")), EnergySource.Surplus.Instance, new EnergySource.Grid(null), new EnergySource.Grid(new ZoneId("a"))];

        var sorted = shuffled.Order(EnergySource.Ordering).Select(EnergySourceText.Plan);

        Assert.Equal(["pv", "zone", "zone:B", "zone:a", "zone:b"], sorted);
    }

    [Fact]
    public void A_source_set_is_made_through_its_factories_only()
    {
        // Why: a public constructor could build a set the grammar refuses (a zone twice, zones with "any"), which TryParse and PvAnd make impossible.
        Assert.Empty(typeof(SourceSet).GetConstructors());
    }

    [Fact]
    public void Pv_and_zones_is_canonical_and_no_zone_is_pv_only()
    {
        var set = SourceSet.PvAnd([new ZoneId("weekend"), new ZoneId("offpeak"), new ZoneId("weekend")]);

        Assert.Equal("pv_and_zone:offpeak,weekend", set.ToText());
        Assert.Equal(SourceSet.PvOnly, SourceSet.PvAnd([]));
        Assert.Equal(set, SourceSet.PvAnd([new ZoneId("offpeak"), new ZoneId("weekend")]));
    }
}
