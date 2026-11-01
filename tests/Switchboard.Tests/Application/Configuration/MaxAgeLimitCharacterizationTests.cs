using Switchboard.Application.Configuration;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Configuration;

/// <summary>
/// The max_age rule had six copies (a signal, a sub-meter, a device state, a device draw, an EV block, a goal input). Why it matters: a frozen sensor counts
/// as fresh for its whole max_age, so an age above control.signals.max_age_limit must be refused at the key that wrote it, with the same words wherever it is
/// written; and an age exactly at the limit is accepted everywhere. Recorded before the copies became one.
/// </summary>
public sealed class MaxAgeLimitCharacterizationTests
{
    private const string Heater = "  - id: heater\n    type: onoff\n    actuator: switch.heater_1\n    phase: L1\n    rated_current_a: 6.6\n    rated_power_w: 1500\n    value_per_kwh: 0.35\n";

    private const string Charger = "  - id: ev_charger\n    type: modulating\n    actuator: number.charger_current\n    phase: L1\n    min: 6\n    max: 16\n    step: 1\n    value_per_kwh: 2.03\n    sources: pv_and_zone:offpeak\n";

    private const string Group = "circuit_groups:\n  - { id: carport, breaker_rating_a: 16, characteristic: B, phases: [L1, L2], current: { L1: sensor.c1, L2: sensor.c2 }, current_max_age: ";

    private const string Tariff = """
        economics:
          currency: PLN
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

    private static string EvDocument(string age) =>
        TestDevices.Document(Charger, extra: "planner:\n  dispatch: narrow\n\nev:\n  device: ev_charger\n  capacity_kwh: 18\n  soc: { entity: sensor.car_battery_level }\n  range: { entity: sensor.car_range, max_age: " + age + " }\n");

    private static string GoalDocument(string age) =>
        TestDevices.Document(
            Charger + "    goal:\n      kind: soc\n      hard: true\n      capacity_kwh: 18\n      loss_fraction: 0.10\n      soc: { entity: sensor.car_battery_level, max_age: " + age + " }\n      target: { value: 90 }\n      deadline: { entity: input_datetime.car_departure, margin: 30m }\n      sources: pv_and_zone:offpeak\n",
            extra: Tariff);

    public static TheoryData<string> Sites => new() { "signal", "group", "state", "draw", "ev", "goal" };

    private static string Document(string site, string age) => site switch
    {
        "signal" => TestConfigurations.ThreePhase(currentMaxAge: age),
        "group" => TestDevices.Document(Heater, extra: Group + age + " }\n"),
        "state" => TestDevices.Document(Heater + "    state_max_age: " + age + "\n"),
        "draw" => TestDevices.Document(Heater + "    draw: { current: sensor.heater_draw_current, power: sensor.heater_draw_power, max_age: " + age + " }\n"),
        "ev" => EvDocument(age),
        "goal" => GoalDocument(age),
        _ => throw new ArgumentOutOfRangeException(nameof(site)),
    };

    [Theory]
    [MemberData(nameof(Sites))]
    public void An_age_at_the_limit_is_accepted(string site)
    {
        var result = TestConfigurations.Parse(Document(site, "24h"));

        Assert.DoesNotContain(result.Errors, e => e.Code == ConfigurationIssueCodes.OutOfRange);
    }

    [Theory]
    [MemberData(nameof(Sites))]
    public void An_age_above_the_limit_is_refused_where_it_was_written_in_the_words_of_its_key(string site)
    {
        var result = TestConfigurations.Parse(Document(site, "30h"));

        var errors = result.Errors.Where(e => e.Code == ConfigurationIssueCodes.OutOfRange).Select(e => (e.Path, e.Message)).ToArray();
        Assert.NotEmpty(errors);
        Assert.All(errors, e => Assert.Equal(Expected[site].Message, e.Message));
        Assert.Contains(errors, e => e.Path == Expected[site].Path);
    }

    private static readonly Dictionary<string, (string Path, string Message)> Expected = new()
    {
        ["signal"] = ("signals.grid_current_l1.max_age", "max_age may be at most 86400 s (control.signals.max_age_limit): a frozen sensor counts as fresh for its whole max_age."),
        ["group"] = ("circuit_groups[0].current_max_age", "current_max_age may not exceed control.signals.max_age_limit."),
        ["state"] = ("devices[0].state_max_age", "state_max_age may be at most 86400 s (control.signals.max_age_limit): a frozen state counts as fresh for its whole max_age."),
        ["draw"] = ("devices[0].draw.max_age", "max_age may not exceed control.signals.max_age_limit: a frozen draw counts as fresh for its whole max_age."),
        ["ev"] = ("ev.range.max_age", "max_age may not exceed control.signals.max_age_limit: a frozen value counts as fresh for its whole max_age."),
        ["goal"] = ("devices[0].goal.soc.max_age", "max_age may not exceed control.signals.max_age_limit: a frozen value counts as fresh for its whole max_age."),
    };
}
