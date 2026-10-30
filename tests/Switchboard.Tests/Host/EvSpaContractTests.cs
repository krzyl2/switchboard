using System.Text.Json;
using System.Text.RegularExpressions;
using Switchboard.Application.Ev;
using Switchboard.Application.Signals;
using Switchboard.Domain.Ev;
using Switchboard.Domain.Planning;
using Switchboard.Domain.Signals;
using Switchboard.Host.Web;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// The SPA reads GET api/ev strictly (web/src/api/ev.ts) and words each refusal of the manual control by its code: a closed list that drifts from the
/// backend's enum, a key the endpoint stopped writing, or a code with no sentence turns the EV card "unavailable" or into a blank message instead of an
/// honest one. These tests hold the lists, the keys of the committed sample and the sentences of en and pl to what the endpoint writes (docs/EV.md section 11).
/// </summary>
public sealed class EvSpaContractTests
{
    private static readonly string Contract = RepoPaths.Combine("web", "src", "api", "ev.ts");
    private static readonly string PlanContract = RepoPaths.Combine("web", "src", "api", "plan.ts");
    private static readonly string SampleFile = RepoPaths.Combine("web", "src", "api", "ev.sample.json");
    private static readonly string Sentences = File.ReadAllText(RepoPaths.Combine("web", "src", "i18n", "ev.ts"));
    private static readonly string PlanSentences = File.ReadAllText(RepoPaths.Combine("web", "src", "i18n", "plan.ts"));
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string[] Names<T>() where T : struct, Enum => [.. Enum.GetValues<T>().Select(ApiText.Snake).Order(StringComparer.Ordinal)];

    private static string[] Spa(string file, string name) => [.. SpaContract.List(file, name).Order(StringComparer.Ordinal)];

    /// <summary>
    /// The car plugged at 40 % with a trip ahead, a charge-now running and the module's snapshot published: every nested object of the answer is present
    /// except the two lists that only an ignored event fills (see <see cref="An_ignored_event_has_the_keys_the_spa_reads"/>).
    /// </summary>
    private static JsonElement Full(out DateTimeOffset at)
    {
        var rig = new EvRig();
        rig.PlugInCar(40);
        rig.PushRange(54);
        rig.PushCalendar(rig.Trip(TimeSpan.FromHours(10), 42, "Kurs Warszawa"));
        rig.Tick();
        at = rig.Now;
        var manual = new EvManual(EvMode.Auto, new EvChargeNow(rig.Now, 80, rig.Now + TimeSpan.FromHours(2)));

        return JsonSerializer.SerializeToElement(
            EvEndpoint.Build(rig.H.Configuration, at, ((IEvInputSource)rig).Current.Inputs, EvRig.Warsaw, rig.Keeper.Current, manual), Web);
    }

    [Fact]
    public void Spa_closed_lists_match_the_backend_exactly()
    {
        // Why: a mode, goal kind, gap, ignored-event reason or refusal code the SPA does not know makes the card "unavailable" (or a blank message); one it lists
        // but the backend never sends is dead text. Both directions are held.
        Assert.Equal(Names<EvMode>(), Spa(Contract, "EV_MODES"));
        Assert.Equal(["auto", "full_charge", "pv_only"], Spa(Contract, "EV_MODES"));
        Assert.Equal(Names<EvGoalKind>(), Spa(Contract, "EV_GOAL_KINDS"));
        Assert.Equal(Names<SocEstimateGap>(), Spa(Contract, "EV_SOC_GAPS"));
        Assert.Equal(Names<UnparsedReason>(), Spa(Contract, "EV_UNPARSED_REASONS"));
        Assert.Equal(Names<TripWarningKind>(), Spa(Contract, "EV_TRIP_WARNINGS"));
        Assert.Equal(Names<PlannerDispatchMode>(), Spa(Contract, "EV_PLANNER_DISPATCH"));
        Assert.Equal([.. EvAssessmentCodes.All.Select(EvAssessmentCodes.Text).Order(StringComparer.Ordinal)], Spa(PlanContract, "EV_CAUSES"));
        Assert.Equal(14, Spa(PlanContract, "EV_CAUSES").Length);
    }

