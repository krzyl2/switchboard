using Switchboard.Application.Configuration;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Configuration;

/// <summary>
/// EV-01, slice S1 (docs/EV.md): the optional <c>ev:</c> section and the <c>control.ev</c> parameters. The section is typed by hand, so a
/// wrong word is an error with a position and an unsuitable device is a warning that narrows the module, never one that invalidates the
/// document; an absent section is the file it always was; the parameters are bound and range-checked like every other control parameter,
/// with no literal anywhere but the defaults file. Nothing in this slice reads any of it, so none of it may add a decision.
/// </summary>
public sealed class EvConfigurationTests
{
    private const string Tariff = """
        economics:
          currency: PLN
          holidays: [2026-11-01]
          tariff:
            default_zone: offpeak
            zones:
              - id: peak
                price: 1.60
                when:
                  - { days: [workday], hours: ["07:00-13:00"] }
              - id: offpeak
                price: 0.60
                when: []
          settlement:
            model: hourly_netting
            export_price: 0.35

        """;

    private const string Ev = "ev:\n  device: ev_charger\n  capacity_kwh: 18\n  soc: { entity: sensor.car_battery_level, max_age: 30m }\n";

    private static string Charger(string extraKeys = "sources: pv_and_zone:offpeak\n", string id = "ev_charger") =>
        $"  - id: {id}\n    type: modulating\n    actuator: number.charger_current\n    phase: L1\n    min: 6\n    max: 16\n    step: 1\n    value_per_kwh: 2.03\n"
        + string.Concat(extraKeys.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => "    " + l + "\n"));

    private static string Document(string ev = Ev, string? charger = null, string planner = "planner:\n  dispatch: narrow\n", string control = "", string economics = Tariff, string defaultStart = "headroom") =>
        TestDevices.Document(charger ?? Charger(), control: control, extra: economics + "\n" + planner + "\n" + ev, defaultStart: defaultStart);

    private static ConfigurationResult Parse(string ev = Ev, string? charger = null, string planner = "planner:\n  dispatch: narrow\n", string control = "", string economics = Tariff, string defaultStart = "headroom") =>
        TestConfigurations.Parse(Document(ev, charger, planner, control, economics, defaultStart));

    private static ConfigurationIssue[] EvIssues(ConfigurationResult result) =>
        [.. result.Errors.Concat(result.Warnings).Where(i => i.Path == "ev" || i.Path.StartsWith("ev.", StringComparison.Ordinal))];

    [Fact]
    public void A_file_without_an_ev_section_has_no_ev_and_no_ev_signal()
    {
        // Why: no ev: means no EV module and nothing about the file moves: Ev stays null, no signal is subscribed on the section's behalf.
        var configuration = TestConfigurations.Valid(Document(ev: string.Empty)).Configuration!;

        Assert.Null(configuration.Ev);
        Assert.DoesNotContain(configuration.Signals, s => s.Name.Value.EndsWith("_ev_soc", StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_ev_section_is_the_same_as_none()
    {
        Assert.Null(TestConfigurations.Valid(Document(ev: "ev:\n")).Configuration!.Ev);
    }

    [Fact]
    public void A_complete_section_is_read_and_creates_exactly_one_signal_for_the_soc()
    {
        // Why: the entity ID must stay in the signal layer (CLAUDE.md): the definition carries a logical name only, and the SoC is a
        // measurement judged by age, not a role that any protection, dispatch or goal reader picks up.
        var result = TestConfigurations.Valid(Document(ev: Ev + "  cheap_zone: offpeak\n"));
        var ev = result.Configuration!.Ev!;

        Assert.Equal(("ev_charger", 18.0, "offpeak"), (ev.Device.Value, ev.CapacityKwh, ev.CheapZone));
        var signal = Assert.Single(result.Configuration.Signals, s => s.Name == ev.Soc);
        Assert.Equal(("sensor.car_battery_level", SignalKind.Percentage, "%", SignalRole.EvSoc, TimeSpan.FromMinutes(30)), (signal.Entity.Value, signal.Kind, signal.Unit, signal.Role, signal.MaxAge));
        Assert.Equal("ev.soc", signal.Path);
        Assert.Empty(EvIssues(result));
    }

    [Fact]
    public void The_soc_max_age_defaults_to_the_goal_input_max_age_and_follows_an_override_of_it()
    {
        // Why: the default is the planner parameter, not a literal of the parser: overriding the parameter must move the signal's max_age.
        var ev = "ev:\n  device: ev_charger\n  capacity_kwh: 18\n  soc: { entity: sensor.car_battery_level }\n";
        var shipped = TestConfigurations.Valid(Document(ev: ev)).Configuration!;
        var overridden = TestConfigurations.Valid(Document(ev: ev, control: "  planner:\n    goal:\n      input_max_age: 2h\n")).Configuration!;

        Assert.Equal(TimeSpan.FromHours(1), shipped.Signals.Single(s => s.Name == shipped.Ev!.Soc).MaxAge);
        Assert.Equal(TimeSpan.FromHours(2), overridden.Signals.Single(s => s.Name == overridden.Ev!.Soc).MaxAge);
    }

    [Fact]
    public void A_user_signal_cannot_take_the_name_of_the_soc_signal()
    {
        // Why: the SoC signal's name carries the reserved device_ prefix, so it can never collide with a signal the user declares.
        var name = TestConfigurations.Valid(Document()).Configuration!.Ev!.Soc.Value;
        var clash = $"  {name}:\n    entity: sensor.other\n    kind: percentage\n    unit: \"%\"\n    max_age: 30m\n";
        var result = TestConfigurations.Parse(Document().Replace("\nsignals:\n", "\nsignals:\n" + clash, StringComparison.Ordinal));

        Assert.Contains(result.Errors, e => e.Code == ConfigurationIssueCodes.InvalidSignalName && e.Path == $"signals.{name}");
    }

    [Fact]
    public void A_cheap_zone_of_the_zoned_tariff_is_kept()
    {
        Assert.Equal("peak", TestConfigurations.Valid(Document(ev: Ev + "  cheap_zone: peak\n")).Configuration!.Ev!.CheapZone);
    }

    [Fact]
    public void A_cheap_zone_the_tariff_does_not_have_is_an_error_listing_the_zones()
    {
        var result = Parse(Ev + "  cheap_zone: night\n");

        var error = Assert.Single(result.Errors, e => e.Path == "ev.cheap_zone");
        Assert.Contains("offpeak", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_tariff_a_cheap_zone_cannot_be_checked_and_is_kept_as_written()
    {
        // Why: with no zones to compare against the id is the user's word; refusing it would refuse a file that merely lacks economics:.
        var result = TestConfigurations.Valid(Document(ev: Ev + "  cheap_zone: night\n", economics: string.Empty, charger: Charger(string.Empty)));

        Assert.Equal("night", result.Configuration!.Ev!.CheapZone);
    }

    [Fact]
    public void A_device_that_is_not_declared_is_an_error_naming_the_key()
    {
        // Why: the module may only charge a device the user declared; a typo must not become a silently inert section.
        var result = Parse(Ev.Replace("device: ev_charger", "device: ev_chargr", StringComparison.Ordinal));

        Assert.Equal(ConfigurationStatus.Invalid, result.Status);
        Assert.Single(result.Errors, e => e.Code == ConfigurationIssueCodes.UnknownDevice && e.Path == "ev.device" && e.Line is not null);
    }

    [Fact]
    public void A_device_declared_twice_is_the_devices_sections_error_and_the_section_charges_nothing()
    {
        // Why: with two devices of one id "the device" would be ambiguous; the devices parser refuses the duplicate (document invalid), so
        // there is no configuration for the section to resolve in.
        var result = Parse(charger: Charger(id: "ev_charger") + Charger(id: "ev_charger"));

        Assert.Equal(ConfigurationStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Code == ConfigurationIssueCodes.DuplicateDeviceId);
        Assert.Null(result.Configuration);
    }

    [Fact]
    public void A_device_with_a_goal_of_its_own_keeps_it_and_the_section_is_ignored_with_a_warning()
    {
        // Why: the module makes the soc goal of its device itself (EV B9); two goals for one device would add up and double count. The
        // document stays valid and the user's goal stays exactly as it was: a configuration problem may only narrow, never invalidate.
        var goal = "goal:\n  kind: energy\n  target_kwh: 12\n  deadline: { at: \"06:30\" }\n";
        var result = TestConfigurations.Valid(Document(charger: Charger("sources: pv_and_zone:offpeak\n" + goal)));

        Assert.Null(result.Configuration!.Ev);
        Assert.NotNull(result.Configuration.Devices.Single(d => d.Id.Value == "ev_charger").Goal);
        Assert.DoesNotContain(result.Configuration.Signals, s => s.Name.Value.EndsWith("_ev_soc", StringComparison.Ordinal));
        Assert.Single(result.Warnings, w => w.Code == ConfigurationIssueCodes.EvDeviceHasGoal && w.Path == "ev.device");
    }

    [Fact]
    public void A_restore_only_device_is_warned_that_no_plan_can_start_it_and_the_section_is_kept()
    {
        var result = TestConfigurations.Valid(Document(charger: Charger("sources: pv_and_zone:offpeak\nstart: restore_only\n")));

        Assert.NotNull(result.Configuration!.Ev);
        Assert.Single(result.Warnings, w => w.Code == ConfigurationIssueCodes.EvDeviceCannotStart && w.Path == "ev.device");
        Assert.DoesNotContain(result.Warnings, w => w.Code is ConfigurationIssueCodes.EvDeviceWithoutSources or ConfigurationIssueCodes.EvWithoutPlannerDispatch);
    }

    [Fact]
    public void The_default_start_applies_to_the_ev_device_like_to_any_other()
    {
        // Why: ev_device_cannot_start follows the effective start policy (devices.default_start included), not only a start: key on the device.
        var result = TestConfigurations.Valid(Document(defaultStart: "restore_only"));

        Assert.Single(result.Warnings, w => w.Code == ConfigurationIssueCodes.EvDeviceCannotStart);
    }

    [Fact]
    public void A_device_without_sources_is_warned_and_the_section_is_kept()
    {
        var result = TestConfigurations.Valid(Document(charger: Charger(string.Empty)));

        Assert.NotNull(result.Configuration!.Ev);
        Assert.Single(result.Warnings, w => w.Code == ConfigurationIssueCodes.EvDeviceWithoutSources && w.Path == "ev.device");
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("planner:\n  dispatch: off\n", true)]
    [InlineData("planner:\n  dispatch: narrow\n", false)]
    [InlineData("planner:\n  dispatch: full\n", false)]
    public void Without_a_planner_dispatch_beyond_off_the_module_is_a_preview_and_the_user_is_told(string planner, bool warned)
    {
        // Why: at off (or with no planner) no plan reaches the charger, so the module can only show a preview; the car stays on the v2 rules.
        var result = TestConfigurations.Valid(Document(planner: planner));

        Assert.NotNull(result.Configuration!.Ev);
        Assert.Equal(warned ? 1 : 0, result.Warnings.Count(w => w.Code == ConfigurationIssueCodes.EvWithoutPlannerDispatch));
    }

    [Fact]
    public void A_fully_prepared_device_raises_no_ev_issue_at_all()
    {
        Assert.Empty(EvIssues(TestConfigurations.Valid(Document())));
    }

    [Fact]
    public void A_soc_entity_another_signal_already_reads_is_a_duplicate_entity_error()
    {
        // Why: one entity feeds one signal (SIG-01); the SoC read twice would count as two sources.
        var result = Parse(Ev.Replace("sensor.car_battery_level", "sensor.meter_power_a", StringComparison.Ordinal));

        Assert.Single(result.Errors, e => e.Code == ConfigurationIssueCodes.DuplicateEntity && e.Path == "ev.soc.entity");
        Assert.Equal(ConfigurationStatus.Invalid, result.Status);
    }

    [Fact]
    public void A_soc_entity_the_device_commands_is_a_duplicate_entity_error()
    {
        // Why: an actuator is no sensor of the car; reading it as the SoC would only be a mistake and must not pass quietly.
        var result = Parse(Ev.Replace("sensor.car_battery_level", "number.charger_current", StringComparison.Ordinal));

        Assert.Single(result.Errors, e => e.Code == ConfigurationIssueCodes.DuplicateEntity && e.Path == "ev.soc.entity");
    }

    [Fact]
    public void A_soc_entity_of_an_unsuitable_domain_is_refused()
    {
        var result = Parse(Ev.Replace("sensor.car_battery_level", "switch.car_battery_level", StringComparison.Ordinal));

        Assert.Single(result.Errors, e => e.Code == ConfigurationIssueCodes.InvalidValue && e.Path == "ev.soc.entity");
    }

    [Theory]
    [InlineData("capacity_kwh: 0", ConfigurationIssueCodes.OutOfRange, "ev.capacity_kwh")]
    [InlineData("capacity_kwh: -3", ConfigurationIssueCodes.OutOfRange, "ev.capacity_kwh")]
    [InlineData("capacity_kwh: 18000", ConfigurationIssueCodes.OutOfRange, "ev.capacity_kwh")]
    [InlineData("capacity_kwh: big", ConfigurationIssueCodes.WrongType, "ev.capacity_kwh")]
    [InlineData("colour: red", ConfigurationIssueCodes.UnknownKey, "ev.colour")]
    [InlineData("cheap_zone: night", ConfigurationIssueCodes.InvalidValue, "ev.cheap_zone")]
    [InlineData("cheap_zone: Off Peak", ConfigurationIssueCodes.InvalidValue, "ev.cheap_zone")]
    [InlineData("soc: { entity: sensor.car_battery_level, max_age: 0s }", ConfigurationIssueCodes.OutOfRange, "ev.soc.max_age")]
    [InlineData("soc: { entity: sensor.car_battery_level, max_age: soon }", ConfigurationIssueCodes.InvalidDuration, "ev.soc.max_age")]
    [InlineData("soc: { entity: sensor.car_battery_level, level: 1 }", ConfigurationIssueCodes.UnknownKey, "ev.soc.level")]
    [InlineData("soc: { entity: car_battery_level }", ConfigurationIssueCodes.InvalidEntityId, "ev.soc.entity")]
    [InlineData("soc: { max_age: 30m }", ConfigurationIssueCodes.MissingKey, "ev.soc.entity")]
    public void A_wrong_value_or_key_is_an_error_naming_the_path(string replacement, string code, string path)
    {
        // Why: a typo that fell back to a default would look like a module that works; the section is data the user types.
        var key = replacement[..replacement.IndexOf(':', StringComparison.Ordinal)];
        var lines = Ev.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith($"  {key}:", StringComparison.Ordinal));
        var ev = string.Concat(lines.Select(l => l + "\n")) + $"  {replacement}\n";

        var result = Parse(ev);

        Assert.Equal(ConfigurationStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Code == code && e.Path == path);
    }

    [Theory]
    [InlineData("device")]
    [InlineData("capacity_kwh")]
    [InlineData("soc")]
    public void A_missing_required_key_is_a_missing_key_error(string key)
    {
        var ev = string.Concat(Ev.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith($"  {key}:", StringComparison.Ordinal)).Select(l => l + "\n"));

        var result = Parse(ev);

        Assert.Contains(result.Errors, e => e.Code == ConfigurationIssueCodes.MissingKey && e.Path == $"ev.{key}");
    }

    [Fact]
    public void A_snapshot_max_age_shorter_than_two_dispatch_cycles_is_refused()
    {
        // Why: the module's offer is recomputed once per dispatch cycle; with a max age under two cycles it would be stale at nearly every guard step, the plan
        // would flap to v2 for the car and nothing would say why. The shipped cycle is 30 s: 60 s passes, 59 s does not.
        var tooShort = Parse(ev: string.Empty, control: "  ev:\n    snapshot_max_age: 59s\n");
        var enough = Parse(ev: string.Empty, control: "  ev:\n    snapshot_max_age: 60s\n");

        Assert.Equal(ConfigurationStatus.Invalid, tooShort.Status);
        Assert.Contains(tooShort.Errors, e => e.Path == "control.ev.snapshot_max_age");
        Assert.DoesNotContain(enough.Errors, e => e.Path == "control.ev.snapshot_max_age");
    }

    [Fact]
    public void A_soc_max_age_beyond_the_signal_limit_is_refused_like_a_signals_max_age()
    {
        var limit = TestConfigurations.Valid(Document()).Configuration!.Control.Signals.MaxAgeLimit;
        var result = Parse(Ev.Replace("max_age: 30m", $"max_age: {(int)limit.TotalSeconds + 1}s", StringComparison.Ordinal));

        Assert.Contains(result.Errors, e => e.Code == ConfigurationIssueCodes.OutOfRange && e.Path == "ev.soc.max_age");
    }

    [Fact]
    public void The_shipped_ev_parameters_are_the_starting_values_of_the_plan()
    {
        // Why: these are the plan's starting values (docs/EV.md section 2, several with no origin, reviewed after a shadow week); a silent
        // change of one would change a plan, so the defaults file is pinned to the document that explains them. kWh/km is 0.20: the
        // maintainer's full charge of 18 kWh is about 90 km.
        var ev = TestConfigurations.Valid(Document(ev: string.Empty)).Configuration!.Control.Ev;

        Assert.Equal((25.0, 25.0, 15.0, 90.0), (ev.FloorPercent, ev.FloorAfterPercent, ev.ReservePoints, ev.CapPercent));
        Assert.Equal([DayOfWeek.Saturday, DayOfWeek.Sunday], ev.WeeklyFull.Days);
        Assert.Equal(100.0, ev.WeeklyFull.TargetPercent);
        Assert.Equal((TimeSpan.FromMinutes(30), 0.10, 0.20), (ev.ReadyMargin, ev.LossFraction, ev.FallbackKwhPerKm));
        Assert.Equal((0.12, 0.60, 20.0), (ev.Learn.MinKwhPerKm, ev.Learn.MaxKwhPerKm, ev.Learn.MinSoc));
        Assert.Equal((0.2, 50), (ev.Learn.Smoothing, ev.Learn.MaxSamples));
        Assert.Equal((500.0, TimeSpan.FromHours(6), 1.5, 3.0), (ev.MaxTripKm, ev.SocMaxAge, ev.DriftLimitKwh, ev.TargetChangeThresholdPoints));
        Assert.Equal((90.0, TimeSpan.FromHours(12)), (ev.ChargeNow.TargetPercent, ev.ChargeNow.Duration));
        Assert.True(ev.Spread);
    }

    [Fact]
    public void Control_ev_overrides_a_parameter_key_by_key()
    {
        var ev = TestConfigurations.Valid(Document(ev: string.Empty, control: "  ev:\n    cap: 80\n    weekly_full:\n      days: [friday]\n    learn:\n      min_soc: 30\n    spread: false\n")).Configuration!.Control.Ev;

        Assert.Equal((80.0, 25.0), (ev.CapPercent, ev.FloorPercent));
        Assert.Equal([DayOfWeek.Friday], ev.WeeklyFull.Days);
        Assert.Equal(100.0, ev.WeeklyFull.TargetPercent);
        Assert.Equal((30.0, 0.12), (ev.Learn.MinSoc, ev.Learn.MinKwhPerKm));
        Assert.False(ev.Spread);
    }

    [Fact]
    public void An_empty_weekly_day_list_switches_the_weekly_full_charge_off_and_a_repeated_day_counts_once()
    {
        var off = TestConfigurations.Valid(Document(ev: string.Empty, control: "  ev:\n    weekly_full:\n      days: []\n")).Configuration!.Control.Ev;
        var twice = TestConfigurations.Valid(Document(ev: string.Empty, control: "  ev:\n    weekly_full:\n      days: [sunday, sunday]\n")).Configuration!.Control.Ev;

        Assert.Empty(off.WeeklyFull.Days);
        Assert.Equal([DayOfWeek.Sunday], twice.WeeklyFull.Days);
    }

    [Theory]
    [InlineData("cap: 10", "control.ev.floor")]
    [InlineData("weekly_full:\n      target: 80", "control.ev.weekly_full.target")]
    [InlineData("kwh_per_km: 0.9", "control.ev.kwh_per_km")]
    [InlineData("kwh_per_km: 0.05", "control.ev.kwh_per_km")]
    [InlineData("learn:\n      min_kwh_per_km: 0.7", "control.ev.learn.min_kwh_per_km")]
    [InlineData("charge_now:\n      duration: 240h", "control.ev.charge_now.duration")]
    public void Keys_that_contradict_each_other_are_an_error_when_the_ev_section_exists_and_only_then(string line, string path)
    {
        // Why: a floor above the cap or a fallback the learner rejects makes the module nonsensical, but a file with no ev: section never
        // uses control.ev, so it must load exactly as before the module existed (no document may become invalid because of an unused block).
        var control = $"  ev:\n    {line}\n";

        var with = Parse(control: control);
        var without = Parse(ev: string.Empty, control: control);

        Assert.Equal(ConfigurationStatus.Invalid, with.Status);
        var error = Assert.Single(with.Errors, e => e.Path == path);
        Assert.NotNull(error.Line);
        Assert.Equal(ConfigurationStatus.Valid, without.Status);
        Assert.Empty(without.Errors);
    }

    [Theory]
    [InlineData("floor: 120", "control.ev.floor")]
    [InlineData("floor: -1", "control.ev.floor")]
    [InlineData("floor_after: 101", "control.ev.floor_after")]
    [InlineData("reserve: -5", "control.ev.reserve")]
    [InlineData("cap: 101", "control.ev.cap")]
    [InlineData("weekly_full:\n      target: 120", "control.ev.weekly_full.target")]
    [InlineData("weekly_full:\n      days: [funday]", "control.ev.weekly_full.days")]
    [InlineData("weekly_full:\n      days: sunday", "control.ev.weekly_full.days")]
    [InlineData("ready_margin: soon", "control.ev.ready_margin")]
    [InlineData("loss: 1", "control.ev.loss")]
    [InlineData("loss: -0.1", "control.ev.loss")]
    [InlineData("kwh_per_km: 0", "control.ev.kwh_per_km")]
    [InlineData("learn:\n      smoothing: 0", "control.ev.learn.smoothing")]
    [InlineData("learn:\n      smoothing: 1.5", "control.ev.learn.smoothing")]
    [InlineData("learn:\n      min_soc: 101", "control.ev.learn.min_soc")]
    [InlineData("learn:\n      max_samples: 0", "control.ev.learn.max_samples")]
    [InlineData("learn:\n      max_samples: 100000", "control.ev.learn.max_samples")]
    [InlineData("learn:\n      max_samples: 201", "control.ev.learn.max_samples")]
    [InlineData("learn:\n      max_kwh_per_km: 10.5", "control.ev.learn.max_kwh_per_km")]
    [InlineData("learn:\n      pair_tolerance: -5s", "control.ev.learn.pair_tolerance")]
    [InlineData("learn:\n      pair_tolerance: soon", "control.ev.learn.pair_tolerance")]
    [InlineData("max_trip_km: 0", "control.ev.max_trip_km")]
    [InlineData("max_trip_km: 100000", "control.ev.max_trip_km")]
    [InlineData("soc_max_age: 0s", "control.ev.soc_max_age")]
    [InlineData("drift_limit_kwh: 0", "control.ev.drift_limit_kwh")]
    [InlineData("target_change_threshold: 0", "control.ev.target_change_threshold")]
    [InlineData("charge_now:\n      target: 0", "control.ev.charge_now.target")]
    [InlineData("charge_now:\n      duration: 0s", "control.ev.charge_now.duration")]
    [InlineData("spread: maybe", "control.ev.spread")]
    [InlineData("colour: red", "control.ev.colour")]
    public void A_parameter_out_of_range_is_an_error_naming_the_key(string line, string path)
    {
        // Why: an override must not be able to produce a nonsensical module (a floor above the cap, a fallback the learner itself rejects).
        var result = Parse(ev: string.Empty, control: $"  ev:\n    {line}\n");

        Assert.Equal(ConfigurationStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Path == path || e.Path.StartsWith(path + ".", StringComparison.Ordinal));
    }
}
