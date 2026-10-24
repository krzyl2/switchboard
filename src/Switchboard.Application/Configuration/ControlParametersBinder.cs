using System.Collections.Immutable;
using System.Globalization;
using Switchboard.Application.Signals;
using Switchboard.Domain.Connection;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Guard;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Range = Switchboard.Application.Configuration.ConfigRead.Range;

namespace Switchboard.Application.Configuration;

/// <summary>
/// Binds the merged defaults-plus-overrides tree to <see cref="ControlParameters"/>. Every key is
/// required (the defaults file carries all of them) and every value is range-checked, so an
/// override cannot produce a nonsensical controller. The tier tables and the budget's recovery
/// time are then checked against the safety envelope (<c>limits:</c>, defaults only), so an
/// override cannot produce an unsafe one.
/// </summary>
internal static class ControlParametersBinder
{
    /// <summary>The key of the safety envelope; read from the defaults file only.</summary>
    public const string LimitsKey = "limits";

    // Schema bounds, not control parameters: more lag polls than this is a typo, not a meter.
    private const int MaxCurrentLagPolls = 10;
    private const int MaxRetentionDays = 3650;

    private static readonly ImmutableDictionary<string, DeviceType> DeviceTypes =
        new Dictionary<string, DeviceType>
        {
            ["onoff"] = DeviceType.OnOff,
            ["stepped"] = DeviceType.Stepped,
            ["modulating"] = DeviceType.Modulating,
        }.ToImmutableDictionary(StringComparer.Ordinal);

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
        var continuousSection = breaker.Child("continuous_multiple");
        var continuous = BindContinuousMultiples(continuousSection);
        var tiersSection = breaker.Child("tiers");
        var tiers = BindTiers(tiersSection, issues);

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

