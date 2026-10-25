using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Switchboard.Application;
using Switchboard.Application.Configuration;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Presentation;
using Switchboard.Application.Signals;
using Switchboard.Domain.Signals;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Infrastructure.Persistence;
using Switchboard.Tests.Host;
using Switchboard.Tests.Support;

namespace Switchboard.Tests;

/// <summary>
/// The runtime UI is a read-only consumer, and that is structural: the presentation types hold no
/// command port, no kill switch and no override control; they never see an entity ID, the
/// configuration or the full signal snapshot; protection, dispatch and the effector gate never read
/// what the store publishes for the presentation; every new endpoint answers GET only; the Home
/// Assistant allow-list is unchanged; and the presentation files live in the data folder.
/// </summary>
public sealed class Stage4ArchitectureTests
{
    private static readonly Type[] Presentation = [.. typeof(PresentationHub).Assembly.GetTypes().Where(t => t.Namespace == "Switchboard.Application.Presentation")];

    [Fact]
    public void Presentation_references_no_command_port_kill_switch_or_override_control()
    {
        var forbidden = new[] { typeof(IDeviceCommandPort), typeof(IKillSwitchControl), typeof(KillSwitchState), typeof(IDeviceOverrideControl), typeof(DeviceOverrides), typeof(EffectorGate) };
        var offenders = Presentation
            .SelectMany(t => Signatures(t).Where(s => forbidden.Any(f => f.IsAssignableFrom(s))).Select(s => $"{t.Name}: {s.Name}"))
            .ToList();

        Assert.NotEmpty(Presentation);
        Assert.Empty(offenders);
    }

    [Fact]
    public void Presentation_sees_no_entity_id_configuration_or_full_snapshot()
    {
        // Entity IDs live in the signal and configuration layer only (SIG-01); the presentation reads
        // the control step and the entity-free presentation inputs of the same store cycle.
        var forbidden = new[] { typeof(EntityId), typeof(SwitchboardConfiguration), typeof(SignalSnapshot), typeof(SignalDiagnostics), typeof(ISignalDiagnosticsSource) };
        foreach (var type in Presentation)
        {
            var graph = SignalArchitectureTests.TypeGraph(type);
            Assert.DoesNotContain(graph, forbidden.Contains);
            Assert.DoesNotContain(Signatures(type), forbidden.Contains);
        }

        Assert.DoesNotContain(typeof(EntityId), SignalArchitectureTests.TypeGraph(typeof(PresentationInputs)));
    }

    [Fact]
    public void Guard_dispatch_and_gate_never_read_the_presentation_inputs()
    {
        // The store publishes the nominal voltage and display names beside the control inputs for the
        // UI only; a control decision built on them would bypass SIG-04.
        foreach (var type in new[] { typeof(GuardEngine), typeof(DispatchEngine), typeof(EffectorGate), typeof(ForeignLoadTracker) })
        {
            Assert.DoesNotContain(typeof(PresentationInputs), Signatures(type));
        }

        foreach (var file in SourceFiles("src/Switchboard.Application/Guard", "src/Switchboard.Application/Dispatch", "src/Switchboard.Application/Effector"))
        {
            Assert.DoesNotContain(".Presentation", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Allow_list_holds_only_the_live_types_and_the_read_only_queries()
    {
        // The runtime UI added nothing; Stage 5 added read-only discovery queries, Stage C call_service.
        Assert.Equal(["call_service", "config/device_registry/list", "config/entity_registry/list", "get_states", "ping", "subscribe_entities", "unsubscribe_events"], HomeAssistantCommandAllowList.Allowed.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("/api/power")]
    [InlineData("/api/statistics")]
    [InlineData("/api/excursions")]
    [InlineData("/api/devices")]
    public async Task New_endpoints_are_get_only(string path)
    {
        using var root = new TempWebRoot();
        using var factory = new SwitchboardHostFactory(root.Path);

        var get = await PresentationEndpointTests.SendAsync(factory, "GET", path);
        Assert.Equal(StatusCodes.Status200OK, get.Response.StatusCode);
        foreach (var method in new[] { "POST", "PUT", "PATCH", "DELETE" })
        {
            var context = await PresentationEndpointTests.SendAsync(factory, method, path);
            Assert.False(context.Response.StatusCode is >= 200 and < 300, $"{method} {path} answered {context.Response.StatusCode}");
        }
    }

    [Fact]
    public void Presentation_files_live_under_the_data_directory()
    {
        var data = Path.Combine(Path.GetTempPath(), "switchboard-data");
        var options = TestStatistics.Options();
        var statistics = new StatisticsFile(data, options, Microsoft.Extensions.Logging.Abstractions.NullLogger<StatisticsFile>.Instance);
        var excursions = new ExcursionFile(data, options, Microsoft.Extensions.Logging.Abstractions.NullLogger<ExcursionFile>.Instance);

        Assert.Equal(Path.Combine(data, "statistics.json"), statistics.FilePath);
        Assert.Equal(Path.Combine(data, "excursions.json"), excursions.FilePath);
    }

    [Fact]
    public void Presentation_holds_no_stream_or_file_and_never_waits_on_the_guard_thread()
    {
        // The hub only signals the writer through a one-slot channel; serialisation and disk access
        // are behind IPresentationStore in Infrastructure.
        var forbidden = new[] { typeof(Stream), typeof(FileSystemInfo), typeof(TextWriter), typeof(TextReader) };
        Assert.DoesNotContain(Presentation.SelectMany(Signatures), s => forbidden.Any(f => f.IsAssignableFrom(s)));
        Assert.Equal(typeof(AppVersion).Assembly, typeof(PresentationHub).Assembly);
    }

    private static IEnumerable<Type> Signatures(Type type) =>
        type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Select(f => f.FieldType)
            .Concat(type.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Select(p => p.PropertyType))
            .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType))
            .Concat(type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly).SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)))
            .SelectMany(Unwrap);

    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type.IsByRef || type.IsArray ? type.GetElementType()! : type;
        foreach (var argument in type.GetGenericArguments().SelectMany(Unwrap))
        {
            yield return argument;
        }
    }

    private static IEnumerable<string> SourceFiles(params string[] roots) =>
        roots.SelectMany(r => Directory.GetFiles(RepoPaths.Combine(r.Split('/')), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
}
