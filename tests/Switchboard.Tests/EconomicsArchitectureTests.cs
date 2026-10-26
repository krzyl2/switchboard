using System.Reflection;
using System.Text.RegularExpressions;
using Switchboard.Application;
using Switchboard.Application.Signals;
using Switchboard.Domain.Economics;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests;

/// <summary>
/// Economics is structurally apart from protection (14-ECONOMICS, D2 to D4, CLAUDE.md): the guard runs on breaker current alone,
/// so no price type may be reachable from it, no currency symbol may be written into the code or the defaults (the label belongs to
/// the user's configuration), and the core never names an entity (prices arrive through the signal layer, slice 4).
/// </summary>
public sealed partial class EconomicsArchitectureTests
{
    private static readonly Assembly Domain = typeof(Tariff).Assembly;
    private static readonly Assembly Application = typeof(AppVersion).Assembly;

    private static bool IsEconomic(Type type) =>
        type.Namespace?.StartsWith("Switchboard.Domain.Economics", StringComparison.Ordinal) == true
        || type.Name is "EconomicsDefinition" or "SettlementDefinition" or "EconomicsParameters"
            or "EconomicsView" or "DeviceEconomicsView" or "EconomicsDecisionInputs" or "ValueOrder";

    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type.IsArray || type.IsByRef ? type.GetElementType()! : type;
        foreach (var argument in type.GetGenericArguments().SelectMany(Unwrap))
        {
            yield return argument;
        }
    }

    private static IEnumerable<Type> Signature(Type type) =>
        type.GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .SelectMany(m => m switch
            {
                MethodBase method => method.GetParameters().Select(p => p.ParameterType).Append(method is MethodInfo info ? info.ReturnType : typeof(void)),
                PropertyInfo property => [property.PropertyType],
                FieldInfo field => [field.FieldType],
                _ => [],
            })
            .SelectMany(Unwrap);

    [Fact]
    public void No_guard_type_has_a_price_or_economics_type_in_its_signature()
    {
        // Why: D2 to D4. Protection must keep working on fresh breaker data alone; a price, a tariff or a settlement type
        // in anything the guard holds, receives or returns would be the first step to letting a price move a limit.
        var guardTypes = Domain.GetTypes().Concat(Application.GetTypes())
            .Where(t => t.Namespace is { } ns && (ns.EndsWith(".Guard", StringComparison.Ordinal) || ns.Contains(".Guard.", StringComparison.Ordinal)))
            .ToList();

        var offenders = guardTypes.SelectMany(t => Signature(t).Where(IsEconomic).Select(s => $"{t.FullName}: {s.Name}")).ToList();

        Assert.True(guardTypes.Count > 5, "the guard namespaces were not found");
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_protection_inputs_and_the_device_spec_reach_no_economics_type()
    {
        // Why: the device spec travels to the guard. The economic keys of a device live on the configuration's DeviceDefinition only.
        var graph = SignalArchitectureTests.TypeGraph(typeof(ProtectionInputs));

        Assert.DoesNotContain(graph, IsEconomic);
        Assert.DoesNotContain(typeof(Switchboard.Domain.Devices.DeviceSpec).GetProperties(), p => IsEconomic(p.PropertyType));
    }

    [Fact]
    public void The_guard_sources_never_mention_a_price()
    {
        var files = new[] { "Switchboard.Domain/Guard", "Switchboard.Application/Guard" }
            .SelectMany(dir => Directory.GetFiles(RepoPaths.Combine("src", dir), "*.cs", SearchOption.AllDirectories))
            .ToList();
        var offenders = files.Where(f => PriceWord().IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();

        Assert.NotEmpty(files);
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_guard_never_orders_by_a_live_key_and_only_the_dispatch_start_order_takes_one()
    {
        // Why: 14-ECONOMICS, "The guard's shed order stays static". The guard's sources may not mention the dispatch order, the value order or
        // the order key, and ShedOrder (the guard's order) has no parameter through which a live key could be handed in; StartOrder (dispatch)
        // is the one place that takes a key function. Together with the scenario that sheds identically with and without prices, a price
        // cannot reach the order protection sheds in.
        var files = new[] { "Switchboard.Domain/Guard", "Switchboard.Application/Guard" }
            .SelectMany(dir => Directory.GetFiles(RepoPaths.Combine("src", dir), "*.cs", SearchOption.AllDirectories))
            .ToList();
        var offenders = files.Where(f => LiveOrderWord().IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();
        var shedOrderDelegates = typeof(Switchboard.Domain.Guard.ShedOrder).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .SelectMany(m => m.GetParameters()).Where(p => typeof(Delegate).IsAssignableFrom(p.ParameterType)).ToList();
        var startOrderKey = typeof(Switchboard.Domain.Dispatch.StartOrder).GetMethod(nameof(Switchboard.Domain.Dispatch.StartOrder.For))!.GetParameters();

        Assert.NotEmpty(files);
        Assert.Empty(offenders);
        Assert.Empty(shedOrderDelegates);
        Assert.Contains(startOrderKey, p => p.Name == "key" && typeof(Delegate).IsAssignableFrom(p.ParameterType));
    }

    [Fact]
    public void Dispatch_reaches_economics_only_through_its_own_setup_and_never_through_the_protection_inputs()
    {
        // Why: the economic types must be reachable from dispatch (that is what slice 3 is) but never from what the guard is handed:
        // DispatchSetup carries the EconomicsSetup, ProtectionSetup and ProtectionInputs do not.
        Assert.Contains(typeof(EconomicsSetup), SignalArchitectureTests.TypeGraph(typeof(Switchboard.Domain.Dispatch.DispatchSetup)));
        Assert.DoesNotContain(SignalArchitectureTests.TypeGraph(typeof(Switchboard.Domain.Guard.ProtectionSetup)), IsEconomic);
        Assert.DoesNotContain(SignalArchitectureTests.TypeGraph(typeof(Switchboard.Domain.Signals.ProtectionInputs)), IsEconomic);
    }

    [Fact]
    public void No_string_literal_in_src_carries_a_currency_symbol_or_a_hard_coded_currency()
    {
        // Why: the currency is a label the user writes in economics.currency and the UI shows verbatim; a symbol in code would
        // be shown to a user who pays in another currency. Only string literals are scanned, so comments may explain.
        var offenders = SourceFiles("src")
            .SelectMany(f => File.ReadLines(f).Select((line, index) => (File: f, Line: index + 1, Text: line)))
            .Where(l => !l.Text.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .SelectMany(l => Literal().Matches(l.Text).Where(m => Currency().IsMatch(m.Value)).Select(m => $"{Path.GetFileName(l.File)}:{l.Line} {m.Value}"))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_control_defaults_carry_no_currency_symbol()
    {
        var defaults = File.ReadAllText(RepoPaths.Combine("src", "Switchboard.Infrastructure", "Configuration", "control-defaults.yaml"));

        Assert.DoesNotMatch(Symbols(), defaults);
        Assert.Contains("economics:", defaults, StringComparison.Ordinal);
    }

    [Fact]
    public void The_scan_does_catch_a_symbol_in_a_literal()
    {
        // The scan must be able to fail: a literal with a symbol or a code is matched, a comment about one is not a literal.
        Assert.Matches(Currency(), "\"€ 1.00\"");
        Assert.Matches(Currency(), "\"price in PLN\"");
        Assert.DoesNotContain(Literal().Matches("// the label is PLN or a symbol"), m => Currency().IsMatch(m.Value));
    }

    [Fact]
    public void The_new_economics_code_names_no_entity_id()
    {
        // Why: the core never knows an entity ID; prices arrive through the signal layer. An entity literal in the domain module,
        // the economics parser or the endpoint would be vendor knowledge in the wrong place.
        var files = SourceFiles("src/Switchboard.Domain/Economics")
            .Append(RepoPaths.Combine("src", "Switchboard.Application", "Configuration", "EconomicsSectionParser.cs"))
            .Append(RepoPaths.Combine("src", "Switchboard.Host", "Web", "EconomicsEndpoint.cs"))
            .Append(RepoPaths.Combine("src", "Switchboard.Application", "Dispatch", "DispatchEngine.Economics.cs"))
            .ToList();
        var offenders = files.Where(f => EntityLiteral().IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();

        Assert.True(files.Count > 8);
        Assert.Empty(offenders);
    }

    [Fact]
    public void A_per_mwh_price_is_converted_in_the_signal_normalizer_and_nowhere_else()
    {
        // Why: CLAUDE.md, unit conversion happens only in the signal layer. The one place that turns a price per MWh into a price per kWh is
        // SignalUnits.PriceToKwh, called by the normalizer; no other source calls it, and nothing outside the signal layer and the configuration
        // reader (which only checks the unit's shape) reads a price unit at all.
        var callers = SourceFiles("src")
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\bPriceToKwh\s*\("))
            .Select(Path.GetFileName)
            .Order()
            .ToList();
        var parsers = SourceFiles("src")
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\bTryParsePrice\s*\("))
            .Select(Path.GetFileName)
            .Order()
            .ToList();

        Assert.Equal(["SignalNormalizer.cs"], callers);
        Assert.Equal(["EconomicsSectionParser.cs", "SignalNormalizer.cs", "SwitchboardDocumentParser.cs"], parsers);
    }

    [Fact]
    public void Prices_reach_dispatch_only_through_the_control_inputs_and_dispatch_input_not_through_protection()
    {
        // Why: SIG-04 by type. The price view is a field of the store cycle, of the control inputs and of the dispatch input; the protection
        // inputs, the dispatch inputs (power and voltage) and the guard's own input and step carry none.
        Assert.Contains(typeof(PriceInputs), typeof(Switchboard.Application.Signals.ControlInputs).GetProperties().Select(p => p.PropertyType));
        Assert.Contains(typeof(PriceInputs), typeof(Switchboard.Application.Dispatch.DispatchInput).GetProperties().Select(p => p.PropertyType));
        Assert.DoesNotContain(SignalArchitectureTests.TypeGraph(typeof(Switchboard.Domain.Signals.ProtectionInputs)), t => t == typeof(PriceInputs));
        Assert.DoesNotContain(SignalArchitectureTests.TypeGraph(typeof(Switchboard.Domain.Signals.DispatchInputs)), t => t == typeof(PriceInputs));
        Assert.DoesNotContain(SignalArchitectureTests.TypeGraph(typeof(Switchboard.Application.Guard.GuardInput)), IsEconomic);
    }

    [Fact]
    public void The_domain_economics_module_references_only_the_bcl_and_the_domain()
    {
        // Pure: no Application, no Infrastructure, no framework package.
        var references = Domain.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain(references, r => r.StartsWith("Switchboard.", StringComparison.Ordinal) || r.StartsWith("Microsoft.", StringComparison.Ordinal));
    }

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.GetFiles(RepoPaths.Combine(root.Split('/')), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [GeneratedRegex("\"(?:[^\"\\\\]|\\\\.)*\"")]
    private static partial Regex Literal();

    [GeneratedRegex("[€£¥₹₩₽₺]|\\bzł\\b|\\b(PLN|EUR|USD|GBP|CHF|CZK|SEK|NOK|DKK)\\b")]
    private static partial Regex Currency();

    [GeneratedRegex("[€£¥₹₩₽₺]|zł")]
    private static partial Regex Symbols();

    [GeneratedRegex(@"StartOrder|ValueOrder|OrderKey|StableAdoption", RegexOptions.CultureInvariant)]
    private static partial Regex LiveOrderWord();

    [GeneratedRegex(@"Economics|PriceBook|PricePoint|PriceSpec|PriceInputs|PriceSample|PriceValue|DynamicPrice|Tariff|ISettlementModel|SettlementModel|EffectiveValue|SourceSet|Money", RegexOptions.CultureInvariant)]
    private static partial Regex PriceWord();

    [GeneratedRegex(@"\b(sensor|switch|number|input_number|input_boolean|binary_sensor|select|input_select|climate|sun|weather)\.[a-z0-9_]+")]
    private static partial Regex EntityLiteral();
}
