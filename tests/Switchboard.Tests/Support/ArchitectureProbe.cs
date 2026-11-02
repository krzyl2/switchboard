using System.Reflection;
using Switchboard.Application.Presentation;

namespace Switchboard.Tests.Support;

/// <summary>Shared scans of the architecture tests: source files under named roots and the types in member signatures.</summary>
internal static class ArchitectureProbe
{
    /// <summary>The types of the Application.Presentation namespace.</summary>
    public static readonly Type[] Presentation = [.. typeof(PresentationHub).Assembly.GetTypes().Where(t => t.Namespace == "Switchboard.Application.Presentation")];

    /// <summary>Every .cs file under the repo-relative roots ("src/Switchboard.Application"), never bin/ or obj/.</summary>
    public static IEnumerable<string> SourceFiles(params string[] roots) =>
        roots.SelectMany(r => Directory.GetFiles(RepoPaths.Combine(r.Split('/')), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>Field, property, constructor and method types of a type (methods declared only), generics unwrapped.</summary>
    public static IEnumerable<Type> Signatures(Type type) =>
        type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Select(f => f.FieldType)
            .Concat(type.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Select(p => p.PropertyType))
            .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType))
            .Concat(type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly).SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)))
            .SelectMany(Unwrap);

    /// <summary>Every member declared on the type itself, events included, generics unwrapped.</summary>
    public static IEnumerable<Type> DeclaredSignatures(Type type) =>
        type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .SelectMany(m => m switch
            {
                MethodInfo method => method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType),
                PropertyInfo property => [property.PropertyType],
                FieldInfo field => [field.FieldType],
                ConstructorInfo constructor => constructor.GetParameters().Select(p => p.ParameterType),
                EventInfo @event => [@event.EventHandlerType!],
                _ => [],
            })
            .SelectMany(Unwrap);

    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type.IsByRef || type.IsArray ? type.GetElementType()! : type;
        foreach (var argument in type.GetGenericArguments().SelectMany(Unwrap))
        {
            yield return argument;
        }
    }
}
