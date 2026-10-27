using System.Reflection;
using System.Text.RegularExpressions;
using Switchboard.Application;
using Switchboard.Domain.Forecast;
using Switchboard.Domain.Signals;
using Switchboard.Tests.Support;

namespace Switchboard.Tests;

/// <summary>
/// Forecasts are structurally apart from protection (15-PLANNER, "Inputs and their signal roles"; D2 to D4, CLAUDE.md): the guard runs on breaker
/// current alone, so no forecast, series or curve type may be reachable from it, and the core never names an entity (a forecast arrives through the
/// signal layer and nowhere else).
/// </summary>
public sealed partial class ForecastArchitectureTests
{
    private static readonly Assembly Domain = typeof(ForecastInputs).Assembly;
    private static readonly Assembly Application = typeof(AppVersion).Assembly;

    private static bool IsForecast(Type type) =>
        type.Namespace?.StartsWith("Switchboard.Domain.Forecast", StringComparison.Ordinal) == true
        || type == typeof(TimeSeries) || type == typeof(SeriesPoint) || type == typeof(SeriesValue) || type == typeof(SeriesValueKind)
        || type == typeof(EnergyForecastValue) || type == typeof(TimestampValue)
        || type.Name is "SeriesSpec" or "ParsedSeries" or "SeriesShape" or "SeriesProblem";

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

    private static IEnumerable<string> GuardSources() =>
        new[] { "Switchboard.Domain/Guard", "Switchboard.Application/Guard" }
            .SelectMany(dir => Directory.GetFiles(RepoPaths.Combine("src", dir), "*.cs", SearchOption.AllDirectories));

    [Fact]
    public void No_guard_type_has_a_forecast_or_series_type_in_its_signature()
    {
        // Why: protection must keep working on fresh breaker data alone; a forecast, a series or a curve in anything the guard holds, receives or
        // returns would be the first step to letting the weather move a limit.
        var guardTypes = Domain.GetTypes().Concat(Application.GetTypes())
            .Where(t => t.Namespace is { } ns && (ns.EndsWith(".Guard", StringComparison.Ordinal) || ns.Contains(".Guard.", StringComparison.Ordinal)))
            .ToList();

        var offenders = guardTypes.SelectMany(t => Signature(t).Where(IsForecast).Select(s => $"{t.FullName}: {s.Name}")).ToList();

        Assert.True(guardTypes.Count > 5, "the guard namespaces were not found");
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_guard_inputs_setup_and_step_reach_no_forecast_type()
    {
        Assert.DoesNotContain(SignalArchitectureTests.TypeGraph(typeof(ProtectionInputs)), IsForecast);
        Assert.DoesNotContain(SignalArchitectureTests.TypeGraph(typeof(Switchboard.Domain.Guard.ProtectionSetup)), IsForecast);
        Assert.DoesNotContain(SignalArchitectureTests.TypeGraph(typeof(Switchboard.Application.Guard.GuardInput)), IsForecast);
        Assert.DoesNotContain(SignalArchitectureTests.TypeGraph(typeof(DispatchInputs)), IsForecast);
        Assert.DoesNotContain(SignalArchitectureTests.TypeGraph(typeof(Switchboard.Application.Signals.ControlInputs)), IsForecast);
    }

    [Fact]
    public void The_guard_sources_never_mention_a_forecast_a_series_or_a_curve()
    {
        var files = GuardSources().ToList();
        var offenders = files.Where(f => ForecastWord().IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();

        Assert.NotEmpty(files);
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_scan_does_catch_a_forecast_word()
    {
        // The scan must be able to fail.
        Assert.Matches(ForecastWord(), "var x = ForecastInputs.Empty;");
        Assert.Matches(ForecastWord(), "TimeSeries series;");
        Assert.DoesNotMatch(ForecastWord(), "var envelope = Envelope.Of(limit);");
    }

    [Fact]
    public void The_forecast_code_names_no_entity_id()
    {
        // Why: the core never knows an entity ID; a forecast arrives through the signal layer. An entity literal in the domain module, the
        // normalizer or the endpoint would be vendor knowledge in the wrong place (Forecast.Solar, Solcast, Nord Pool specifics belong in configuration).
        var files = Directory.GetFiles(RepoPaths.Combine("src", "Switchboard.Domain", "Forecast"), "*.cs")
            .Append(RepoPaths.Combine("src", "Switchboard.Domain", "Signals", "TimeSeries.cs"))
            .Append(RepoPaths.Combine("src", "Switchboard.Application", "Signals", "SignalNormalizer.cs"))
            .Append(RepoPaths.Combine("src", "Switchboard.Infrastructure", "HomeAssistant", "SeriesAttributeParser.cs"))
            .ToList();
        var offenders = files.Where(f => EntityLiteral().IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();

        Assert.True(files.Count >= 4);
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_domain_forecast_module_references_only_the_bcl_and_the_domain()
    {
        var references = Domain.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain(references, r => r.StartsWith("Switchboard.", StringComparison.Ordinal) || r.StartsWith("Microsoft.", StringComparison.Ordinal));
    }

    [GeneratedRegex(@"Forecast|TimeSeries|SeriesPoint|SeriesValue|SeriesSpec|ParsedSeries|PriceCurve|PvForecast|SunRising|SunSetting|EnergyForecast", RegexOptions.CultureInvariant)]
    private static partial Regex ForecastWord();

    [GeneratedRegex(@"\b(sensor|switch|number|input_number|input_boolean|binary_sensor|select|input_select|climate|sun|weather)\.[a-z0-9_]+")]
    private static partial Regex EntityLiteral();
}
