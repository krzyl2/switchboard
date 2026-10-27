using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Switchboard.Application.Guard;
using Switchboard.Application.Modes;
using Switchboard.Application.Signals;
using Switchboard.Application.State;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Infrastructure.Persistence;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Infrastructure;

/// <summary>
/// /data/state.json carries the kill switch, the overload budgets, the control switch, the
/// observation record and the commanded restore baselines across a restart (SAFE-07), and never a
/// lockout expiry. A user may edit it and a crash may cut it, so it is read as untrusted input:
/// anything unexpected is set aside with a warning (GuardService then starts with control locked off
/// and the kill switch on), never a crash and never a half-trusted value.
/// </summary>
public sealed class RuntimeStateFileTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDataDirectory _data = new();

    [Fact]
    public void Round_trip_keeps_every_field()
    {
        var file = File();
        var state = Full();

        file.Save(state);
        var load = file.Load();

        Assert.Equal(StateFileStatus.Loaded, load.Status);
        Assert.Null(load.Warning);
        var loaded = load.State;
        Assert.Equal(
            (state.SavedAt, state.CleanShutdown, state.KillSwitch, state.KillSwitchChangedAt),
            (loaded.SavedAt, loaded.CleanShutdown, loaded.KillSwitch, loaded.KillSwitchChangedAt));
        Assert.Equal(state.Budgets.OrderBy(b => b.Key), loaded.Budgets.OrderBy(b => b.Key));
        Assert.Equal(state.Devices[DeviceName.Parse("heater_l1")], loaded.Devices[DeviceName.Parse("heater_l1")]);
        Assert.Equal(state.Devices[DeviceName.Parse("ev_charger")], loaded.Devices[DeviceName.Parse("ev_charger")]);
        Assert.Equal(state.Discovered, loaded.Discovered);
        Assert.Equal(state.Control, loaded.Control);
        Assert.Equal(state.Observation, loaded.Observation);
        Assert.Equal(state.StartBaselines.OrderBy(b => b.Key), loaded.StartBaselines.OrderBy(b => b.Key));
        Assert.Equal(state.SelfStarted.OrderBy(b => b.Key), loaded.SelfStarted.OrderBy(b => b.Key));
    }

    [Fact]
    public void A_schema_2_file_of_0_6_x_reads_with_control_off_and_its_ui_mode_ignored()
    {
        // The upgrade never starts commanding: the Stage 5 UI choice is not the control switch, and
        // the switch reads off until the user turns it on (maintainer survey 2026-10-25).
        const string Version2 = """{"schema_version":2,"saved_at":"2026-10-24T11:00:00.0000000Z","clean_shutdown":true,"kill_switch":false,"kill_switch_changed_at":null,"ui_mode":"active","phases":{},"devices":{},"mqtt":{"discovered":[]},"overrides":{},"group_budgets":{}}""";
        var file = File();
        System.IO.File.WriteAllText(file.FilePath, Version2);

        var load = file.Load();

        Assert.Equal(StateFileStatus.Loaded, load.Status);
        Assert.Equal(ControlSwitchStatus.Default, load.State.Control);
        Assert.Null(load.State.Observation);
        Assert.Empty(load.State.StartBaselines);
    }

    [Fact]
    public void State_holds_no_lockout_expiry()
    {
        // SAFE-07: every lockout restarts from the restart instant, so the file has no place for one.
        File().Save(Full());

        using var json = JsonDocument.Parse(System.IO.File.ReadAllText(Path.Combine(_data.Path, "state.json")));
        Assert.Equal(
            ["schema_version", "saved_at", "clean_shutdown", "kill_switch", "kill_switch_changed_at", "phases", "devices", "mqtt", "overrides", "group_budgets", "control", "observation", "start_baselines", "self_started", "goals"],
            json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(4, json.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal(["level", "shed", "since"], json.RootElement.GetProperty("start_baselines").GetProperty("heater_l1").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["budget", "at"], json.RootElement.GetProperty("phases").GetProperty("L1").EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain(typeof(RuntimeState).GetProperties(), p => p.Name.Contains("Lockout", StringComparison.OrdinalIgnoreCase) || p.PropertyType == typeof(TimeSpan));
    }

    [Fact]
    public void Select_baseline_keeps_option_before_across_restart()
    {
        // 0.7.1: a charger stopped through its select keeps the option it reported before the stop,
        // so the log after a restart can still name an undeclared option that is not restored.
        var file = File();
        var stopped = new Switchboard.Application.Effector.StartBaseline(DeviceLevel.At(new Amperes(10)), DeviceLevel.At(new Amperes(6), enabled: false), At.AddMinutes(-4), true) { OptionBefore = OptionText.Parse("2") };
        file.Save(Full() with { StartBaselines = Full().StartBaselines.SetItem(DeviceName.Parse("ev_charger"), stopped) });

        var loaded = file.Load();

        Assert.Equal(StateFileStatus.Loaded, loaded.Status);
        Assert.Equal(stopped, loaded.State.StartBaselines[DeviceName.Parse("ev_charger")]);
        Assert.Equal(OptionText.Parse("2"), loaded.State.StartBaselines[DeviceName.Parse("ev_charger")].OptionBefore);
    }

    [Fact]
    public void Baseline_without_option_before_is_written_as_in_0_7_0()
    {
        // A switch stop or a relay keeps the 0.7.0 shape exactly (no key, not even null), so a file
        // without a select stop still reads in 0.7.0.
        File().Save(Full());

        var text = System.IO.File.ReadAllText(Path.Combine(_data.Path, "state.json"));

        Assert.DoesNotContain("option_before", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"option_before\":\"\"")]
    [InlineData("\"option_before\":1")]
    [InlineData("\"option_before\":null")]
    [InlineData("\"option_before\":\"a\\nb\"")]
    public void Untrusted_option_before_refuses_the_file(string tampered)
    {
        var file = File();
        var stopped = new Switchboard.Application.Effector.StartBaseline(DeviceLevel.At(new Amperes(10)), DeviceLevel.At(new Amperes(6), enabled: false), At, true) { OptionBefore = OptionText.Parse("0") };
        file.Save(Full() with { StartBaselines = Full().StartBaselines.SetItem(DeviceName.Parse("ev_charger"), stopped) });
        var text = System.IO.File.ReadAllText(file.FilePath);
        Assert.Contains("\"option_before\":\"0\"", text, StringComparison.Ordinal);
        System.IO.File.WriteAllText(file.FilePath, text.Replace("\"option_before\":\"0\"", tampered, StringComparison.Ordinal));

        var load = file.Load();

        Assert.Equal(StateFileStatus.ResetCorrupt, load.Status);
    }

    [Fact]
    public void Written_atomically_via_temp_and_rename()
    {
        var file = File();
        file.Save(Full());
        var before = System.IO.File.ReadAllBytes(file.FilePath);

        // The temporary file cannot be created (here: a folder in its way, as a full disk would):
        // the write fails, and the previous state stays whole and loadable.
        Directory.CreateDirectory(file.FilePath + RuntimeStateFile.TemporarySuffix);
        var failure = Record.Exception(() => file.Save(Full() with { KillSwitch = false }));
        Assert.True(failure is IOException or UnauthorizedAccessException, $"Expected an I/O failure, got {failure}");
        Assert.Equal(before, System.IO.File.ReadAllBytes(file.FilePath));
        Assert.True(file.Load().State.KillSwitch);

        Directory.Delete(file.FilePath + RuntimeStateFile.TemporarySuffix);
        file.Save(Full() with { KillSwitch = false });
        Assert.False(file.Load().State.KillSwitch);
        Assert.False(System.IO.File.Exists(file.FilePath + RuntimeStateFile.TemporarySuffix), "No temporary file is left behind.");
    }

    [Fact]
    public void Corrupt_state_starts_fresh_and_keeps_the_bad_file()
    {
        var file = File();
        System.IO.File.WriteAllText(file.FilePath, "{\"schema_version\":1,\"kill_swi");

        var load = file.Load();

        Assert.Equal(StateFileStatus.ResetCorrupt, load.Status);
        Assert.Equal(RuntimeState.Fresh, load.State);
        Assert.Contains("state.json.corrupt", load.Warning, StringComparison.Ordinal);
        Assert.Equal("{\"schema_version\":1,\"kill_swi", System.IO.File.ReadAllText(file.FilePath + RuntimeStateFile.CorruptSuffix));
        Assert.False(System.IO.File.Exists(file.FilePath));
    }

    [Theory]
    [InlineData("\"budget\":0.83", "\"budget\":1.5")]
    [InlineData("\"budget\":0.83", "\"budget\":-0.1")]
    [InlineData("\"schema_version\":4", "\"schema_version\":5")]
    [InlineData("\"allowed\":true", "\"allowed\":\"yes\"")]
    [InlineData("\"locked\":false", "\"locked\":true")]
    [InlineData("\"source\":\"api\"", "\"source\":\"browser\"")]
    [InlineData("\"configuration_errors\":2", "\"configuration_errors\":-1")]
    [InlineData("\"relays\":[true]", "\"relays\":[]")]
    [InlineData("\"relays\":[true]", "\"relays\":[true],\"setpoint_b\":1")]
    [InlineData("\"heater_l2\":\"2026", "\"switch.heater_2\":\"2026")]
    [InlineData("\"L1\":{", "\"L4\":{")]
    [InlineData("\"heater_l1\":{", "\"switch.heater\":{")]
    [InlineData("\"applied\":\"on\"", "\"applied\":\"maybe\"")]
    [InlineData("\"setpoint_a\":16", "\"setpoint_a\":\"16\"")]
    [InlineData("\"setpoint_a\":16", "\"setpoint_a\":1e999")]
    [InlineData("\"kill_switch\":true", "\"kill_switch\":\"yes\"")]
    [InlineData("\"clean_shutdown\":true,", "\"clean_shutdown\":true,\"lockout_until\":\"2026-10-24T12:00:00.0000000Z\",")]
    [InlineData("\"clean_shutdown\":true,", "\"clean_shutdown\":true,\"clean_shutdown\":true,")]
    [InlineData("\"sensor/l1_headroom\"", "\"sensor/../x\"")]
    [InlineData("\"at\":\"2026-10-24T", "\"at\":\"24.10.2026 ")]
    public void Untrusted_content_is_refused_as_a_whole(string valid, string tampered)
    {
        // Any value out of shape resets the whole state; partially trusting an edited file could
        // keep a kill switch released or a budget full that should not be.
        var file = File();
        file.Save(Full());
        var text = System.IO.File.ReadAllText(file.FilePath);
        Assert.Contains(valid, text, StringComparison.Ordinal);
        System.IO.File.WriteAllText(file.FilePath, text.Replace(valid, tampered, StringComparison.Ordinal));

        var load = file.Load();

        Assert.Equal(StateFileStatus.ResetCorrupt, load.Status);
        Assert.Equal(RuntimeState.Fresh, load.State);
    }

    [Fact]
    public void A_discharging_storage_device_survives_a_restart_with_the_kill_switch()
    {
        // A battery discharging at −10 A when the state is saved is a known state (the signal layer
        // accepts a negative storage setpoint). Refusing it would reset the whole file at the next
        // start: an engaged kill switch released, budgets refilled, overrides gone.
        var file = File();
        var state = Full() with
        {
            Devices = Full().Devices.Add(DeviceName.Parse("battery"), [new StageLastKnown(new SetpointApplied(new Amperes(-10)), At.AddMinutes(-1))]),
        };

        file.Save(state);
        var load = file.Load();

        Assert.Equal(StateFileStatus.Loaded, load.Status);
        Assert.True(load.State.KillSwitch);
        Assert.Equal(new SetpointApplied(new Amperes(-10)), load.State.Devices[DeviceName.Parse("battery")][0]!.Applied);
    }

    [Fact]
    public void A_file_above_the_size_limit_is_not_read()
    {
        var file = File();
        System.IO.File.WriteAllText(file.FilePath, "{" + new string(' ', TestDecisions.Options().StateMaxFileBytes) + "}");

        Assert.Equal(StateFileStatus.ResetCorrupt, file.Load().Status);
    }

    [Fact]
    public void Missing_file_is_a_first_start_without_a_warning()
    {
        var load = File().Load();

        Assert.Equal((StateFileStatus.Fresh, (string?)null), (load.Status, load.Warning));
        Assert.Equal((false, OperatingMode.Shadow, false), (load.State.KillSwitch, load.State.UiMode, load.State.CleanShutdown));
    }

    [Fact]
    public async Task Kill_switch_survives_restart()
    {
        // Engaged from Home Assistant, saved at once, and engaged again from the first evaluation
        // after a restart, with the restart as its source.
        var file = File();
        var coordinator = new RuntimeStateCoordinator(file, file.Load(), () => null, Microsoft.Extensions.Options.Options.Create(TestDecisions.Options()), TimeProvider.System, NullLogger<RuntimeStateCoordinator>.Instance);
        coordinator.OnKillSwitchChanged(new KillSwitchStatus(true, At, KillSwitchSource.Mqtt));
        await coordinator.FlushAsync(CancellationToken.None);

        var restarted = File().Load();
        var killSwitch = new KillSwitchState(new GuardInbox(), new SignalClock(new SteppableTime(At)), NullLogger<KillSwitchState>.Instance);
        killSwitch.Restore(restarted.State.KillSwitch, restarted.State.KillSwitchChangedAt);

        Assert.Equal(new KillSwitchStatus(true, At, KillSwitchSource.Restored), killSwitch.Status);
    }

    public void Dispose() => _data.Dispose();

    private RuntimeStateFile File() => new(_data.Path, TestDecisions.Options(), NullLogger<RuntimeStateFile>.Instance);

    private static RuntimeState Full() => new RuntimeState(
        At,
        true,
        true,
        At.AddMinutes(-5),
        OperatingMode.Shadow,
        ImmutableDictionary<Phase, BudgetMemory>.Empty
            .Add(Phase.L1, new BudgetMemory(0.83, At.AddSeconds(-10)))
            .Add(Phase.L2, new BudgetMemory(1, At.AddSeconds(-10))),
        ImmutableDictionary<DeviceName, ImmutableArray<StageLastKnown?>>.Empty
            .Add(DeviceName.Parse("heater_l1"), [new StageLastKnown(new RelayApplied(true), At.AddMinutes(-1))])
            .Add(DeviceName.Parse("ev_charger"), [new StageLastKnown(new SetpointApplied(new Amperes(16)), At.AddMinutes(-2))])
            .Add(DeviceName.Parse("boiler"), [null, new StageLastKnown(new RelayApplied(false), At)]),
        ImmutableSortedSet.Create(StringComparer.Ordinal, "sensor/l1_headroom", "switch/kill_switch"))
    {
        Control = new ControlSwitchStatus(true, At.AddMinutes(-3), ControlSwitchSource.Api, false, null),
        Observation = new ObservationRecord(At.AddDays(-9), 2, At.AddDays(-1)),
        StartBaselines = ImmutableDictionary<DeviceName, Switchboard.Application.Effector.StartBaseline>.Empty
            .Add(DeviceName.Parse("heater_l1"), new(DeviceLevel.Relay(true), DeviceLevel.Relay(false), At.AddMinutes(-4), true))
            .Add(DeviceName.Parse("ev_charger"), new(DeviceLevel.At(new Amperes(16)), DeviceLevel.At(new Amperes(6)), At.AddMinutes(-4), true)),
        SelfStarted = ImmutableDictionary<DeviceName, DateTimeOffset>.Empty.Add(DeviceName.Parse("heater_l2"), At.AddMinutes(-20)),
    };
}
