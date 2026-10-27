using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Switchboard.Application.Signals;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Signals;
using Range = Switchboard.Application.Configuration.ConfigRead.Range;

namespace Switchboard.Application.Configuration;

/// <summary>How the user's periods are settled and what a kWh not exported earns (14-ECONOMICS, slice 1: parsed and echoed, not used).</summary>
public sealed record SettlementDefinition(SettlementModelId Model, PriceSpec? ExportPrice);

/// <summary>
/// The optional top-level <c>economics:</c> section: a free currency label (never a symbol in code), the dated
/// holiday list, the import price and the settlement. Absent in a 0.9 file, and then nothing changes.
/// </summary>
public sealed record EconomicsDefinition(string Currency, HolidayCalendar Holidays, PriceSpec? Tariff, SettlementDefinition? Settlement)
{
    /// <summary>The longest currency label; a label, not a code list, so "PLN", "EUR" and a short local word all fit.</summary>
    public const int MaxCurrencyLength = 8;
}

/// <summary>Reads and validates <c>economics:</c>; every problem is collected with its position, the document stays untrusted.</summary>
internal static partial class EconomicsSectionParser
{
    public const string Key = "economics";

    private static readonly string[] SectionKeys = ["currency", "holidays", "tariff", "settlement"];
    private static readonly string[] PriceKeys = ["default_zone", "zones"];
    private static readonly string[] DynamicKeys = ["signal", "fallback", "add", "curve"];
    private static readonly string[] ZoneKeys = ["id", "price", "when"];
    private static readonly string[] RuleKeys = ["days", "months", "hours"];
    private static readonly string[] SettlementKeys = ["model", "export_price"];

    // Known to the design (14-ECONOMICS) but not built: refused with its own message, not as a typo.
    private const string DeferredModel = "net_metering";

    private static readonly ImmutableDictionary<string, SettlementModelId> Models = ImmutableDictionary<string, SettlementModelId>.Empty
        .Add("hourly_netting", SettlementModelId.HourlyNetting)
        .Add("net_billing", SettlementModelId.NetBilling);

    private static readonly ImmutableDictionary<string, DayClass> Days = ImmutableDictionary<string, DayClass>.Empty
        .Add("workday", DayClass.Workday)
        .Add("weekend", DayClass.Weekend)
        .Add("holiday", DayClass.Holiday)
        .Add("monday", DayClass.Monday)
        .Add("tuesday", DayClass.Tuesday)
        .Add("wednesday", DayClass.Wednesday)
        .Add("thursday", DayClass.Thursday)
        .Add("friday", DayClass.Friday)
        .Add("saturday", DayClass.Saturday)
        .Add("sunday", DayClass.Sunday);

    private static readonly string[] MonthNames = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    /// <param name="loadedAt">The load time: its year is "the current year" for the stale holiday list warning; the API re-checks against the local clock.</param>
    /// <param name="signals">The signals declared under <c>signals:</c>: a dynamic price names one of the price signals among them.</param>
    public static EconomicsDefinition? Read(ConfigMapping root, DateTimeOffset loadedAt, ImmutableArray<SignalDefinition> signals, ConfigIssues issues)
    {
        if (root.Find(Key) is not { } entry || ConfigRead.IsNull(entry.Value))
        {
            return null;
        }

        if (ConfigRead.Mapping(entry.Value, Key, issues) is not { } mapping)
        {
            return null;
        }

        var errorsBefore = issues.Errors.Count;
        ConfigRead.CheckKeys(mapping, Key, SectionKeys, issues);

        var prices = new PriceContext(
            signals.Where(s => s.Kind == SignalKind.Price).ToImmutableDictionary(s => s.Name),
            signals.Where(s => s.Kind == SignalKind.Series && s.SeriesMeaning == SeriesValueKind.PricePerKwh).ToImmutableDictionary(s => s.Name));
        var currency = ReadCurrency(mapping, issues);
        var holidays = ReadHolidays(mapping, issues);
        PriceSpec? tariff = null;
        if (ConfigRead.Optional(mapping, "tariff") is { } tariffNode)
        {
            tariff = ReadPrice(tariffNode, ConfigRead.Join(Key, "tariff"), issues, prices);
        }

        var settlement = ReadSettlement(mapping, prices, issues);
        if (issues.Errors.Count > errorsBefore || currency is null || holidays is null)
        {
            return null;
        }

        WarnWhenHolidaysStale(mapping, holidays, tariff, settlement?.ExportPrice, loadedAt.Year, issues);
        return new EconomicsDefinition(currency, holidays, tariff, settlement);
    }

