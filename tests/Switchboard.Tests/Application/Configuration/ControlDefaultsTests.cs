using Switchboard.Application.Configuration;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests.Application.Configuration;

/// <summary>
/// The shipped control defaults are the values the predecessor proved in practice (04 §7) and the
/// breaker tier table of 04 §1. They live in a YAML file, never in code; this pins them to the spec.
/// </summary>
public sealed class ControlDefaultsTests
{
    private static readonly Switchboard.Application.Configuration.ControlParameters Control =
        TestConfigurations.Valid(TestConfigurations.ThreePhase()).Configuration!.Control;

    [Fact]
    public void Shipped_defaults_match_04_section_7()
    {
        var dispatch = Control.Dispatch;
        Assert.Equal(300, dispatch.StartHysteresis.Value);
        Assert.Equal(-200, dispatch.StopHysteresis.Value);
        Assert.Equal(TimeSpan.FromMinutes(6), dispatch.RelayMinOn);
        Assert.Equal(TimeSpan.FromSeconds(90), dispatch.RelayMinOff);
        Assert.Equal(1, dispatch.MaxStepChangeStages);
        Assert.Equal(3, dispatch.PhaseRankingStabilityCycles);
        Assert.Equal(0.25, dispatch.BalanceCredit.Value);
        Assert.Equal((6, 2), (dispatch.PersistenceUpCycles, dispatch.PersistenceDownCycles));
        Assert.Equal(1, dispatch.MaxSimultaneousIncreases);
        Assert.Equal(TimeSpan.FromSeconds(90), Control.Settling.HardTimeout);
        Assert.Equal(TimeSpan.FromSeconds(600), Control.Budget.RecoveryTime);
        Assert.Equal(2500, Control.Connection.DefaultReserve.Value);
    }

    [Fact]
    public void Shipped_hourly_balance_defaults_match_13_hourly_balance()
    {
        var balance = Control.HourlyBalance;
        Assert.Equal((150, 525), (balance.FloorWh, balance.CeilingWh));
        Assert.Equal(TimeSpan.FromMinutes(15), balance.BurnWindow);
        Assert.Equal(TimeSpan.FromMinutes(5), balance.SmoothingWindow);
        Assert.Equal(TimeSpan.FromMinutes(1), balance.MinRemaining);
        Assert.Equal(TimeSpan.FromSeconds(60), balance.MaxAge);
    }

    [Fact]
    public void Hourly_balance_is_overridden_key_by_key_and_is_not_the_signal_layers_balance_check()
    {
        var parsed = TestConfigurations.Parse(TestConfigurations.ThreePhase(control: "  hourly_balance:\n    floor_wh: 100\n    burn_window: 10m\n"));

        Assert.Equal(ConfigurationStatus.Valid, parsed.Status);
        var control = parsed.Configuration!.Control;
        Assert.Equal((100, 525), (control.HourlyBalance.FloorWh, control.HourlyBalance.CeilingWh));
        Assert.Equal(TimeSpan.FromMinutes(10), control.HourlyBalance.BurnWindow);
        Assert.Equal(300, control.Signals.Balance.ResidualThreshold.Value);
    }

    [Theory]
    [InlineData("ceiling_wh: 100")]
    [InlineData("burn_window: 0s")]
    [InlineData("smoothing_window: 0s")]
    [InlineData("min_remaining: 20m")]
    [InlineData("max_age: 0s")]
    [InlineData("bogus: 1")]
    public void A_hourly_balance_override_that_makes_no_band_is_refused(string line)
    {
        var parsed = TestConfigurations.Parse(TestConfigurations.ThreePhase(control: $"  hourly_balance:\n    {line}\n"));

        Assert.NotEqual(ConfigurationStatus.Valid, parsed.Status);
    }

    [Fact]
    public void Shipped_economics_defaults_are_the_starting_values_of_14_economics()
    {
        // Slice 1 binds them and nothing reads them yet; pinning them here keeps a later edit of the YAML a visible decision.
        var economics = Control.Economics;
        Assert.Equal(0, economics.MinMargin);
        Assert.Equal(3, economics.OrderStabilityCycles);
        Assert.Equal(0.05, economics.OrderMinGapFraction);
        Assert.Equal(TimeSpan.FromMinutes(6), economics.ZoneEndMargin);
        Assert.Equal(TimeSpan.FromSeconds(60), economics.NetBillingWindow);
    }

    [Fact]
    public void Economics_parameters_are_overridden_key_by_key_through_control()
    {
        var parsed = TestConfigurations.Parse(TestConfigurations.ThreePhase(control: "  economics:\n    min_margin: 0.05\n    zone_end_margin: 10m\n"));

        Assert.Equal(ConfigurationStatus.Valid, parsed.Status);
        var economics = parsed.Configuration!.Control.Economics;
        Assert.Equal((0.05, TimeSpan.FromMinutes(10)), (economics.MinMargin, economics.ZoneEndMargin));
        Assert.Equal((3, 0.05, TimeSpan.FromSeconds(60)), (economics.OrderStabilityCycles, economics.OrderMinGapFraction, economics.NetBillingWindow));
    }

