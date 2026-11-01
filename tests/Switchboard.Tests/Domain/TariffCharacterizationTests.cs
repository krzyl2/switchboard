using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Switchboard.Application.Configuration;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Domain;

/// <summary>
/// Pins what a tariff answers over a sweep of kinds of spec (flat, zoned G13, dynamic with and without an add, a zoned fallback, a
/// dynamic fallback), instants (a 23-hour day, a 25-hour day, a weekday, a weekend, a holiday) and price inputs (none, fresh, negative,
/// stale, not a number). Why: the tariff decides every price the dispatch and the planner compare, and a file without economics must decide as
/// before; the digest below was recorded on the single-class tariff before it was split by kind and must never be edited to follow a change.
/// </summary>
public sealed class TariffCharacterizationTests
{
    private const string RecordedDigest = "A1DE6CABF9D2B80D35D1C2F5E2FC65031748B6774F8643C0B7488589D0417BBA";

    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly SignalName Market = SignalName.Parse("market_price");

    private static readonly string G13Snippet = File.ReadAllText(RepoPaths.Combine("deploy", "examples", "economics-g13.yaml"));

    private static readonly EconomicsDefinition G13Definition =
        TestConfigurations.Valid(TestConfigurations.ThreePhase() + "\n" + G13Snippet).Configuration!.Economics!;

    private static readonly ZonedPrice G13 = (ZonedPrice)G13Definition.Tariff!;

    private static readonly ZonedPrice Gapped = new(
        [new TariffZone("day", 0.5, [new ZoneRule([DayClass.Workday], [], [new HoursRange(2 * 60, 3 * 60)])]), new TariffZone("rest", 0.2, [])],
        null);

    private static IEnumerable<(string Name, PriceSpec Spec)> Specs()
    {
        yield return ("flat", new FlatPrice(0.9));
        yield return ("g13", G13);
        yield return ("gapped", Gapped);
        yield return ("dyn-add-flat", new DynamicPrice(Market, new FlatPrice(0.9), G13));
        yield return ("dyn-noadd-g13", new DynamicPrice(Market, G13, null));
        yield return ("dyn-add-g13", new DynamicPrice(Market, G13, G13));
        yield return ("dyn-add-gapped", new DynamicPrice(Market, new FlatPrice(0.9), Gapped));
        yield return ("dyn-gapped-fallback", new DynamicPrice(Market, Gapped, null));
        yield return ("dyn-nested", new DynamicPrice(Market, new DynamicPrice(Market, G13, G13), G13));
    }

    private static IEnumerable<(string Name, PriceInputs? Inputs)> Inputs()
    {
        yield return ("none", null);
        yield return ("empty", new PriceInputs(ImmutableDictionary<SignalName, PriceSample>.Empty));
        yield return ("fresh", Sample(SignalState.Fresh, 0.30));
        yield return ("negative", Sample(SignalState.Fresh, -0.5));
        yield return ("stale", Sample(SignalState.Stale, 0.30));
        yield return ("nan", Sample(SignalState.Fresh, double.NaN));
    }

    private static PriceInputs Sample(SignalState state, double value) =>
        new(ImmutableDictionary<SignalName, PriceSample>.Empty.Add(Market, new PriceSample(state, SignalDetail.None, value, DateTimeOffset.UnixEpoch)));

    private static IEnumerable<DateTimeOffset> Instants()
    {
        foreach (var (from, days, step) in new[]
        {
            (new DateTimeOffset(2026, 3, 28, 0, 0, 0, TimeSpan.Zero), 3, 20),
            (new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero), 3, 20),
            (new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero), 7, 60),
            (new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero), 2, 90),
            (new DateTimeOffset(2026, 11, 10, 22, 0, 0, TimeSpan.Zero), 3, 90),
        })
        {
            for (var m = 0; m < days * 24 * 60; m += step)
            {
                yield return from.AddMinutes(m).AddSeconds(m % 7);
            }
        }
    }

    private static string Render()
    {
        var text = new StringBuilder();
        var instants = Instants().ToList();
        foreach (var (name, spec) in Specs())
        {
            var tariff = Tariff.For(spec, G13Definition.Holidays, Warsaw);
            text.Append(CultureInfo.InvariantCulture, $"{name} zoned={tariff.IsZoned}\n");
            foreach (var (inputName, inputs) in Inputs())
            {
                var i = 0;
                foreach (var t in instants)
                {
                    var price = tariff.PriceAt(t, inputs);
                    var zone = tariff.ZoneAt(t, inputs);
                    var line = FormattableString.Invariant($"{name}|{inputName}|{t:O}|{price?.PerKwh:R}|{price?.Basis}|{price?.Zone}|{zone?.Id}|{zone?.Price:R}");
                    text.Append(line);
                    if (i++ % 5 == 0)
                    {
                        var next = tariff.NextChange(t, inputs);
                        text.Append(FormattableString.Invariant($"|{next?.At:O}|{next?.Zone}"));
                    }

                    text.Append('\n');
                }
            }
        }

        return text.ToString();
    }

    [Fact]
    public void The_tariff_answers_a_sweep_of_specs_instants_and_inputs_as_recorded()
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Render())));

        Assert.Equal(RecordedDigest, digest);
    }

    [Fact]
    public void A_flat_tariff_has_no_zone_and_never_changes_whatever_the_inputs()
    {
        // Why: a file without economics, or with a flat price, must decide as before: one price, basis flat, no zone to wait for.
        var tariff = Tariff.For(new FlatPrice(0.9), HolidayCalendar.Empty, Warsaw);
        var t = new DateTimeOffset(2026, 7, 15, 8, 30, 0, TimeSpan.Zero);

        Assert.False(tariff.IsZoned);
        Assert.Equal(new PricePoint(0.9, PriceBasis.Flat, null), tariff.PriceAt(t, Sample(SignalState.Fresh, 0.1)));
        Assert.Null(tariff.ZoneAt(t));
        Assert.Null(tariff.NextChange(t));
    }

    [Fact]
    public void A_dynamic_tariff_is_zoned_when_its_add_or_its_fallback_is()
    {
        Assert.True(Tariff.For(new DynamicPrice(Market, new FlatPrice(1), G13), G13Definition.Holidays, Warsaw).IsZoned);
        Assert.True(Tariff.For(new DynamicPrice(Market, G13, null), G13Definition.Holidays, Warsaw).IsZoned);
        Assert.False(Tariff.For(new DynamicPrice(Market, new FlatPrice(1), new FlatPrice(2)), G13Definition.Holidays, Warsaw).IsZoned);
    }
}