        var currentLagPolls = signals.Integer("current_lag_polls", 0, MaxCurrentLagPolls) ?? 0;
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
            dispatchParameters.Cycle,
            dispatchParameters.PersistenceDownCycles,
            currentLagPolls);

        var devices = root.Child("devices");
        var reactionTimes = devices.Child("reaction_time");
        reactionTimes.CheckKeys([.. DeviceTypes.Keys]);
        var modulatingLockout = devices.Child("lockout").Child("modulating");
        var deviceDefaults = new DeviceDefaults(
            devices.PositiveDuration("state_max_age") ?? TimeSpan.Zero,
            DeviceTypes
                .Select(type => (type.Value, Time: reactionTimes.NonNegativeDuration(type.Key)))
                .Where(t => t.Time is not null)
                .ToImmutableDictionary(t => t.Value, t => t.Time!.Value),
            modulatingLockout.NonNegativeDuration("min_on") ?? TimeSpan.Zero,
            modulatingLockout.NonNegativeDuration("min_off") ?? TimeSpan.Zero);
        if (maxAgeLimit > TimeSpan.Zero && deviceDefaults.StateMaxAge > maxAgeLimit)
        {
            issues.Error(
                ConfigurationIssueCodes.OutOfRange,
                ConfigRead.Join(devices.Path, "state_max_age"),
                devices.Node("state_max_age"),
                "state_max_age may not exceed signals.max_age_limit: a frozen device state counts as fresh for its whole max_age.");
        }

        var decisionLog = new DecisionLogParameters(root.Child("decision_log").Integer("retention_days", 1, MaxRetentionDays) ?? 0);

        var limits = root.Child(LimitsKey);
        var envelope = new SafetyEnvelope(
            limits.Number("continuous_multiple_max", Range.Above(0)) ?? 0,
            limits.Number("unknown_continuous_multiple_max", Range.Above(0)) ?? 0,
            limits.Number("may_add_up_to_max", Range.Above(0)) ?? 0,
            limits.Number("open_band_from_max", Range.Above(0)) ?? 0,
            limits.PositiveDuration("budget_recovery_time_min") ?? TimeSpan.Zero,
            limits.Number("budget_stop_increases_below_min", Range.Fraction) ?? 0,
            limits.Number("budget_shed_one_below_min", Range.Fraction) ?? 0);

        if (issues.Errors.Count > errorsBefore)
        {
            return null;
        }

        CheckEnvelope(envelope, continuous, continuousSection, tiers, tiersSection, issues);
        if (budgetParameters.RecoveryTime < envelope.BudgetRecoveryTimeMin)
        {
            issues.Error(
                ConfigurationIssueCodes.TierEnvelopeViolated,
                ConfigRead.Join(budget.Path, "recovery_time"),
                budget.Node("recovery_time"),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"budget.recovery_time may be no shorter than {envelope.BudgetRecoveryTimeMin.TotalSeconds} s: a faster recovery lets short dips erase the memory of repeated overloads."));
        }

        CheckBudgetFloor(budget, "stop_increases_below", budgetParameters.StopIncreasesBelow, envelope.BudgetStopIncreasesBelowMin, issues);
        CheckBudgetFloor(budget, "shed_one_below", budgetParameters.ShedOneBelow, envelope.BudgetShedOneBelowMin, issues);

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
            new SignalParameters(defaultSigma, directionParameters, refreshParameters, probe, balanceParameters, maxAgeLimit, currentLagPolls),
            reserveParameters,
            deviceDefaults,
            decisionLog,
            envelope);
    }

    private static void CheckBudgetFloor(Section budget, string key, double value, double floor, ConfigIssues issues)
    {
        if (value < floor)
        {
            issues.Error(
                ConfigurationIssueCodes.TierEnvelopeViolated,
                ConfigRead.Join(budget.Path, key),
                budget.Node(key),
                string.Create(CultureInfo.InvariantCulture, $"budget.{key} may be raised but not set below {floor}: a lower threshold reacts later to the same loss of budget."));
        }
    }

    /// <summary>
    /// The safety envelope (DECISIONS, Stage 2): whatever <c>control:</c> overrides, every tier table
    /// sheds everything controlled at 0 s above the conventional tripping current, never lets a band
    /// above the continuous multiple run unlimited or add load, adds load only up to rated current,
    /// and claims no more continuous current than IEC 60898 gives. A violation is reported at the
    /// user's override: the band, else the continuous multiple that pushed a shipped band outside.
    /// </summary>
    private static void CheckEnvelope(
        SafetyEnvelope envelope,
        ImmutableDictionary<BreakerCharacteristic, double> continuous,
        Section continuousSection,
        ImmutableDictionary<BreakerCharacteristic, TierTable> tiers,
        Section tiersSection,
        ConfigIssues issues)
    {
        foreach (var (key, characteristic) in Characteristics)
        {
            if (!continuous.TryGetValue(characteristic, out var multiple) || !tiers.TryGetValue(characteristic, out var table))
            {
                continue;
            }

            var multiplePath = ConfigRead.Join(continuousSection.Path, key);
            var multipleNode = continuousSection.Node(key);
            var multipleMax = characteristic == BreakerCharacteristic.Unknown ? envelope.UnknownContinuousMultipleMax : envelope.ContinuousMultipleMax;
            if (multiple > multipleMax)
            {
                issues.Error(
                    ConfigurationIssueCodes.TierEnvelopeViolated,
                    multiplePath,
                    multipleNode,
                    string.Create(CultureInfo.InvariantCulture, $"The continuous multiple of characteristic {key} may be at most {multipleMax} × In."));
            }

            var items = tiersSection.Node(key) is ConfigSequence sequence ? sequence.Items : [];
            var bands = table.Bands;
            for (var i = 0; i < bands.Length; i++)
            {
                var (problem, blamesMultiple) = EnvelopeProblem(bands, i, multiple, envelope);
                if (problem is null)
                {
                    continue;
                }

                var bandPath = $"{ConfigRead.Join(tiersSection.Path, key)}[{i.ToString(CultureInfo.InvariantCulture)}]";
                var bandNode = i < items.Length ? items[i] : null;
                var (path, at) = blamesMultiple && bandNode?.Origin != ConfigOrigin.Document && multipleNode?.Origin == ConfigOrigin.Document
                    ? (multiplePath, multipleNode)
                    : (bandPath, bandNode);
                issues.Error(ConfigurationIssueCodes.TierEnvelopeViolated, path, at, problem);
            }
        }
    }

    /// <summary>What is wrong with band <paramref name="i"/>, and whether the continuous multiple is part of it.</summary>
    private static (string? Problem, bool BlamesMultiple) EnvelopeProblem(ImmutableArray<TierBand> bands, int i, double multiple, SafetyEnvelope envelope)
    {
        var band = bands[i];
        if (i == bands.Length - 1)
        {
            return band.Reaction != TierReaction.ShedAllOnPhase || band.Allowed != TimeSpan.Zero
                ? ("The open band (the last one) must be shed_all_on_phase with allowed: 0s.", false)
                : (null, false);
        }

        if (i == bands.Length - 2 && band.UpTo > envelope.OpenBandFromMax)
        {
            return (string.Create(
                CultureInfo.InvariantCulture,
                $"Everything above {envelope.OpenBandFromMax} × In (the conventional tripping current) must be shed at once: the last finite band may end there at most."), false);
        }

        var reachesAboveContinuous = band.UpTo > multiple;
        if (reachesAboveContinuous && band.Allowed is null)
        {
            return (string.Create(CultureInfo.InvariantCulture, $"A band above the continuous multiple ({multiple} × In) needs a finite allowed duration."), true);
        }

        if (reachesAboveContinuous && band.Reaction is TierReaction.MayAdd or TierReaction.Hold)
        {
            return (string.Create(CultureInfo.InvariantCulture, $"A band above the continuous multiple ({multiple} × In) must reduce or shed, not {ReactionName(band.Reaction)}."), true);
        }

        return band.Reaction == TierReaction.MayAdd && band.UpTo > envelope.MayAddUpToMax
            ? (string.Create(CultureInfo.InvariantCulture, $"may_add is allowed only up to {envelope.MayAddUpToMax} × In."), false)
            : (null, false);
    }

    private static string ReactionName(TierReaction reaction) => Reactions.First(r => r.Value == reaction).Key;

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
