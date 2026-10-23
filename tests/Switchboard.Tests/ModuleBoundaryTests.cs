using System.Reflection;
using Switchboard.Application;

namespace Switchboard.Tests;

/// <summary>
/// Application holds the policies (mode, health, heartbeat, backoff) and must stay free of I/O and
/// transport types, so the control logic that grows here in later stages remains testable on a
/// virtual clock and never reaches for a socket directly.
/// </summary>
public sealed class ModuleBoundaryTests
{
    private static readonly AssemblyName[] References = typeof(AppVersion).Assembly.GetReferencedAssemblies();

    [Theory]
    [InlineData("Switchboard.Infrastructure")]
    [InlineData("Switchboard.Host")]
    [InlineData("MQTTnet")]
    [InlineData("Microsoft.AspNetCore")]
    [InlineData("System.Net.WebSockets")]
    [InlineData("System.Net.Http")]
    [InlineData("System.Net.Sockets")]
    public void Application_does_not_reference(string forbidden)
    {
        Assert.DoesNotContain(References, r =>
            r.Name == forbidden || (r.Name?.StartsWith(forbidden + ".", StringComparison.Ordinal) ?? false));
    }

    [Fact]
    public void Application_exposes_no_transport_types_in_its_public_surface()
    {
        var forbiddenNamespaces = new[] { "System.Net.WebSockets", "MQTTnet", "Microsoft.AspNetCore" };
        var exported = typeof(AppVersion).Assembly.GetExportedTypes();

        var offenders = exported
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .SelectMany(MemberTypes)
            .Where(t => forbiddenNamespaces.Any(ns => t.Namespace?.StartsWith(ns, StringComparison.Ordinal) ?? false))
            .ToList();

        Assert.Empty(offenders);
    }

    private static IEnumerable<Type> MemberTypes(MemberInfo member) => member switch
    {
        MethodInfo method => method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType),
        PropertyInfo property => [property.PropertyType],
        FieldInfo field => [field.FieldType],
        ConstructorInfo constructor => constructor.GetParameters().Select(p => p.ParameterType),
        EventInfo @event => [@event.EventHandlerType!],
        _ => [],
    };
}
