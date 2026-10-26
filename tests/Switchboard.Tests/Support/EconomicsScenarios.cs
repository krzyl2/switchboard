using System.Globalization;
using Switchboard.Application.Balance;
using Switchboard.Application.Decisions;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Signals;
using Switchboard.Domain.Balance;
using Switchboard.Domain.Decisions;
using Switchboard.Domain.Signals;

namespace Switchboard.Tests.Support;

/// <summary>
/// Dispatch scenarios with an <c>economics:</c> section on the virtual clock in the Warsaw zone (14-ECONOMICS, slice 3): a G13-like
/// tariff (the placeholders of the worked example, not a contract), hourly netting or net billing, heaters that may only take surplus
/// and a car that may also take the off-peak zone. Quiet phases (1 A), so the guard's envelope never decides what the allowance does.
/// </summary>
internal static class EconomicsScenarios
{
    public static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    /// <summary>The one-boundary dispatch persistence of the 0.9 balance tests, so a decision shows at the first boundary that wants it.</summary>
    public const string OneBoundary = "  dispatch:\n    persistence_up_cycles: 1\n    persistence_down_cycles: 1\n";

    /// <summary>A local Warsaw wall-clock instant as the UTC instant the virtual clock runs on.</summary>
    public static DateTimeOffset Local(int year, int month, int day, int hour, int minute = 0)
    {
        var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Warsaw.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>A workday (Tuesday 2026-10-27, winter time) at a local time.</summary>
    public static DateTimeOffset Workday(int hour, int minute = 0) => Local(2026, 10, 27, hour, minute);

    /// <summary>The G13-like tariff: morning peak 1.10 (workdays 07-13), afternoon peak 1.60 (winter 16-21), off-peak 0.60 otherwise; export 0.35.</summary>
    public static string Economics(string settlement = "hourly_netting", string? exportPrice = "0.35", string? tariff = null) =>
        "economics:\n  currency: PLN\n  holidays: [2026-11-01, 2026-11-11]\n"
        + (tariff ?? """
            tariff:
              default_zone: offpeak
              zones:
                - id: morning_peak
                  price: 1.10
                  when:
                    - { days: [workday], hours: ["07:00-13:00"] }
                - id: afternoon_peak
                  price: 1.60
                  when:
                    - { days: [workday], months: [apr, may, jun, jul, aug, sep], hours: ["19:00-22:00"] }
                    - { days: [workday], months: [oct, nov, dec, jan, feb, mar], hours: ["16:00-21:00"] }
                - id: offpeak
                  price: 0.60
                  when: []
            """.Indent(2) + "\n")
        + $"  settlement:\n    model: {settlement}\n"
        + (exportPrice is null ? string.Empty : $"    export_price: {exportPrice}\n");

    /// <summary>A relay device on one phase.</summary>
    public static string Device(string id, string phase, double amps, double watts, double value, string? sources, string extra = "") =>
        $"  - id: {id}\n    type: onoff\n    actuator: switch.{id}\n    phase: {phase}\n    rated_current_a: {amps.ToString(CultureInfo.InvariantCulture)}\n    rated_power_w: {watts.ToString(CultureInfo.InvariantCulture)}\n    value_per_kwh: {value.ToString(CultureInfo.InvariantCulture)}\n"
        + (sources is null ? string.Empty : $"    sources: {sources}\n")
        + extra;

    /// <summary>Three 1.5 kW heaters (0.35 per kWh) that may only take surplus, and a 0.9 kW car (2.03 per kWh) that may also take the off-peak zone.</summary>
    public static string HeatersAndCar() =>
        Device("heater_l1", "L1", 6.6, 1500, 0.35, "pv_only")
        + Device("heater_l2", "L2", 6.6, 1500, 0.35, "pv_only")
        + Device("heater_l3", "L3", 6.6, 1500, 0.35, "pv_only")
        + Device("ev_charger", "L2", 4, 920, 2.03, "pv_and_zone:offpeak");

    /// <summary>One heater-like device that is the whole subject of a row.</summary>
    public static string One(string sources, double value) => Device("dev", "L1", 6.6, 1500, value, sources);

    /// <summary>The whole document of a scenario (for a configuration reload).</summary>
    public static string Document(string devices, string? economics = null, string control = OneBoundary, string extra = "") =>
        TestDevices.Document(devices, control, extra: "\n" + (economics ?? Economics()) + extra);

    /// <param name="off">The devices reported off; the rest start on.</param>
    public static GuardScenario Create(string devices, DateTimeOffset at, string[] off, string? economics = null, string control = OneBoundary, string extra = "")
    {
        var s = DispatchScenario.Create(devices, off, control, start: at, extra: "\n" + (economics ?? Economics()) + extra);
        s.Time.Zone = Warsaw;
        return s;
    }

    /// <summary>The balance monitor's reading for the local hour <paramref name="s"/> is in (null: not known).</summary>
    public static Action Feed(GuardScenario s, double? wh, TimeSpan? age = null) => () =>
    {
        var local = TimeZoneInfo.ConvertTime(s.Now, Warsaw);
        var hourStart = new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, 0, 0, local.Offset);
        s.Energy = new EnergyInputs(new EnergyCounters(0, 0, s.Now), TimeSpan.FromSeconds(90));
        s.Balance = new HourlyBalanceStatus(
            new BalanceReading(wh, hourStart, wh is null ? BalanceUnknown.Stale : BalanceUnknown.None),
            s.Now - (age ?? TimeSpan.Zero),
            hourStart.AddHours(1) - s.Now);
    };

    /// <summary>The grid exports this much in total, split over the phases (net import is negative).</summary>
    public static void Export(GuardScenario s, double watts)
    {
        foreach (var phase in new[] { Phase.L1, Phase.L2, Phase.L3 })
        {
            s.SetLoad(phase, -watts / 3);
        }
    }

    /// <summary>Runs <paramref name="minutes"/> with the balance fed and the phases quiet (1 A each).</summary>
    public static void Run(GuardScenario s, Action? feed, double minutes)
    {
        feed?.Invoke();
        s.PollUntil(s.Now + TimeSpan.FromMinutes(minutes), 1, 1, 1, feed);
    }

    public static IEnumerable<Decision> Starts(GuardScenario s, string? device = null) =>
        (device is null ? s.Decisions : s.DecisionsFor(device)).Where(d => d.Action == DecisionAction.SwitchOn);

    public static IEnumerable<Decision> Stops(GuardScenario s, string? device = null) =>
        (device is null ? s.Decisions : s.DecisionsFor(device)).Where(d => d.Action == DecisionAction.SwitchOff);

    /// <summary>The start rank dispatch gave a device in the last step (1 is given energy first).</summary>
    public static int? Rank(GuardScenario s, string device) => s.Dispatches[^1].Devices.FirstOrDefault(d => d.Device.Value == device)?.StartRank;

    /// <summary>What dispatch says about a device in the last step: why an increase is pending, and the economics it was made beside.</summary>
    public static ReasonCode? Pending(GuardScenario s, string device) => s.Dispatches[^1].Devices.Single(d => d.Device.Value == device).Pending;

    public static EconomicsDecisionInputs? Block(GuardScenario s, string device) => s.Dispatches[^1].Wish(Switchboard.Domain.Devices.DeviceName.Parse(device))!.Inputs.Economics;

    /// <summary>Indents every line of a YAML block (a raw literal's own indentation is stripped by the compiler).</summary>
    public static string Indent(this string block, int spaces) =>
        string.Join('\n', block.ReplaceLineEndings("\n").Split('\n').Select(l => l.Length == 0 ? l : new string(' ', spaces) + l));
}
