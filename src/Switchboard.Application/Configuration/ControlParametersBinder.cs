using System.Collections.Immutable;
using System.Globalization;
using Switchboard.Application.Signals;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Range = Switchboard.Application.Configuration.ConfigRead.Range;

namespace Switchboard.Application.Configuration;

/// <summary>
/// Binds the merged defaults-plus-overrides tree to <see cref="ControlParameters"/>. Every key is
/// required (the defaults file carries all of them) and every value is range-checked, so an
/// override cannot produce a nonsensical controller.
/// </summary>
internal static class ControlParametersBinder
{
    private static readonly ImmutableDictionary<string, BreakerCharacteristic> Characteristics =
        new Dictionary<string, BreakerCharacteristic>
        {
            ["B"] = BreakerCharacteristic.B,
            ["C"] = BreakerCharacteristic.C,
            ["unknown"] = BreakerCharacteristic.Unknown,
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ImmutableDictionary<string, TierReaction> Reactions =
        new Dictionary<string, TierReaction>
        {
            ["may_add"] = TierReaction.MayAdd,
            ["hold"] = TierReaction.Hold,
            ["reduce_ordinary_cycle"] = TierReaction.ReduceOrdinaryCycle,
            ["reduce_next_cycle"] = TierReaction.ReduceNextCycle,
            ["shed_one_immediately"] = TierReaction.ShedOneImmediately,
            ["shed_all_on_phase"] = TierReaction.ShedAllOnPhase,
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static readonly ImmutableDictionary<string, SignalKind> SigmaKinds =
        new Dictionary<string, SignalKind>
        {
            ["current"] = SignalKind.Current,
            ["power"] = SignalKind.Power,
            ["voltage"] = SignalKind.Voltage,
        }.ToImmutableDictionary(StringComparer.Ordinal);

    public static ControlParameters? Bind(ConfigNode merged, string path, ConfigIssues issues)
    {
        var errorsBefore = issues.Errors.Count;
        var root = new Section(merged, path, issues);

        var connection = root.Child("connection");
        var connectionDefaults = new ConnectionDefaults(new Watts(connection.Number("default_reserve_w", Range.AtLeast(0)) ?? 0));

        var breaker = root.Child("breaker");
        var continuous = BindContinuousMultiples(breaker.Child("continuous_multiple"));
        var tiers = BindTiers(breaker.Child("tiers"), issues);

        var budget = root.Child("budget");
        var budgetParameters = new BudgetParameters(
            budget.PositiveDuration("recovery_time") ?? TimeSpan.Zero,
            budget.Number("stop_increases_below", Range.Fraction) ?? 0,
            budget.Number("shed_one_below", Range.Fraction) ?? 0,
            budget.Number("top_tier_at_or_below", Range.Fraction) ?? 0);
        if (budgetParameters.StopIncreasesBelow < budgetParameters.ShedOneBelow
            || budgetParameters.ShedOneBelow < budgetParameters.TopTierAtOrBelow)
        {
            issues.Error(
                ConfigurationIssueCodes.InvalidValue,
                budget.Path,
                budget.Self,
                "Budget thresholds must satisfy stop_increases_below ≥ shed_one_below ≥ top_tier_at_or_below.");
        }

        var dispatch = root.Child("dispatch");
        var dispatchParameters = new DispatchParameters(
            dispatch.PositiveDuration("cycle") ?? TimeSpan.Zero,
            new Watts(dispatch.Number("start_hysteresis_w") ?? 0),
            new Watts(dispatch.Number("stop_hysteresis_w") ?? 0),
            dispatch.NonNegativeDuration("relay_min_on") ?? TimeSpan.Zero,
            dispatch.NonNegativeDuration("relay_min_off") ?? TimeSpan.Zero,
            dispatch.Integer("max_step_change_stages", 1, int.MaxValue) ?? 0,
            dispatch.Integer("phase_ranking_stability_cycles", 1, int.MaxValue) ?? 0,
            new KilowattHours(dispatch.Number("balance_credit_kwh", Range.AtLeast(0)) ?? 0),
            dispatch.Integer("persistence_up_cycles", 1, int.MaxValue) ?? 0,
            dispatch.Integer("persistence_down_cycles", 1, int.MaxValue) ?? 0,
            dispatch.Integer("max_simultaneous_increases", 1, int.MaxValue) ?? 0);
        if (dispatchParameters.StartHysteresis <= dispatchParameters.StopHysteresis)
        {
            // Equal thresholds let a device oscillate around the boundary (07-PITFALLS, chattering).
            issues.Error(
                ConfigurationIssueCodes.InvalidValue,
                ConfigRead.Join(dispatch.Path, "start_hysteresis_w"),
                dispatch.Self,
                "The start threshold must be strictly higher than the stop threshold.");
        }

        var settling = new SettlingParameters(root.Child("settling").PositiveDuration("hard_timeout") ?? TimeSpan.Zero);

        var signals = root.Child("signals");
        var sigma = signals.Child("default_sigma");
        sigma.CheckKeys([.. SigmaKinds.Keys]);
        var defaultSigma = SigmaKinds
            .Select(kind => (kind.Value, Sigma: sigma.Number(kind.Key, Range.AtLeast(0))))
            .Where(s => s.Sigma is not null)
            .ToImmutableDictionary(s => s.Value, s => s.Sigma!.Value);

        var direction = signals.Child("direction");
        var directionParameters = new DirectionParameters(
            new Watts(direction.Number("deadband_w", Range.AtLeast(0)) ?? 0),
            direction.PositiveDuration("max_skew") ?? TimeSpan.Zero,
            direction.PositiveDuration("pairing_wait") ?? TimeSpan.Zero);
        if (directionParameters.PairingWait >= directionParameters.MaxSkew)
        {
            issues.Error(
                ConfigurationIssueCodes.InvalidValue,
                ConfigRead.Join(direction.Path, "pairing_wait"),
                direction.Self,
                "pairing_wait must be shorter than max_skew.");
        }

        var refresh = signals.Child("refresh");
        var refreshParameters = new RefreshDetectionParameters(
            refresh.Integer("window_intervals", 2, 4096) ?? 0,
            refresh.Integer("min_intervals", 2, 4096) ?? 0,
            refresh.Number("quantile", new Range(0, 1, MinExclusive: true)) ?? 0);
        if (refreshParameters.MinIntervals > refreshParameters.WindowIntervals)
        {
            issues.Error(
                ConfigurationIssueCodes.InvalidValue,
                ConfigRead.Join(refresh.Path, "min_intervals"),
                refresh.Self,
                "min_intervals must not exceed window_intervals.");
        }

        var probe = new FreshnessProbeParameters(
            signals.Child("freshness_probe").Number("after_fraction", new Range(0, 1, MinExclusive: true, MaxExclusive: true)) ?? 0);

        var balance = signals.Child("balance");
        var balanceParameters = new BalanceParameters(
            new Watts(balance.Number("residual_threshold_w", Range.Above(0)) ?? 0),
            balance.PositiveDuration("max_skew") ?? TimeSpan.Zero);

        var maxAgeLimit = signals.PositiveDuration("max_age_limit") ?? TimeSpan.Zero;
        if (maxAgeLimit > SignalStoreRunner.LongestTimerDue)
        {
            // Past the platform's longest timer the store could not wake up when a signal turns stale.
            issues.Error(
                ConfigurationIssueCodes.OutOfRange,
                ConfigRead.Join(signals.Path, "max_age_limit"),
                signals.Node("max_age_limit"),
                string.Create(CultureInfo.InvariantCulture, $"max_age_limit may be at most {SignalStoreRunner.LongestTimerDue.TotalHours:0} h."));
        }

        var reserve = root.Child("reserve_derivation");
        var reserveParameters = new ReserveDerivationParameters(
            new Watts(reserve.Number("reference_step_w", Range.AtLeast(0)) ?? 0),
            reserve.NonNegativeDuration("shed_latency") ?? TimeSpan.Zero,
            reserve.Number("sigma_multiplier", Range.AtLeast(0)) ?? 0,
            new Watts(reserve.Number("floor_w", Range.AtLeast(0)) ?? 0),
            dispatchParameters.Cycle);

        if (issues.Errors.Count > errorsBefore)
        {
            return null;
        }

        return new ControlParameters(
            connectionDefaults,
            new BreakerParameters(continuous, tiers),
            budgetParameters,
            dispatchParameters,
            settling,
            new SignalParameters(defaultSigma, directionParameters, refreshParameters, probe, balanceParameters, maxAgeLimit),
            reserveParameters);
    }

    private static ImmutableDictionary<BreakerCharacteristic, double> BindContinuousMultiples(Section section)
    {
        section.CheckKeys([.. Characteristics.Keys]);
        var builder = ImmutableDictionary.CreateBuilder<BreakerCharacteristic, double>();
        foreach (var (key, characteristic) in Characteristics)
        {
            if (section.Number(key, Range.Above(0)) is { } multiple)
            {
                builder[characteristic] = multiple;
            }
        }

        return builder.ToImmutable();
    }

    private static ImmutableDictionary<BreakerCharacteristic, TierTable> BindTiers(Section section, ConfigIssues issues)
    {
        section.CheckKeys([.. Characteristics.Keys]);
        var builder = ImmutableDictionary.CreateBuilder<BreakerCharacteristic, TierTable>();
        foreach (var (key, characteristic) in Characteristics)
        {
            var path = ConfigRead.Join(section.Path, key);
            if (section.Node(key) is not { } node)
            {
                continue;
            }

            if (node is not ConfigSequence sequence || sequence.Items.Length == 0)
            {
                issues.Error(ConfigurationIssueCodes.WrongType, path, node, "Expected a non-empty list of tier bands.");
                continue;
            }

            if (BindTierTable(sequence, path, issues) is { } table)
            {
                builder[characteristic] = table;
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Bands ascend strictly by up_to (inclusive upper limits, as multiples of In), only the last is
    /// open (up_to null), and the allowed duration never grows with the current: a higher current
    /// may never be tolerated for longer.
    /// </summary>
    private static TierTable? BindTierTable(ConfigSequence sequence, string path, ConfigIssues issues)
    {
        var errorsBefore = issues.Errors.Count;
        var bands = ImmutableArray.CreateBuilder<TierBand>();
        for (var i = 0; i < sequence.Items.Length; i++)
        {
            var bandPath = $"{path}[{i.ToString(CultureInfo.InvariantCulture)}]";
            var band = new Section(sequence.Items[i], bandPath, issues);
            band.CheckKeys(["up_to", "allowed", "reaction"]);

            double? upTo = null;
            if (band.Node("up_to", required: false) is { } upToNode)
            {
                upTo = ConfigRead.Number(upToNode, ConfigRead.Join(bandPath, "up_to"), issues, Range.Above(0));
            }

            TimeSpan? allowed = null;
            var allowedOk = band.Node("allowed") is { } allowedNode
                && ConfigRead.TryDuration(allowedNode, ConfigRead.Join(bandPath, "allowed"), issues, allowUnlimited: true, out allowed);
            var reaction = band.Node("reaction") is { } reactionNode
                ? ConfigRead.Choice(reactionNode, ConfigRead.Join(bandPath, "reaction"), issues, Reactions)
                : null;
            if (allowedOk && reaction is { } r)
            {
                bands.Add(new TierBand(upTo, allowed, r));
            }
        }

        if (issues.Errors.Count > errorsBefore)
        {
            return null;
        }

        for (var i = 0; i < bands.Count; i++)
        {
            var isLast = i == bands.Count - 1;
            var problem = (isLast, bands[i].UpTo) switch
            {
                (true, not null) => "The last band must be open-ended (up_to: null).",
                (false, null) => "Only the last band may be open-ended (up_to: null).",
                _ when i > 0 && bands[i].UpTo is { } upTo && upTo <= bands[i - 1].UpTo => "up_to must ascend strictly from band to band.",
                _ when i > 0 && Longer(bands[i].Allowed, bands[i - 1].Allowed) => "A higher band may not allow a longer duration than the band below it.",
                _ => null,
            };
            if (problem is not null)
            {
                issues.Error(ConfigurationIssueCodes.TierBandsInvalid, $"{path}[{i.ToString(CultureInfo.InvariantCulture)}]", sequence.Items[i], problem);
                return null;
            }
        }

        return new TierTable(bands.ToImmutable());
    }

    // Null is unlimited, longer than any duration.
    private static bool Longer(TimeSpan? candidate, TimeSpan? reference) =>
        (candidate, reference) switch
        {
            (null, null) => false,
            (null, _) => true,
            (_, null) => false,
            _ => candidate.Value > reference.Value,
        };

    /// <summary>A mapping in the merged tree; every key read through it is required.</summary>
    private sealed class Section
    {
        private readonly ConfigIssues _issues;
        private readonly ConfigMapping? _mapping;

        public Section(ConfigNode? node, string path, ConfigIssues issues)
        {
            _issues = issues;
            Path = path;
            Self = node;
            _mapping = ConfigRead.Mapping(node, path, issues);
            if (_mapping is null && ConfigRead.IsNull(node))
            {
                issues.Error(ConfigurationIssueCodes.MissingKey, path, null, $"'{path}' is required.");
            }
        }

        public string Path { get; }

        public ConfigNode? Self { get; }

        public Section Child(string key) => new(_mapping is null ? null : ConfigRead.Optional(_mapping, key), ConfigRead.Join(Path, key), _issues);

        public void CheckKeys(IReadOnlyCollection<string> allowed)
        {
            if (_mapping is not null)
            {
                ConfigRead.CheckKeys(_mapping, Path, allowed, _issues);
            }
        }

        public ConfigNode? Node(string key, bool required = true)
        {
            if (_mapping is null)
            {
                return null;
            }

            return required ? ConfigRead.Required(_mapping, key, Path, _issues) : ConfigRead.Optional(_mapping, key);
        }

        public double? Number(string key) => Node(key) is { } node ? ConfigRead.Number(node, ConfigRead.Join(Path, key), _issues) : null;

        public double? Number(string key, Range range) =>
            Node(key) is { } node ? ConfigRead.Number(node, ConfigRead.Join(Path, key), _issues, range) : null;

        public int? Integer(string key, int min, int max) =>
            Node(key) is { } node ? ConfigRead.Integer(node, ConfigRead.Join(Path, key), _issues, min, max) : null;

        public TimeSpan? PositiveDuration(string key) =>
            Node(key) is { } node ? ConfigRead.PositiveDuration(node, ConfigRead.Join(Path, key), _issues) : null;

        public TimeSpan? NonNegativeDuration(string key) =>
            Node(key) is { } node ? ConfigRead.NonNegativeDuration(node, ConfigRead.Join(Path, key), _issues) : null;
    }
}
