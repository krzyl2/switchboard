using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Switchboard.Application.Configuration;
using Switchboard.Host.Web;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Host;

/// <summary>
/// GET api/ev (EV-01, slice S1): read-only, and it shows configuration only. It must say plainly that there is no EV section (a file from
/// before the module), echo the section and the parameters in force, carry the issues of <c>ev</c> and <c>control.ev</c> and nothing else,
/// and show no assessment or trip: there is no module yet, and the answer must not look as if there were.
/// </summary>
public sealed class EvEndpointTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private const string Charger =
        "  - id: ev_charger\n    type: modulating\n    actuator: number.charger_current\n    phase: L1\n    min: 6\n    max: 16\n    step: 1\n    value_per_kwh: 2.03\n";

    private const string Ev = "ev:\n  device: ev_charger\n  capacity_kwh: 18\n  soc: { entity: sensor.car_battery_level, max_age: 30m }\n";

    private static ConfigurationResult Config(string extra) => TestConfigurations.Parse(TestDevices.Document(Charger, extra: extra));

    [Fact]
    public void A_file_without_an_ev_section_is_not_enabled_but_still_carries_the_parameters()
    {
        // Why: the UI says "no EV module" instead of failing; the parameters in force are shown either way, they are control parameters.
        var response = EvEndpoint.Build(Config(string.Empty), Now);

        Assert.False(response.Enabled);
        Assert.Null(response.Config);
        Assert.Empty(response.Issues);
        Assert.Equal((25.0, 90.0, 0.20), (response.Parameters!.FloorPercent, response.Parameters.CapPercent, response.Parameters.FallbackKwhPerKm));
    }

    [Fact]
    public void A_section_is_echoed_with_its_signal_name_and_never_an_entity_id()
    {
        var response = EvEndpoint.Build(Config("planner:\n  dispatch: narrow\n" + Ev), Now);

        Assert.True(response.Enabled);
        Assert.Equal(("ev_charger", 18.0, "narrow"), (response.Config!.Device, response.Config.CapacityKwh, response.Config.PlannerDispatch));
        Assert.DoesNotContain("sensor.", JsonSerializer.Serialize(response), StringComparison.Ordinal);
        Assert.StartsWith("device_", response.Config.SocSignal, StringComparison.Ordinal);
    }

    [Fact]
    public void Durations_are_seconds_and_the_weekly_days_are_lower_case_names()
    {
        var parameters = EvEndpoint.Build(Config(string.Empty), Now).Parameters!;

        Assert.Equal([ "saturday", "sunday" ], parameters.WeeklyFullDays);
        Assert.Equal((1800.0, 21600.0, 43200.0), (parameters.ReadyMarginS, parameters.SocMaxAgeS, parameters.ChargeNowDurationS));
        Assert.Equal(60.0, parameters.LearnPairToleranceS);
    }

    [Fact]
    public void The_issues_of_the_section_are_listed_and_those_of_other_sections_are_not()
    {
        // Why: the section's own warnings (here: no planner, so a preview only) belong on its page; an unrelated warning does not.
        var response = EvEndpoint.Build(Config(Ev), Now);

        Assert.Contains(response.Issues, i => i.Code == ConfigurationIssueCodes.EvWithoutPlannerDispatch);
        Assert.All(response.Issues, i => Assert.Matches("^(ev|control\\.ev)([.\\[]|$)", i.Path));
    }

    [Fact]
    public void With_a_planner_whose_dispatch_is_off_the_preview_warning_is_on_the_ev_answer()
    {
        // Why: this is the main first-week scenario (planner present, dispatch off). The warning is about the EV section, so it must be listed
        // under api/ev, not only under the planner path that api/ev does not show.
        var response = EvEndpoint.Build(Config("planner:\n  dispatch: off\n" + Ev), Now);

        Assert.True(response.Enabled);
        Assert.Equal("off", response.Config!.PlannerDispatch);
        var warning = Assert.Single(response.Issues, i => i.Code == ConfigurationIssueCodes.EvWithoutPlannerDispatch);
        Assert.Equal("ev", warning.Path);
        Assert.NotNull(warning.Line);
    }

    [Fact]
    public void With_dispatch_narrow_there_is_no_preview_warning()
    {
        var response = EvEndpoint.Build(Config("planner:\n  dispatch: narrow\n" + Ev), Now);

        Assert.DoesNotContain(response.Issues, i => i.Code == ConfigurationIssueCodes.EvWithoutPlannerDispatch);
    }

    [Fact]
    public void An_invalid_section_is_not_enabled_and_its_error_is_listed_with_the_position()
    {
        var response = EvEndpoint.Build(Config(Ev.Replace("device: ev_charger", "device: nobody", StringComparison.Ordinal)), Now);

        Assert.False(response.Enabled);
        Assert.Null(response.Parameters);
        var issue = Assert.Single(response.Issues, i => i.Code == ConfigurationIssueCodes.UnknownDevice);
        Assert.Equal("ev.device", issue.Path);
    }

    [Fact]
    public void Without_a_configuration_the_answer_is_empty_not_an_error()
    {
        var response = EvEndpoint.Build(null, Now);

        Assert.False(response.Enabled);
        Assert.Null(response.Parameters);
        Assert.Empty(response.Issues);
    }

    [Fact]
    public async Task The_route_answers_a_get_and_has_no_write_route()
    {
        // Why: S1 is read-only; the mode and charge-now writes come with S6 and need their own guard.
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);
        _ = factory.Server;

        var ok = await PresentationEndpointTests.SendAsync(factory, "GET", "/api/ev");
        var post = await PresentationEndpointTests.SendAsync(factory, "POST", "/api/ev");

        Assert.Equal(StatusCodes.Status200OK, ok.Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, post.Response.StatusCode);
        using var json = JsonDocument.Parse(ok.Response.Body);
        Assert.False(json.RootElement.GetProperty("enabled").GetBoolean());
    }
}
