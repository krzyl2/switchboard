using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.State;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Goals;
using Switchboard.Infrastructure.Persistence;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// PLAN-03: the goal ledger in state.json (schema 4, since schema 5). A file of 0.10 (schema 3) must still read, as "no goals"; a file a person edited or a
/// crash cut must be set aside as a whole, like every other part of state.json, never half trusted.
/// </summary>
public sealed class GoalLedgerPersistenceTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 18, 0, 0, TimeSpan.Zero);
    private static readonly DeviceName Car = DeviceName.Parse("ev_charger");
    private static readonly DeviceName Boiler = DeviceName.Parse("boiler");

    private readonly TempDataDirectory _data = new();

    public void Dispose() => _data.Dispose();

    private RuntimeStateFile File() => new(_data.Path, TestDecisions.Options(), NullLogger<RuntimeStateFile>.Instance);

    private static RuntimeState WithGoals() => RuntimeState.Fresh with
    {
        SavedAt = At,
        Goals = System.Collections.Immutable.ImmutableDictionary<DeviceName, GoalLedgerEntry>.Empty
            .Add(Car, new GoalLedgerEntry(GoalKind.Energy, At.AddHours(-2), At.AddHours(12), 4.25, At))
            .Add(Boiler, new GoalLedgerEntry(GoalKind.Energy, At.AddHours(-1), null, 0, At)),
    };

    [Fact]
    public void The_ledger_round_trips_with_its_deadline_or_none_and_the_file_is_schema_5()
    {
        var file = File();

        file.Save(WithGoals());
        var load = file.Load();

        Assert.Equal(StateFileStatus.Loaded, load.Status);
        Assert.Equal(WithGoals().Goals.OrderBy(g => g.Key), load.State.Goals.OrderBy(g => g.Key));
        using var json = JsonDocument.Parse(System.IO.File.ReadAllText(file.FilePath));
        Assert.Equal(5, json.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal(["kind", "since", "deadline", "delivered_kwh", "updated_at"], json.RootElement.GetProperty("goals").GetProperty("ev_charger").EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("goals").GetProperty("boiler").GetProperty("deadline").ValueKind);
    }

    [Fact]
    public void A_schema_3_file_of_0_10_reads_as_no_goals_with_everything_else_intact()
    {
        // Why: the upgrade must not reset the kill switch or the budgets because a ledger did not exist yet. An old file simply has no goals.
        var file = File();
        file.Save(RuntimeState.Fresh with { KillSwitch = true, SavedAt = At });
        var text = System.IO.File.ReadAllText(file.FilePath);
        var schema3 = System.Text.RegularExpressions.Regex.Replace(text.Replace("\"schema_version\":5", "\"schema_version\":3", StringComparison.Ordinal), ",\"(goals|manual_goals)\":\\{\\}", string.Empty);
        Assert.DoesNotContain("goals", schema3, StringComparison.Ordinal);
        System.IO.File.WriteAllText(file.FilePath, schema3);

        var load = file.Load();

        Assert.Equal(StateFileStatus.Loaded, load.Status);
        Assert.Empty(load.State.Goals);
        Assert.True(load.State.KillSwitch);
    }

    [Fact]
    public void A_schema_3_file_cannot_carry_goals()
    {
        // Why: the key set of each schema is exact; a goals key in an old-schema file is an edited file, not a ledger.
        var file = File();
        file.Save(WithGoals());
        System.IO.File.WriteAllText(file.FilePath, System.IO.File.ReadAllText(file.FilePath).Replace("\"schema_version\":5", "\"schema_version\":3", StringComparison.Ordinal));

        var load = file.Load();

        Assert.Equal(StateFileStatus.ResetCorrupt, load.Status);
        Assert.Empty(load.State.Goals);
    }

    [Theory]
    [InlineData("\"kind\":\"energy\",\"since\":\"2026-10-26T16:00:00.0000000Z\",\"deadline\":\"2026-10-27T06:00:00.0000000Z\"", "\"kind\":\"watts\",\"since\":\"2026-10-26T16:00:00.0000000Z\",\"deadline\":\"2026-10-27T06:00:00.0000000Z\"")]
    [InlineData("\"delivered_kwh\":4.25", "\"delivered_kwh\":-1")]
    [InlineData("\"delivered_kwh\":4.25", "\"delivered_kwh\":1e999")]
    [InlineData("\"delivered_kwh\":4.25", "\"delivered_kwh\":\"4.25\"")]
    [InlineData("\"delivered_kwh\":4.25", "\"delivered_kwh\":2000000")]
    [InlineData("\"ev_charger\":{\"kind\"", "\"switch.charger\":{\"kind\"")]
    [InlineData("\"ev_charger\":{\"kind\"", "\"EV Charger\":{\"kind\"")]
    [InlineData("\"delivered_kwh\":4.25,", "\"delivered_kwh\":4.25,\"extra\":1,")]
    [InlineData("\"since\":\"2026-10-26T16:00:00.0000000Z\"", "\"since\":\"yesterday\"")]
    [InlineData("\"since\":\"2026-10-26T16:00:00.0000000Z\"", "\"since\":null")]
    public void Untrusted_goal_content_refuses_the_whole_file(string valid, string tampered)
    {
        // Why: an edited ledger must never half apply: a delivered figure the file invented would make a goal look met and leave a trip
        // uncharged. Anything out of shape resets the whole state, as for every other key.
        var file = File();
        file.Save(WithGoals());
        var text = System.IO.File.ReadAllText(file.FilePath);
        Assert.Contains(valid, text, StringComparison.Ordinal);
        System.IO.File.WriteAllText(file.FilePath, text.Replace(valid, tampered, StringComparison.Ordinal));

        var load = file.Load();

        Assert.Equal(StateFileStatus.ResetCorrupt, load.Status);
        Assert.Empty(load.State.Goals);
        Assert.True(System.IO.File.Exists(file.FilePath + RuntimeStateFile.CorruptSuffix));
    }

    [Fact]
    public void The_goals_key_must_be_an_object()
    {
        var file = File();
        file.Save(WithGoals());
        var text = System.IO.File.ReadAllText(file.FilePath);
        var start = text.IndexOf("\"goals\":", StringComparison.Ordinal);
        System.IO.File.WriteAllText(file.FilePath, text[..start] + "\"goals\":[]}");

        Assert.Equal(StateFileStatus.ResetCorrupt, file.Load().Status);
    }
}
