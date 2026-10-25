using System.Reflection;
using System.Text.RegularExpressions;
using Switchboard.Application;
using Switchboard.Application.Signals;
using Switchboard.Domain.Signals;
using Switchboard.Domain.Units;
using Switchboard.Host.Composition;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Tests.Support;

namespace Switchboard.Tests;

/// <summary>
/// SIG-01 and SIG-04 as structure: Domain is pure, only the mapping layer knows entity IDs, and
/// protection and dispatch can only ever receive their own kind of input.
/// </summary>
public sealed partial class SignalArchitectureTests
{
    private static readonly Assembly Domain = typeof(SignalSnapshot).Assembly;

    private static readonly string[] Bcl = ["System.", "netstandard", "mscorlib"];

    [Fact]
    public void Domain_references_nothing_but_the_BCL()
    {
        var references = Domain.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.All(references, name => Assert.True(
            name == "System" || Bcl.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)),
            $"Domain references {name}."));
        Assert.DoesNotContain(references, name => name.StartsWith("System.Net", StringComparison.Ordinal));
    }

    [Fact]
    public void Domain_never_touches_a_clock_or_IO_types()
    {
        // Time arrives as values; a clock, a timer, a file or a socket in any signature would let
        // control logic run on wall time or perform I/O.
        var forbidden = new[] { typeof(TimeProvider), typeof(ITimer), typeof(Stream), typeof(FileSystemInfo), typeof(TextReader), typeof(TextWriter) };
        var offenders = Domain.GetTypes()
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .SelectMany(m => SignatureTypes(m).Select(t => (Member: m, Type: t)))
            .Where(x => forbidden.Any(f => f.IsAssignableFrom(x.Type)) || (x.Type.Namespace?.StartsWith("System.Net", StringComparison.Ordinal) ?? false))
            .Select(x => $"{x.Member.DeclaringType?.Name}.{x.Member.Name}: {x.Type.Name}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Application_does_not_reference_YamlDotNet()
    {
        // The YAML library stays in Infrastructure; validation works on ConfigNode trees.
        Assert.DoesNotContain(typeof(AppVersion).Assembly.GetReferencedAssemblies(), r => r.Name == "YamlDotNet");
    }

    [Fact]
    public void Source_contains_no_entity_id_literals()
    {
        // No core component references an entity ID (SIG-01): no entity-shaped string literal in
        // Domain or Application, and none of the maintainer's entities anywhere in src. Our own
        // published identities (<domain>.switchboard_*) are the only exception.
        var core = Files("src/Switchboard.Domain", "src/Switchboard.Application");
        var offenders = new List<string>();
        foreach (var file in core)
        {
            foreach (Match literal in StringLiteral().Matches(File.ReadAllText(file)))
            {
                foreach (Match entity in EntityIdShape().Matches(literal.Value))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {entity.Value}");
                }
            }
        }

        foreach (var file in Files("src"))
        {
            foreach (Match match in MaintainerEntity().Matches(File.ReadAllText(file)))
            {
                offenders.Add($"{Path.GetFileName(file)}: {match.Value}");
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Consumer_views_carry_no_entity_ids()
    {
        // DeviceName and GroupName are leaves: their text matches ^[a-z][a-z0-9_]{0,39}$ and can
        // never hold a '.', so neither can be an entity ID (GuardArchitectureTests proves the pattern).
        // OptionText (0.7.1, a select stop's option) is a leaf for another reason: it is compared and
        // logged only, and no command carries it (Stage3ArchitectureTests pins DeviceCommand), so even
        // an option that looked like an entity ID could never address one.
        foreach (var root in new[] { typeof(ProtectionInputs), typeof(DispatchInputs) })
        {
            var graph = TypeGraph(root);
            Assert.DoesNotContain(typeof(EntityId), graph);
            Assert.DoesNotContain(typeof(string), graph);
        }

        Assert.Contains(typeof(Switchboard.Domain.Devices.OptionText), TypeGraph(typeof(ProtectionInputs)));
        Assert.DoesNotContain(typeof(Switchboard.Domain.Devices.OptionText), TypeGraph(typeof(Switchboard.Application.Effector.DeviceCommand)));
    }

    [Fact]
    public void Protection_inputs_expose_no_watts_in_their_type_graph()
    {
        // Since Stage 2 the protection inputs carry the setup and device states too; still amperes
        // only, and no volts either, so protection cannot turn a current into watts (07-PITFALLS).
        var graph = TypeGraph(typeof(ProtectionInputs));

        Assert.Contains(typeof(Amperes), graph);
        Assert.Contains(typeof(Switchboard.Domain.Devices.DeviceSpec), graph);
        Assert.DoesNotContain(typeof(Watts), graph);
        Assert.DoesNotContain(typeof(Volts), graph);
        Assert.DoesNotContain(typeof(SignalValue), graph);
    }

    [Fact]
    public void Dispatch_inputs_expose_no_amperes_in_their_type_graph()
    {
        var graph = TypeGraph(typeof(DispatchInputs));

        Assert.Contains(typeof(Watts), graph);
        Assert.DoesNotContain(typeof(Amperes), graph);
        Assert.DoesNotContain(typeof(SignalValue), graph);
    }

    [Fact]
    public void Only_the_signal_layer_and_diagnostics_see_the_full_snapshot()
    {
        // The guard (Stage 2) must depend on IProtectionInputSource and dispatch on
        // IDispatchInputSource; anything else holding a SignalSnapshot could read watts in
        // protection or amperes in dispatch.
        var allowed = new[] { "Switchboard.Domain.Signals", "Switchboard.Application.Signals", "Switchboard.Host.Web", "Switchboard.Host.Composition" };
        var wide = new[] { typeof(SignalSnapshot), typeof(SignalReading), typeof(SignalDiagnostics), typeof(ISignalDiagnosticsSource), typeof(SignalSnapshotHolder) };
        var assemblies = new[] { Domain, typeof(AppVersion).Assembly, typeof(HomeAssistantClient).Assembly, typeof(SwitchboardServices).Assembly };

        var offenders = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => !allowed.Any(ns => t.Namespace?.StartsWith(ns, StringComparison.Ordinal) ?? false))
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .SelectMany(m => SignatureTypes(m).Where(s => wide.Contains(s)).Select(s => $"{t.FullName}.{m.Name}: {s.Name}")))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Only_the_state_reader_and_mqtt_credentials_send_a_token_over_http()
    {
        // The HA token may leave over HTTP in exactly two places: the GET-only state reader and the
        // Supervisor MQTT credential request. A third place would be a second, unguarded channel.
        var carriers = Files("src/Switchboard.Infrastructure", "src/Switchboard.Host")
            .Where(f => File.ReadAllText(f).Contains("HttpRequestMessage", StringComparison.Ordinal)
                        || File.ReadAllText(f).Contains("AuthenticationHeaderValue", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order()
            .ToList();

        Assert.Equal(["HomeAssistantStateReader.cs", "MqttCredentialSource.cs"], carriers);
    }

    private static List<string> Files(params string[] roots) =>
        [.. roots.SelectMany(r => Directory.GetFiles(RepoPaths.Combine(r.Split('/')), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];

    /// <summary>
    /// Every type reachable through public properties and generic arguments, staying inside our own
    /// assemblies. A device or group name and a select's option are leaves (see <see cref="Consumer_views_carry_no_entity_ids"/>).
    /// </summary>
    internal static HashSet<Type> TypeGraph(Type root)
    {
        var seen = new HashSet<Type>();
        var pending = new Stack<Type>([root]);
        while (pending.TryPop(out var type))
        {
            if (!seen.Add(type))
            {
                continue;
            }

            foreach (var argument in type.GetGenericArguments())
            {
                pending.Push(argument);
            }

            if (type.Assembly.GetName().Name?.StartsWith("Switchboard", StringComparison.Ordinal) != true
                || type == typeof(Switchboard.Domain.Devices.DeviceName)
                || type == typeof(Switchboard.Domain.Connection.GroupName)
                || type == typeof(Switchboard.Domain.Devices.OptionText))
            {
                continue;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                pending.Push(property.PropertyType);
            }
        }

        return seen;
    }

    private static IEnumerable<Type> SignatureTypes(MemberInfo member)
    {
        IEnumerable<Type> direct = member switch
        {
            MethodInfo method => method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType),
            PropertyInfo property => [property.PropertyType],
            FieldInfo field => [field.FieldType],
            ConstructorInfo constructor => constructor.GetParameters().Select(p => p.ParameterType),
            EventInfo @event => [@event.EventHandlerType!],
            _ => [],
        };
        return direct.SelectMany(Unwrap);
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type.IsByRef || type.IsArray ? type.GetElementType()! : type;
        foreach (var argument in type.GetGenericArguments().SelectMany(Unwrap))
        {
            yield return argument;
        }
    }

    [GeneratedRegex("\"(?:[^\"\\\\\\n]|\\\\.)*\"")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"\b(?:sensor|binary_sensor|switch|number|input_number|input_boolean|light|climate|select|button|water_heater|counter|fan|cover)\.(?!switchboard_)[a-z0-9_]+\b")]
    private static partial Regex EntityIdShape();

    [GeneratedRegex(@"\b[a-z_]+\.(?:solaredge_|goe_|grzalk|usage\b|zuzycie_|moc_grzalek)[a-z0-9_]*")]
    private static partial Regex MaintainerEntity();
}
