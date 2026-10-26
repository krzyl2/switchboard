using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Switchboard.Application.Configuration;
using Switchboard.Application.Setup;
using Switchboard.Application.Signals;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Signals;
using Switchboard.Host.Composition;
using Switchboard.Infrastructure.Runtime;

namespace Switchboard.Host.Web;

/// <summary>Shape of GET api/signals: the diagnostics view of every signal (SIG-05, SIG-07). Enum values are snake_case.</summary>
public sealed record SignalsResponse(
    DateTimeOffset GeneratedAt,
    DateTimeOffset SnapshotAt,
    long Sequence,
    SignalSourceDto Source,
    ConfigurationDto Configuration,
    ConnectionDto? Connection,
    IReadOnlyList<PhaseDto> Phases,
    IReadOnlyList<SignalDto> Signals,
    RejectionsDto Rejections,
    IReadOnlyList<ConfigurationWarningDto> ConfigurationWarnings);

/// <summary>A warning of the derived-sensor check of the configuration in force (never a safe state, never a refusal).</summary>
/// <param name="Code">derived_measurement_input, measurement_not_in_registry or derived_check_unavailable.</param>
public sealed record ConfigurationWarningDto(string Code, string? Signal, string? Entity, string? Platform, DateTimeOffset CheckedAt);

public sealed record SignalSourceDto(string Kind, string Status);

public sealed record ConfigurationDto(string State, string Path, DateTimeOffset? LoadedAt, IReadOnlyList<IssueDto> Errors, IReadOnlyList<IssueDto> Warnings);

public sealed record IssueDto(string Code, string Path, int? Line, int? Column, string Message);

public sealed record ConnectionDto(string Topology, double BreakerRatingA, string Characteristic, double VoltageV);

public sealed record PhaseDto(
    string Phase,
    bool ProtectionDataValid,
    string? InvalidReason,
    double? CurrentA,
    string? Direction,
    double? NetImportW,
    double? GenerationW,
    double? LoadW,
    double? ResidualW,
    DateTimeOffset? ResidualAt,
    long BalanceRejections,
    ReserveDto? Reserve);

public sealed record ReserveDto(
    double ConfiguredW,
    double MinimumSafeW,
    bool BelowMinimum,
    double RefreshS,
    bool RefreshDetected,
    double BlindTimeS,
    double TierMultiple);

/// <param name="Value">A number in <paramref name="Unit"/>, or text for a state signal.</param>
/// <param name="DependentDevices">Devices protection treats as unsafe while this signal is not fresh (SIG-05).</param>
public sealed record SignalDto(
    string? Name,
    string? Entity,
    string Kind,
    string Role,
    string? Phase,
    string State,
    string Detail,
    object? Value,
    string? Unit,
    RawDto? Raw,
    DateTimeOffset? LastReported,
    double? AgeS,
    double? MaxAgeS,
    double? RefreshS,
    int RefreshIntervals,
    DirectionDto? Direction,
    long RejectedCount,
    IReadOnlyList<string> DependentDevices);

public sealed record RawDto(string State, string? Unit);

public sealed record DirectionDto(string Source, string Direction, string? From);

public sealed record RejectionsDto(long Total, long BalanceResidual, long InvalidSample);

