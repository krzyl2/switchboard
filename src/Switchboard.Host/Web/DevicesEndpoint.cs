using Microsoft.AspNetCore.Mvc;
using Switchboard.Application.Configuration;
using Switchboard.Application.Signals;
using Switchboard.Domain.Signals;

namespace Switchboard.Host.Web;

/// <summary>
/// Shape of GET api/devices (UI-12): every declared device as the configuration declares it, for the
/// device card: the actuator and state entities (from the configuration layer, as api/signals shows
/// entities), phases, limits, reaction time, lockouts, how its state reports, its draw signals and
/// its value per kWh. Read-only: editing the configuration is not part of this version.
/// </summary>
/// <param name="Configuration">valid, invalid or missing; devices are listed only from a valid configuration.</param>
public sealed record DevicesResponse(DateTimeOffset At, string Configuration, IReadOnlyList<DeviceConfigDto> Devices);

/// <param name="Name">The optional display name from <c>name:</c>; null when none is declared.</param>
/// <param name="StateReports">age (judged by state_max_age) or on_change (by availability, no age limit).</param>
/// <param name="StateMaxAgeS">The age limit of the state signals; null for on_change.</param>
public sealed record DeviceConfigDto(
    string Id,
    string? Name,
    string Type,
    IReadOnlyList<string> Phases,
    IReadOnlyList<StageConfigDto> Stages,
    string? StopActuator,
    RangeConfigDto? Range,
    double ReactionTimeS,
    double MinOnS,
    double MinOffS,
    string StateReports,
    double? StateMaxAgeS,
    DrawConfigDto? Draw,
    double ValuePerKwh,
    double? ShedPriority,
    bool NoShed,
    string? RotationGroup,
    string? CircuitGroup,
    string Start);

/// <param name="Actuator">The only entity that may ever be commanded for this stage (SAFE-05), and only while control is active.</param>
public sealed record StageConfigDto(int Stage, string Actuator, string StateEntity, IReadOnlyList<string> Phases, double RatedCurrentA, double? RatedPowerW);

/// <param name="Unit">The unit the actuator is written in (A or mA); the bounds are in A.</param>
public sealed record RangeConfigDto(double MinA, double MaxA, double StepA, string Unit);

public sealed record DrawConfigDto(IReadOnlyList<DrawEntityDto> Current, IReadOnlyList<DrawEntityDto> Power, double MaxAgeS);

public sealed record DrawEntityDto(string Phase, string Entity);

internal static class DevicesEndpoint
{
    public static IEndpointRouteBuilder MapDevicesApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/devices", ([FromServices] ISignalDiagnosticsSource diagnostics, [FromServices] SignalClock clock) =>
            TypedResults.Json(Build(diagnostics.Current, clock.Time.GetUtcNow())));
        return endpoints;
    }

    internal static DevicesResponse Build(SignalDiagnostics diagnostics, DateTimeOffset now)
    {
        var result = diagnostics.Configuration;
        var devices = result?.Configuration?.Devices ?? [];
        return new DevicesResponse(
            now,
            result is null ? "missing" : ApiText.Snake(result.Status),
            [.. devices.Select(d => Device(d, diagnostics))]);
    }

    private static DeviceConfigDto Device(DeviceDefinition device, SignalDiagnostics diagnostics) => new(
        device.Id.Value,
        device.Name,
        ApiText.Snake(device.Type),
        [.. device.Stages.SelectMany(s => s.Phases).Distinct().Order().Select(p => p.ToString())],
        [.. device.Stages.Select(s => new StageConfigDto(s.Index, s.Actuator.Value, s.StateEntity.Value, [.. s.Phases.Select(p => p.ToString())], s.RatedCurrent.Value, s.RatedPower?.Value))],
        device.StopActuator?.Value,
        device.Range is { } range ? new RangeConfigDto(range.Min.Value, range.Max.Value, range.Step.Value, device.RangeUnit ?? "A") : null,
        device.ReactionTime.TotalSeconds,
        device.MinOn.TotalSeconds,
        device.MinOff.TotalSeconds,
        ApiText.Snake(device.StateReports),
        device.StateReports == StateFreshness.OnChange ? null : device.StateMaxAge.TotalSeconds,
        device.Draw is { } draw
            ? new DrawConfigDto(Entities(draw.Current, diagnostics), Entities(draw.Power, diagnostics), draw.MaxAge.TotalSeconds)
            : null,
        device.ValuePerKwh,
        device.ShedPriority,
        device.NoShed,
        device.RotationGroup?.Value,
        device.CircuitGroup?.Value,
        ApiText.Snake(device.Start));

    private static List<DrawEntityDto> Entities(IReadOnlyDictionary<Phase, SignalName> signals, SignalDiagnostics diagnostics) =>
        [.. signals.OrderBy(s => s.Key)
            .Where(s => diagnostics.Entities.ContainsKey(s.Value))
            .Select(s => new DrawEntityDto(s.Key.ToString(), diagnostics.Entities[s.Value].Value))];
}
