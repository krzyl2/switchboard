using Switchboard.Application.Configuration;
using Switchboard.Application.Ev;
using Switchboard.Application.Reports;
using Switchboard.Application.Signals;
using Switchboard.Domain.Ev;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Reports;

/// <summary>
/// What GET api/ev decides before it is worded: a charge-now that has run out must not be shown as running (the user would wait for a charge that is not
/// coming), the calendar's zone is only judged when the add-on's zone is known, and a missing <c>ev:</c> section offers neither trips, assessment nor controls.
/// </summary>
public sealed class EvReportTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 6, 0, 0, TimeSpan.FromHours(1));

    private const string Charger =
        "  - id: ev_charger\n    type: modulating\n    actuator: number.charger_current\n    phase: L1\n    min: 6\n    max: 16\n    step: 1\n    value_per_kwh: 2.03\n";

    private const string Ev =
        "planner:\n  dispatch: narrow\n\nev:\n  device: ev_charger\n  capacity_kwh: 18\n  soc: { entity: sensor.car_battery_level }\n  calendar: { entity: calendar.kursy }\n";

    private static ConfigurationResult Config(string extra = Ev) => TestConfigurations.Parse(TestDevices.Document(Charger, extra: extra));

    private static EvInputs Inputs(string? zoneName) =>
        EvInputs.Empty with { CalendarZoneName = zoneName };

    [Fact]
    public void A_charge_now_is_running_only_until_it_expires()
    {
        var manual = new EvManual(EvMode.Auto, new EvChargeNow(Now.AddHours(-1), 80, Now.AddMinutes(10)));

        Assert.NotNull(EvReport.Compose(Config(), Now, manual: manual).Manual!.Running);
        Assert.Null(EvReport.Compose(Config(), Now.AddMinutes(10), manual: manual).Manual!.Running);
    }

    [Fact]
    public void Without_an_ev_section_nothing_of_the_module_is_reported()
    {
        var manual = new EvManual(EvMode.Auto, null);
        var report = EvReport.Compose(Config(string.Empty), Now, Inputs(null), Warsaw, new EvPublished(null, true), manual);

        Assert.Null(report.Trips);
        Assert.Null(report.Inputs);
        Assert.Null(report.Assessment);
        Assert.Null(report.Manual);
    }

    [Fact]
    public void A_failed_computation_is_an_expired_assessment_without_a_snapshot()
    {
        var report = EvReport.Compose(Config(), Now, published: new EvPublished(null, true));

        Assert.Equal((true, true), (report.Assessment!.Failed, report.Assessment.Expired));
        Assert.Null(report.Assessment.Snapshot);
    }

    [Fact]
    public void A_calendar_zone_that_differs_is_an_issue_only_when_the_add_on_zone_is_known()
    {
        var stated = Inputs("Asia/Tokyo");

        Assert.DoesNotContain(EvReport.Compose(Config(), Now, stated).Issues, i => i.Code == ConfigurationIssueCodes.EvCalendarZoneMismatch);
        Assert.Contains(EvReport.Compose(Config(), Now, stated, Warsaw).Issues, i => i.Code == ConfigurationIssueCodes.EvCalendarZoneMismatch);
        Assert.DoesNotContain(EvReport.Compose(Config(), Now, Inputs("Europe/Warsaw"), Warsaw).Issues, i => i.Code == ConfigurationIssueCodes.EvCalendarZoneMismatch);
    }
}
