using System.Collections.Immutable;
using Switchboard.Domain.Ev;

namespace Switchboard.Tests.Domain.Ev;

/// <summary>The control.ev defaults as the tests see them (values of control-defaults.yaml; the code under test reads them from <see cref="EvParameters"/> only).</summary>
internal static class EvTestParameters
{
    public const double CapacityKwh = 18;

    public static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    public static readonly DateTimeOffset T0 = new(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);

    public static EvParameters Defaults { get; } = new(
        FloorPercent: 25,
        FloorBy: new TimeOnly(15, 0),
        TripMarginPoints: 10,
        CapPercent: 90,
        WeeklyFull: new EvWeeklyFullParameters([DayOfWeek.Saturday, DayOfWeek.Sunday], 100),
        ReadyMargin: TimeSpan.FromMinutes(30),
        LossFraction: 0.10,
        FallbackKwhPerKm: 0.20,
        Learn: new EvLearnParameters(Smoothing: 0.2, MinSoc: 20, MinKwhPerKm: 0.12, MaxKwhPerKm: 0.60, MaxSamples: 50, PairTolerance: TimeSpan.FromSeconds(60)),
        MaxTripKm: 500,
        SocMaxAge: TimeSpan.FromHours(6),
        FloorSocMaxAge: TimeSpan.FromHours(12),
        DriftLimitKwh: 1.5,
        TargetChangeThresholdPoints: 3,
        ChargeNow: new EvChargeNowParameters(100, TimeSpan.FromHours(12)),
        Hold: new EvHoldParameters(TimeSpan.FromHours(3)),
        Spread: true,
        SnapshotMaxAge: TimeSpan.FromMinutes(2));
}