    [Fact]
    public void Spa_refusal_codes_are_the_ones_the_endpoint_sends()
    {
        // Why: each refusal of POST api/ev/mode and api/ev/charge-now is worded in the SPA by the code in the body; a code the SPA does not list would show as
        // a generic failure, and one it lists that is never sent is dead text. EV.md section 11 names ten.
        var sent = new[]
        {
            EvEndpoint.BadRequest, EvEndpoint.NotConfigured, EvEndpoint.NoChargeNow, EvEndpoint.NeedsFullDispatch, EvEndpoint.NotPlugged, EvEndpoint.SocUnknown,
            EvEndpoint.AlreadyAtTarget, EvEndpoint.TargetOutOfRange, EvEndpoint.DurationOutOfRange, EvEndpoint.DeadlineBeyondHorizon,
        };

        Assert.Equal(10, sent.Length);
        Assert.Equal([.. sent.Order(StringComparer.Ordinal)], Spa(Contract, "EV_REFUSALS"));
    }

    [Fact]
    public void Every_listed_value_has_a_sentence_in_english_and_in_polish()
    {
        // Why: CLAUDE.md: UI chrome has en and pl. A list value with a sentence in only one language shows the key itself in the other.
        static void Both(string sentences, string prefix, IEnumerable<string> values)
        {
            foreach (var value in values)
            {
                var key = $"'{prefix}{value}':";
                Assert.True(Regex.Count(sentences, Regex.Escape(key)) == 2, $"{prefix}{value} must be worded exactly twice (en, pl)");
            }
        }

        Both(Sentences, "ev.mode.", Spa(Contract, "EV_MODES"));
        Assert.DoesNotContain("'ev.mode.off':", Sentences, StringComparison.Ordinal);
        Both(Sentences, "ev.refused.", [.. Spa(Contract, "EV_REFUSALS"), "failed"]);
        Both(Sentences, "ev.goal.kind.", Spa(Contract, "EV_GOAL_KINDS"));
        Both(Sentences, "ev.socGap.", Spa(Contract, "EV_SOC_GAPS"));
        Both(Sentences, "ev.unparsed.", Spa(Contract, "EV_UNPARSED_REASONS"));
        Both(Sentences, "ev.warn.", Spa(Contract, "EV_TRIP_WARNINGS"));
        Both(PlanSentences, "plan.cause.", Spa(PlanContract, "EV_CAUSES"));
    }

    [Fact]
    public void Spa_sample_has_exactly_the_keys_the_endpoint_writes()
    {
        var real = Full(out var at);
        var sample = SpaContract.Sample(SampleFile, real, at);

        Assert.Equal(SpaContract.Describe([real]), SpaContract.Describe([sample]));
        // The sample is the interesting case: the module has offered goals, the charge-now is running, and there is a next trip.
        Assert.Equal("auto", sample.GetProperty("manual").GetProperty("mode").GetString());
        Assert.True(sample.GetProperty("manual").GetProperty("chargeNow").GetProperty("active").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, sample.GetProperty("assessment").GetProperty("reason").ValueKind);
        Assert.True(sample.GetProperty("assessment").GetProperty("goals").GetArrayLength() > 0);
        Assert.NotEqual(JsonValueKind.Null, sample.GetProperty("trips").GetProperty("nextTrip").ValueKind);
    }

    [Fact]
    public void An_ignored_event_has_the_keys_the_spa_reads()
    {
        // Why: the sample has no ignored event (the calendar shows one event), so the shape of an `unparsed` and of a `warnings` element is held here; the SPA
        // reads title, start, reason, km and kind strictly.
        var rig = new EvRig();
        rig.PlugInCar(40);
        rig.PushCalendar(new CalendarEvent("Urlop", "urlop", rig.Now + TimeSpan.FromHours(3), rig.Now + TimeSpan.FromHours(4), true));
        rig.Tick();

        var trips = JsonSerializer.SerializeToElement(
            EvEndpoint.Build(rig.H.Configuration, rig.Now, ((IEvInputSource)rig).Current.Inputs, EvRig.Warsaw, rig.Keeper.Current), Web).GetProperty("trips");

        Assert.Equal(["title", "start", "reason", "km"], trips.GetProperty("unparsed")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(["kind", "title", "start"], trips.GetProperty("warnings")[0].EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void A_charge_now_that_is_not_running_has_the_same_keys_with_nulls()
    {
        // Why: the SPA reads `chargeNow` strictly whether or not one runs; a missing key would blank the controls exactly when the user wants to start one.
        var running = Full(out var at).GetProperty("manual").GetProperty("chargeNow");
        var rig = new EvRig();
        var idle = JsonSerializer.SerializeToElement(EvEndpoint.Build(rig.H.Configuration, at, manual: EvManual.Default), Web).GetProperty("manual").GetProperty("chargeNow");

        Assert.Equal(running.EnumerateObject().Select(p => p.Name), idle.EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Null, idle.GetProperty("until").ValueKind);
    }
}