    [Theory]
    [InlineData("min_margin: -0.01")]
    [InlineData("order_stability_cycles: 0")]
    [InlineData("order_stability_cycles: 1.5")]
    [InlineData("order_min_gap_fraction: 1.5")]
    [InlineData("order_min_gap_fraction: -0.1")]
    [InlineData("zone_end_margin: soon")]
    [InlineData("net_billing_window: 0s")]
    [InlineData("bogus: 1")]
    public void An_economics_override_outside_its_range_is_refused(string line)
    {
        // A margin below zero would buy energy at a loss; zero cycles would reorder on every price tick.
        var parsed = TestConfigurations.Parse(TestConfigurations.ThreePhase(control: $"  economics:\n    {line}\n"));

        Assert.NotEqual(ConfigurationStatus.Valid, parsed.Status);
    }

    [Fact]
    public void Shipped_budget_thresholds_match_04_section_1()
    {
        Assert.Equal((0.5, 0.25, 0.0), (Control.Budget.StopIncreasesBelow, Control.Budget.ShedOneBelow, Control.Budget.TopTierAtOrBelow));
    }

    [Theory]
    [InlineData(BreakerCharacteristic.B)]
    [InlineData(BreakerCharacteristic.C)]
    [InlineData(BreakerCharacteristic.Unknown)]
    public void Shipped_tier_table_matches_04_section_1(BreakerCharacteristic characteristic)
    {
        var bands = Control.Breaker.Tiers[characteristic].Bands;

        Assert.Equal(
            [
                new TierBand(0.90, null, TierReaction.MayAdd),
                new TierBand(1.00, null, TierReaction.Hold),
                new TierBand(1.13, TimeSpan.FromMinutes(15), TierReaction.ReduceOrdinaryCycle),
                new TierBand(1.25, TimeSpan.FromMinutes(3), TierReaction.ReduceNextCycle),
                new TierBand(1.45, TimeSpan.FromSeconds(30), TierReaction.ShedOneImmediately),
                new TierBand(null, TimeSpan.Zero, TierReaction.ShedAllOnPhase),
            ],
            bands);
    }

    [Fact]
    public void Unknown_characteristic_gets_no_credit_for_the_curve()
    {
        // 04 §1: "unknown collapses the continuous threshold to 1.00 × In".
        Assert.Equal(1.13, Control.Breaker.ContinuousMultiple[BreakerCharacteristic.B]);
        Assert.Equal(1.00, Control.Breaker.ContinuousMultiple[BreakerCharacteristic.Unknown]);
    }

    [Fact]
    public void Signal_parameters_have_their_documented_defaults()
    {
        var signals = Control.Signals;
        Assert.Equal(0.1, signals.DefaultSigma[SignalKind.Current]);
        Assert.Equal((20.0, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2)), (signals.Direction.Deadband.Value, signals.Direction.MaxSkew, signals.Direction.PairingWait));
        Assert.Equal((20.0, TimeSpan.FromSeconds(30)), (signals.Direction.PowerFactorDeadband, signals.Direction.ExportHold));
        Assert.Equal((64, 8, 0.25), (signals.Refresh.WindowIntervals, signals.Refresh.MinIntervals, signals.Refresh.Quantile));
        Assert.Equal(0.5, signals.FreshnessProbe.AfterFraction);
        Assert.Equal((300.0, TimeSpan.FromSeconds(5)), (signals.Balance.ResidualThreshold.Value, signals.Balance.MaxSkew));
        Assert.Equal(TimeSpan.FromHours(24), signals.MaxAgeLimit);
        Assert.Equal((2000.0, TimeSpan.FromSeconds(15), 3.0, 0.0, TimeSpan.FromSeconds(30)), (
            Control.ReserveDerivation.ReferenceStep.Value,
            Control.ReserveDerivation.ShedLatency,
            Control.ReserveDerivation.SigmaMultiplier,
            Control.ReserveDerivation.Floor.Value,
            Control.ReserveDerivation.DispatchCycle));

        // The M1 current lags its power by one poll now and then (DECISIONS, Stage 1): one extra
        // refresh of blind time; reduce_ordinary_cycle acts after persistence_down boundaries.
        Assert.Equal(1, signals.CurrentLagPolls);
        Assert.Equal((1, 2), (Control.ReserveDerivation.CurrentLagPolls, Control.ReserveDerivation.PersistenceDownCycles));
    }

    [Fact]
    public void Device_and_decision_log_defaults_ship_in_the_defaults_file()
    {
        var devices = Control.Devices;
        Assert.Equal(TimeSpan.FromMinutes(10), devices.StateMaxAge);
        Assert.Equal(
            (TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20)),
            (devices.ReactionTime[Switchboard.Domain.Devices.DeviceType.OnOff], devices.ReactionTime[Switchboard.Domain.Devices.DeviceType.Stepped], devices.ReactionTime[Switchboard.Domain.Devices.DeviceType.Modulating]));
        Assert.Equal((TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60)), (devices.ModulatingMinOn, devices.ModulatingMinOff));
        Assert.Equal(30, Control.DecisionLog.RetentionDays);
    }
}