    private static string? ReadCurrency(ConfigMapping mapping, ConfigIssues issues)
    {
        var path = ConfigRead.Join(Key, "currency");
        if (ConfigRead.Required(mapping, "currency", Key, issues) is not { } node || ConfigRead.String(node, path, issues) is not { } text)
        {
            return null;
        }

        var label = text.Trim();
        if (label.Length is 0 or > EconomicsDefinition.MaxCurrencyLength || label.Any(char.IsControl))
        {
            issues.Error(
                ConfigurationIssueCodes.InvalidValue,
                path,
                node,
                $"The currency is a label of 1 to {EconomicsDefinition.MaxCurrencyLength} characters (for example an ISO code), shown as written.");
            return null;
        }

        return label;
    }

    private static HolidayCalendar? ReadHolidays(ConfigMapping mapping, ConfigIssues issues)
    {
        var path = ConfigRead.Join(Key, "holidays");
        if (ConfigRead.Optional(mapping, "holidays") is not { } node)
        {
            return HolidayCalendar.Empty;
        }

        if (node is not ConfigSequence sequence)
        {
            issues.Error(ConfigurationIssueCodes.WrongType, path, node, "Expected a list of dates (yyyy-MM-dd).");
            return null;
        }

        var dates = new List<DateOnly>();
        var errorsBefore = issues.Errors.Count;
        for (var i = 0; i < sequence.Items.Length; i++)
        {
            var itemPath = Indexed(path, i);
            if (ConfigRead.String(sequence.Items[i], itemPath, issues) is not { } text)
            {
                continue;
            }

            if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                dates.Add(date);
            }
            else
            {
                issues.Error(ConfigurationIssueCodes.InvalidValue, itemPath, sequence.Items[i], $"'{text}' is not a date; write it as yyyy-MM-dd.");
            }
        }