internal static class SignalsEndpoint
{
    public static IEndpointRouteBuilder MapSignalsApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/signals", (
                [FromServices] ISignalDiagnosticsSource diagnostics,
                [FromServices] SignalClock clock,
                [FromServices] SignalSourceSelection selection,
                [FromServices] RuntimeEnvironment environment,
                [FromServices] ConfigurationWarningsHolder warnings) =>
            TypedResults.Json(Build(diagnostics.Current, clock.Time.GetUtcNow(), selection, environment.ConfigPath, warnings.Current)));
        return endpoints;
    }

    /// <summary>Ages are computed at request time from each signal's last report; states come from the snapshot.</summary>
    internal static SignalsResponse Build(SignalDiagnostics diagnostics, DateTimeOffset now, SignalSourceSelection selection, string configPath, IReadOnlyList<ConfigurationWarning>? warnings = null)
    {
        var snapshot = diagnostics.Snapshot;
        var configuration = diagnostics.Configuration;
        return new SignalsResponse(
            now,
            snapshot.TakenAt,
            snapshot.Sequence,
            new SignalSourceDto(selection.IsReplay ? "replay" : "home_assistant", Snake(snapshot.Source)),
            new ConfigurationDto(
                configuration is null ? "missing" : Snake(configuration.Status),
                configuration?.Path ?? configPath,
                configuration?.LoadedAt,
                [.. (configuration?.Errors ?? []).Select(Issue)],
                [.. (configuration?.Warnings ?? []).Concat(diagnostics.RuntimeWarnings).Select(Issue)]),
            snapshot.Connection is { } connection
                ? new ConnectionDto(Snake(connection.Topology), connection.BreakerRating.Value, Characteristic(connection.Characteristic), connection.Voltage.Value)
                : null,
            [.. snapshot.Protection.Phases.Select(p => Phase(snapshot, p))],
            [.. snapshot.Signals.Select(s => Signal(s, diagnostics, now))],
            new RejectionsDto(snapshot.Rejections.Total, snapshot.Rejections.BalanceResidual, snapshot.Rejections.InvalidSample),
            [.. (warnings ?? []).Select(w => new ConfigurationWarningDto(w.Code, w.Signal?.Value, w.Entity?.Value, w.Platform, w.CheckedAt))]);
    }

    private static PhaseDto Phase(SignalSnapshot snapshot, PhaseProtection protection)
    {
        var dispatch = snapshot.Dispatch.For(protection.Phase);
        var balance = snapshot.Balance.FirstOrDefault(b => b.Phase == protection.Phase);
        var reserve = snapshot.Reserve.FirstOrDefault(r => r.Phase == protection.Phase);
        return new PhaseDto(
            protection.Phase.ToString(),
            protection.DataValid,
            protection.InvalidReason is { } reason ? Snake(reason) : null,
            protection.Current?.Signed.Value,
            protection.Current is { } current ? Snake(current.Direction) : null,
            dispatch?.NetImport?.Value,
            dispatch?.Generation?.Value,
            dispatch?.Load?.Value,
            balance?.Residual?.Value,
            balance?.LastEvaluated,
            balance?.Rejections ?? 0,
            reserve is null
                ? null
                : new ReserveDto(
                    reserve.Configured.Value,
                    reserve.Minimum.Value,
                    reserve.BelowMinimum,
                    reserve.RefreshUsed.TotalSeconds,
                    reserve.RefreshDetected,
                    reserve.BlindTime.TotalSeconds,
                    reserve.TierMultiple));
    }

    private static SignalDto Signal(SignalReading reading, SignalDiagnostics diagnostics, DateTimeOffset now) =>
        new(
            reading.Name?.Value,
            reading.Name is { } name && diagnostics.Entities.TryGetValue(name, out var entity) ? entity.Value : null,
            Snake(reading.Kind),
            Snake(reading.Role),
            reading.Phase?.ToString(),
            Snake(reading.State),
            Snake(reading.Detail),
            reading.Value switch
            {
                null => null,
                StateValue state => state.State,
                var value => SignalNormalizer.Magnitude(value),
            },
            reading.Value is null or StateValue ? null : reading.Kind == SignalKind.Price ? PriceUnit(diagnostics) : InternalUnit(reading.Kind),
            reading.RawState is { } raw ? new RawDto(raw, reading.RawUnit) : null,
            reading.LastReported,
            reading.LastReported is { } reported ? Math.Round((now - reported).TotalSeconds, 3) : null,
            reading.MaxAge?.TotalSeconds,
            reading.DetectedRefresh?.TotalSeconds,
            reading.RefreshIntervals,
            reading.Direction is { } direction
                ? new DirectionDto(Snake(direction.Source), Snake(direction.Direction), direction.From?.Value)
                : null,
            reading.RejectedCount,
            reading.Dependents.IsDefault ? [] : [.. reading.Dependents.Select(d => d.Value)]);

    private static IssueDto Issue(ConfigurationIssue issue) => new(issue.Code, issue.Path, issue.Line, issue.Column, issue.Message);

    /// <summary>A price travels in currency per kWh, the currency being the label of <c>economics.currency</c> (the add-on writes no symbol of its own).</summary>
    private static string? PriceUnit(SignalDiagnostics diagnostics) =>
        diagnostics.Configuration?.Configuration?.Economics?.Currency is { } currency ? $"{currency}/kWh" : null;

    private static string? InternalUnit(SignalKind kind) => kind switch
    {
        SignalKind.Current => "A",
        SignalKind.Power => "W",
        SignalKind.Voltage => "V",
        SignalKind.EnergyCounter => "kWh",
        SignalKind.Percentage => "%",
        _ => null,
    };

    // The configuration spelling: B, C, unknown.
    private static string Characteristic(BreakerCharacteristic characteristic) =>
        characteristic == BreakerCharacteristic.Unknown ? "unknown" : characteristic.ToString();

    private static string Snake<TEnum>(TEnum value)
        where TEnum : struct, Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