        return issues.Errors.Count > errorsBefore ? null : new HolidayCalendar(dates);
    }

    private static SettlementDefinition? ReadSettlement(ConfigMapping mapping, PriceContext prices, ConfigIssues issues)
    {
        var path = ConfigRead.Join(Key, "settlement");
        if (ConfigRead.Optional(mapping, "settlement") is not { } node || ConfigRead.Mapping(node, path, issues) is not { } settlement)
        {
            return null;
        }

        ConfigRead.CheckKeys(settlement, path, SettlementKeys, issues);
        SettlementModelId? model = null;
        if (ConfigRead.Required(settlement, "model", path, issues) is { } modelNode)
        {
            if (modelNode is ConfigScalar { Value: DeferredModel })
            {
                issues.Error(
                    ConfigurationIssueCodes.InvalidValue,
                    ConfigRead.Join(path, "model"),
                    modelNode,
                    $"'{DeferredModel}' is deferred: it needs a persisted credit balance and a monthly or annual period, which this version does not have. Use one of: {string.Join(", ", Models.Keys)}.");
            }
            else
            {
                model = ConfigRead.Choice(modelNode, ConfigRead.Join(path, "model"), issues, Models);
            }
        }

        PriceSpec? export = null;
        if (ConfigRead.Optional(settlement, "export_price") is { } exportNode)
        {
            export = ReadPrice(exportNode, ConfigRead.Join(path, "export_price"), issues, prices);
        }

        return model is { } m ? new SettlementDefinition(m, export) : null;
    }

    /// <summary>The price signals a dynamic price may name, by logical signal name.</summary>
    /// <param name="Curves">The series signals with a price unit (price curves) a dynamic price may name as its forward <c>curve</c>.</param>
    private sealed record PriceContext(ImmutableDictionary<SignalName, SignalDefinition> Signals, ImmutableDictionary<SignalName, SignalDefinition> Curves);

    /// <summary>
    /// The price signals of the document after the economics section was read: each gets the currency label its unit must carry
    /// (a signal whose label differs is unusable, warned <c>unit_mismatch</c>, and the price stays at its fallback), and a signal
    /// with a price role that no spec names is warned <c>price_signal_unused</c>.
    /// </summary>
    public static ImmutableArray<SignalDefinition> BindPriceSignals(ConfigMapping root, ImmutableArray<SignalDefinition> signals, EconomicsDefinition? economics, ConfigIssues issues)
    {
        var used = PriceSpecs.Signals(economics?.Tariff).Concat(PriceSpecs.Signals(economics?.Settlement?.ExportPrice)).ToHashSet();
        var signalNodes = root.Find("signals")?.Value as ConfigMapping;
        var curves = NamedCurves(root);
        var plans = root.Find(PlannerKey) is not null;
        return [.. signals.Select(signal =>
        {
            var isCurve = signal.Kind == SignalKind.Series && signal.SeriesMeaning == SeriesValueKind.PricePerKwh;
            if (signal.Kind != SignalKind.Price && !isCurve)
            {
                WarnWhenForecastUnused(signal, plans, curves, signalNodes, issues);
                return signal;
            }

            var mapping = signalNodes?.Find(signal.Name.Value)?.Value as ConfigMapping;
            if (economics is { } section && SignalUnits.TryParsePrice(signal.Unit, out var label, out _) && label != section.Currency)
            {
                issues.Warning(
                    ConfigurationIssueCodes.UnitMismatch,
                    ConfigRead.Join(signal.Path, "unit"),
                    mapping?.Find("unit")?.Value,
                    $"The unit '{signal.Unit}' carries the label '{label}' but economics.currency is '{section.Currency}': the signal is not usable and a price that follows it stays at its fallback.");
            }

            if (isCurve)
            {
                WarnWhenForecastUnused(signal, plans, curves, signalNodes, issues);
            }

            if (signal.Role is SignalRole.PriceImport or SignalRole.PriceExport && !used.Contains(signal.Name))
            {
                issues.Warning(
                    ConfigurationIssueCodes.PriceSignalUnused,
                    ConfigRead.Join(signal.Path, "role"),
                    mapping?.Find("role")?.Value,
                    $"Signal '{signal.Name}' has a price role but no price names it: write signal: {signal.Name} in economics.tariff or economics.settlement.export_price, or it feeds nothing.");
            }

            return signal with { PriceCurrency = economics?.Currency };
        })];
    }

    // The planner section is read by its own parser (slice 1); here only its presence matters: the forecast signals have a consumer.
    private const string PlannerKey = "planner";

    /// <summary>
    /// A forecast, sun-time or price-curve role that nothing consumes is warned (<c>forecast_signal_unused</c>): the forecast and sun
    /// roles are read by the planner, so they are used when a <c>planner:</c> section exists; a curve role is used when an
    /// economics price names it as its <c>curve:</c>. Nothing is dropped: the signal still shows in diagnostics.
    /// </summary>
    private static void WarnWhenForecastUnused(SignalDefinition signal, bool plans, HashSet<string> curves, ConfigMapping? signalNodes, ConfigIssues issues)
    {
        var used = signal.Role switch
        {
            SignalRole.PvForecast or SignalRole.PvForecastToday or SignalRole.PvForecastTomorrow or SignalRole.SunRising or SignalRole.SunSetting => plans,
            SignalRole.PriceCurveImport or SignalRole.PriceCurveExport => curves.Contains(signal.Name.Value),
            _ => true,
        };
        if (used)
        {
            return;
        }

        var mapping = signalNodes?.Find(signal.Name.Value)?.Value as ConfigMapping;
        issues.Warning(
            ConfigurationIssueCodes.ForecastSignalUnused,
            ConfigRead.Join(signal.Path, "role"),
            mapping?.Find("role")?.Value,
            signal.Role is SignalRole.PriceCurveImport or SignalRole.PriceCurveExport
                ? $"Signal '{signal.Name}' has a price curve role but no price names it: write curve: {signal.Name} next to signal: in economics.tariff or economics.settlement.export_price, or it feeds nothing."
                : $"Signal '{signal.Name}' has a forecast role but there is no planner: section to read it, so it feeds nothing.");
    }

    /// <summary>The names written after <c>curve:</c> in the economics prices, from the raw document (whatever the price parsed to).</summary>
    private static HashSet<string> NamedCurves(ConfigMapping root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (root.Find(Key)?.Value is not ConfigMapping economics)
        {
            return names;
        }

        var tariff = economics.Find("tariff")?.Value as ConfigMapping;
        var export = (economics.Find("settlement")?.Value as ConfigMapping)?.Find("export_price")?.Value as ConfigMapping;
        foreach (var price in new[] { tariff, export })
        {
            if (price?.Find("curve")?.Value is ConfigScalar { Value: string name })
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>A number (flat), a zoned schedule or, where a dynamic price is allowed, a price that follows a signal.</summary>
    private static PriceSpec? ReadPrice(ConfigNode node, string path, ConfigIssues issues, PriceContext prices, bool allowDynamic = true)
    {
        if (node is ConfigScalar)
        {
            return ConfigRead.Number(node, path, issues, Range.AtLeast(0)) is { } value ? new FlatPrice(value) : null;
        }

        if (node is not ConfigMapping mapping)
        {
            issues.Error(ConfigurationIssueCodes.WrongType, path, node, "Expected a number (a flat price per kWh) or a mapping with zones.");
            return null;
        }

        if (mapping.Entries.FirstOrDefault(e => DynamicKeys.Contains(e.Key)) is { } dynamic)
        {
            if (!allowDynamic)
            {
                issues.ErrorAtKey(
                    ConfigurationIssueCodes.DynamicPriceNotSupported,
                    ConfigRead.Join(path, dynamic.Key),
                    dynamic,
                    "A dynamic price is not allowed here: fallback and add are static (a number or zones).");
                return null;
            }

            return ReadDynamic(mapping, path, issues, prices);
        }

        ConfigRead.CheckKeys(mapping, path, PriceKeys, issues);
        var errorsBefore = issues.Errors.Count;
        if (ConfigRead.Required(mapping, "zones", path, issues) is not { } zonesNode)
        {
            return null;
        }

        var zonesPath = ConfigRead.Join(path, "zones");
        if (zonesNode is not ConfigSequence sequence || sequence.Items.IsEmpty)
        {
            issues.Error(ConfigurationIssueCodes.WrongType, zonesPath, zonesNode, "Expected a list of at least one zone.");
            return null;
        }

        var zones = ImmutableArray.CreateBuilder<TariffZone>();
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < sequence.Items.Length; i++)
        {
            if (ReadZone(sequence.Items[i], Indexed(zonesPath, i), issues) is not { } zone)
            {
                continue;
            }

            if (ids.TryAdd(zone.Id, i))
            {
                zones.Add(zone);
            }
            else
            {
                issues.Error(
                    ConfigurationIssueCodes.DuplicateZoneId,
                    ConfigRead.Join(Indexed(zonesPath, i), "id"),
                    (sequence.Items[i] as ConfigMapping)?.Find("id")?.Value,
                    $"Zone '{zone.Id}' is already declared by zones[{ids[zone.Id]}]; zone ids are unique.");
            }
        }

        string? defaultZone = null;
        if (ConfigRead.Optional(mapping, "default_zone") is { } defaultNode
            && ConfigRead.String(defaultNode, ConfigRead.Join(path, "default_zone"), issues) is { } defaultId)
        {
            defaultZone = defaultId;
            if (!ids.ContainsKey(defaultId))
            {
                issues.Error(
                    ConfigurationIssueCodes.InvalidValue,
                    ConfigRead.Join(path, "default_zone"),
                    defaultNode,
                    $"'{defaultId}' is not a zone id; the default zone must be one of: {string.Join(", ", ids.Keys)}.");
            }
        }

        if (issues.Errors.Count > errorsBefore)
        {
            return null;
        }

        var schedule = new ZonedPrice(zones.ToImmutable(), defaultZone);
        CheckCoverage(schedule, mapping, path, issues);
        return issues.Errors.Count > errorsBefore ? null : schedule;
    }

    /// <summary>
    /// <c>signal:</c> names a price signal; <c>fallback</c> is required and static (without it the document is invalid: a price
    /// that has nothing to fall back to would have to be guessed); <c>add</c> is optional and static, added while the signal is live.
    /// </summary>
    private static PriceSpec? ReadDynamic(ConfigMapping mapping, string path, ConfigIssues issues, PriceContext prices)
    {
        var errorsBefore = issues.Errors.Count;
        ConfigRead.CheckKeys(mapping, path, DynamicKeys, issues);

        SignalName? signal = null;
        var signalPath = ConfigRead.Join(path, "signal");

        // A curve with no signal is a warning, not an error (15-PLANNER, Configuration checks): it replaces nothing, so the price is the fallback.
        var curveOnly = ConfigRead.Optional(mapping, "signal") is null && ConfigRead.Optional(mapping, "curve") is not null;
        if (curveOnly)
        {
            issues.Warning(
                ConfigurationIssueCodes.CurveWithoutSignal,
                ConfigRead.Join(path, "curve"),
                ConfigRead.Optional(mapping, "curve"),
                "curve: without signal: replaces nothing; the price is its fallback. A forward curve sits beside the live price signal it extends.");
        }

        if (!curveOnly && ConfigRead.Required(mapping, "signal", path, issues) is { } signalNode && ConfigRead.String(signalNode, signalPath, issues) is { } text)
        {
            if (SignalName.TryParse(text, out var name) && prices.Signals.ContainsKey(name.Value))
            {
                signal = name;
            }
            else
            {
                issues.Error(
                    ConfigurationIssueCodes.InvalidValue,
                    signalPath,
                    signalNode,
                    $"'{text}' is not a signal of kind price declared under signals:; a dynamic price follows one (kind: price, unit: <currency>/kWh or <currency>/MWh).");
            }
        }

        PriceSpec? fallback = null;
        var fallbackPath = ConfigRead.Join(path, "fallback");
        if (ConfigRead.Optional(mapping, "fallback") is { } fallbackNode)
        {
            fallback = ReadPrice(fallbackNode, fallbackPath, issues, prices, allowDynamic: false);
        }
        else
        {
            issues.Error(
                ConfigurationIssueCodes.PriceFallbackMissing,
                fallbackPath,
                mapping,
                "A dynamic price needs a static fallback (a number or zones): the price used whenever the signal is stale, unavailable or in the wrong unit.");
        }

        PriceSpec? add = null;
        if (ConfigRead.Optional(mapping, "add") is { } addNode)
        {
            add = ReadPrice(addNode, ConfigRead.Join(path, "add"), issues, prices, allowDynamic: false);
        }

        SignalName? curve = null;
        var curvePath = ConfigRead.Join(path, "curve");
        if (ConfigRead.Optional(mapping, "curve") is { } curveNode && ConfigRead.String(curveNode, curvePath, issues) is { } curveText)
        {
            if (SignalName.TryParse(curveText, out var curveName) && prices.Curves.ContainsKey(curveName.Value))
            {
                curve = curveName;
            }
            else
            {
                issues.Error(
                    ConfigurationIssueCodes.InvalidValue,
                    curvePath,
                    curveNode,
                    $"'{curveText}' is not a signal of kind series with a price unit declared under signals:; a forward curve is one (kind: series, attribute: ..., unit: <currency>/kWh or <currency>/MWh).");
            }
        }

        if (curveOnly)
        {
            return issues.Errors.Count > errorsBefore ? null : fallback;
        }

        return issues.Errors.Count > errorsBefore || signal is null || fallback is null ? null : new DynamicPrice(signal.Value, fallback, add, curve);
    }

    /// <summary>
    /// Time no zone claims falls to the default zone; without one it has no price, and the schedule is refused. A default
    /// zone that also carries rules of its own is warned about when the rules leave a gap: it was probably meant to be listed.
    /// </summary>
    private static void CheckCoverage(ZonedPrice schedule, ConfigMapping mapping, string path, ConfigIssues issues)
    {
        var open = schedule with { DefaultZone = null };
        if (open.FindGap() is not { } gap)
        {
            return;
        }

        var where = string.Create(
            CultureInfo.InvariantCulture,
            $"{MonthNames[gap.Month - 1]}, {gap.Day.ToString().ToLowerInvariant()}{(gap.Holiday ? " (a holiday)" : string.Empty)}, {gap.MinuteOfDay / 60:00}:{gap.MinuteOfDay % 60:00}");
        if (schedule.DefaultZone is null)
        {
            issues.Error(
                ConfigurationIssueCodes.ZoneGap,
                ConfigRead.Join(path, "zones"),
                mapping,
                $"No zone claims every moment (for example {where}) and there is no default_zone to take the rest.");
        }
        else if (schedule.Zones.First(z => z.Id == schedule.DefaultZone).Rules is { IsDefaultOrEmpty: false })
        {
            issues.Warning(
                ConfigurationIssueCodes.ZoneGap,
                ConfigRead.Join(path, "default_zone"),
                mapping.Find("default_zone")?.Value,
                $"Moments no zone claims (for example {where}) fall to the default zone '{schedule.DefaultZone}', which has rules of its own.");
        }
    }

    private static TariffZone? ReadZone(ConfigNode node, string path, ConfigIssues issues)
    {
        if (ConfigRead.Mapping(node, path, issues) is not { } mapping)
        {
            return null;
        }

        var errorsBefore = issues.Errors.Count;
        ConfigRead.CheckKeys(mapping, path, ZoneKeys, issues);

        string? id = null;
        if (ConfigRead.Required(mapping, "id", path, issues) is { } idNode && ConfigRead.String(idNode, ConfigRead.Join(path, "id"), issues) is { } text)
        {
            if (ZoneId().IsMatch(text))
            {
                id = text;
            }
            else
            {
                issues.Error(
                    ConfigurationIssueCodes.InvalidValue,
                    ConfigRead.Join(path, "id"),
                    idNode,
                    $"'{text}' is not a valid zone id: lower-case letters, digits and '_', starting with a letter, at most 63 characters.");
            }
        }

        double? price = null;
        if (ConfigRead.Required(mapping, "price", path, issues) is { } priceNode)
        {
            price = ConfigRead.Number(priceNode, ConfigRead.Join(path, "price"), issues, Range.AtLeast(0));
        }

        var rules = ImmutableArray.CreateBuilder<ZoneRule>();
        if (ConfigRead.Optional(mapping, "when") is { } whenNode)
        {
            var whenPath = ConfigRead.Join(path, "when");
            if (whenNode is not ConfigSequence sequence)
            {
                issues.Error(ConfigurationIssueCodes.WrongType, whenPath, whenNode, "Expected a list of rules (days, months, hours); an empty list is a zone with no rule of its own.");
            }
            else
            {
                for (var i = 0; i < sequence.Items.Length; i++)
                {
                    if (ReadRule(sequence.Items[i], Indexed(whenPath, i), issues) is { } rule)
                    {
                        rules.Add(rule);
                    }
                }
            }
        }

        return issues.Errors.Count > errorsBefore || id is null || price is not { } p ? null : new TariffZone(id, p, rules.ToImmutable());
    }

    private static ZoneRule? ReadRule(ConfigNode node, string path, ConfigIssues issues)
    {
        if (ConfigRead.Mapping(node, path, issues) is not { } mapping)
        {
            return null;
        }

        var errorsBefore = issues.Errors.Count;
        ConfigRead.CheckKeys(mapping, path, RuleKeys, issues);

        var days = ReadList<DayClass>(mapping, "days", path, issues, (text, _) => Days.TryGetValue(text, out var d) ? d : null, $"one of: {string.Join(", ", Days.Keys)}");
        var months = ReadList<int>(
            mapping,
            "months",
            path,
            issues,
            (text, _) => Array.IndexOf(MonthNames, text) is var index and >= 0 ? index + 1 : null,
            $"one of: {string.Join(", ", MonthNames)}");
        var hours = ReadList<HoursRange>(
            mapping,
            "hours",
            path,
            issues,
            (text, _) => HoursRange.TryParse(text, out var range) ? range : null,
            "a range HH:MM-HH:MM (end exclusive, 24:00 allowed, a start after the end wraps past midnight)");

        return issues.Errors.Count > errorsBefore ? null : new ZoneRule(days, months, hours);
    }

    /// <summary>A key that is one text or a list of texts, each converted; absent means "all" (an empty array), an empty list is refused.</summary>
    private static ImmutableArray<T> ReadList<T>(ConfigMapping mapping, string key, string path, ConfigIssues issues, Func<string, int, T?> convert, string expected)
        where T : struct
    {
        if (ConfigRead.Optional(mapping, key) is not { } node)
        {
            return [];
        }

        var keyPath = ConfigRead.Join(path, key);
        var items = node is ConfigSequence sequence ? sequence.Items : [node];
        if (items.IsEmpty)
        {
            issues.Error(ConfigurationIssueCodes.InvalidValue, keyPath, node, $"'{key}' may not be an empty list; leave the key out to match all.");
            return [];
        }

        var result = ImmutableArray.CreateBuilder<T>();
        for (var i = 0; i < items.Length; i++)
        {
            var itemPath = node is ConfigSequence ? Indexed(keyPath, i) : keyPath;
            if (ConfigRead.String(items[i], itemPath, issues) is not { } text)
            {
                continue;
            }

            if (convert(text, i) is { } value)
            {
                result.Add(value);
            }
            else
            {
                issues.Error(ConfigurationIssueCodes.InvalidValue, itemPath, items[i], $"'{text}' is not valid; expected {expected}.");
            }
        }

        return result.ToImmutable();
    }

    private static void WarnWhenHolidaysStale(ConfigMapping mapping, HolidayCalendar holidays, PriceSpec? tariff, PriceSpec? export, int year, ConfigIssues issues)
    {
        var usesHolidays = PriceSpecs.Schedules(tariff).Concat(PriceSpecs.Schedules(export))
            .SelectMany(z => z.Zones).SelectMany(z => z.Rules).SelectMany(r => r.Days)
            .Any(d => d is DayClass.Workday or DayClass.Holiday);
        var empty = holidays.Dates.Count == 0;
        if (empty ? !usesHolidays : !holidays.HasNoDateIn(year))
        {
            return;
        }

        issues.Warning(
            ConfigurationIssueCodes.HolidaysListStale,
            ConfigRead.Join(Key, "holidays"),
            mapping.Find("holidays")?.Value ?? mapping,
            empty
                ? "The zones use workday or holiday but economics.holidays lists no date: public holidays are priced as ordinary days."
                : string.Create(CultureInfo.InvariantCulture, $"economics.holidays has no date in {year}: renew the list, public holidays are priced as ordinary days until then."));
    }

    private static string Indexed(string path, int index) => $"{path}[{index.ToString(CultureInfo.InvariantCulture)}]";

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex ZoneId();
}
