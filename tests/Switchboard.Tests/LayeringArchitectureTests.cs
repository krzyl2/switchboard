using System.Reflection;
using Switchboard.Application;
using Switchboard.Application.Configuration;
using Switchboard.Application.Dispatch;
using Switchboard.Application.Effector;
using Switchboard.Application.Guard;
using Switchboard.Application.Presentation;
using Switchboard.Application.Setup;
using Switchboard.Application.Setup.SelfTest;
using Switchboard.Application.Signals;
using Switchboard.Domain.Devices;
using Switchboard.Domain.Dispatch;
using Switchboard.Domain.Signals;
using Switchboard.Infrastructure.HomeAssistant;
using Switchboard.Infrastructure.Mqtt;
using Switchboard.Infrastructure.Persistence;
using Switchboard.Tests.Support;
using static Switchboard.Tests.Support.ArchitectureProbe;

namespace Switchboard.Tests;

/// <summary>
/// Layering: which parts may see which. Persistence and the published entities cannot reach the Home
/// Assistant client; Application holds no file or stream; dispatch reads watts from its own port and
/// amperes only from the guard's envelope; the presentation (read-only consumer) never sees an entity
/// ID, the configuration or the full snapshot, and holds no stream or file; guard, dispatch and the
/// effector gate never read what the store publishes for the presentation; the setup code is
/// invisible to guard, dispatch, the effector gate and the presentation; the evidence recorder only
/// listens to the guard runner; and the presentation files live in the data folder.
/// </summary>
public sealed class LayeringArchitectureTests
{
    [Fact]
    public void Persistence_and_published_entities_cannot_reach_the_home_assistant_client()
    {
        // The decision log, state.json and the MQTT entities see decisions and status only; none of
        // them holds anything from the Home Assistant namespace.
        var types = new[] { typeof(JsonlDecisionLog), typeof(RuntimeStateFile), typeof(DecisionJson), typeof(GuardEntities), typeof(MqttLink) };
        var offenders = types
            .SelectMany(t => t.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Select(f => f.FieldType)
                .Concat(t.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType)))
            .Where(t => t.Namespace?.StartsWith("Switchboard.Infrastructure.HomeAssistant", StringComparison.Ordinal) == true)
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Application_has_no_file_or_stream_in_any_signature()
    {
        // The guard's thread never touches a disk: everything that writes is behind a port
        // (IDecisionRecorder, IRuntimeStateStore) implemented in Infrastructure and drained by
        // writer tasks there.
        var forbidden = new[] { typeof(Stream), typeof(FileSystemInfo), typeof(TextWriter), typeof(TextReader) };
        var offenders = typeof(AppVersion).Assembly.GetTypes()
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .SelectMany(m => MemberSignature(m).Where(s => forbidden.Any(f => f.IsAssignableFrom(s))).Select(s => $"{t.Name}.{m.Name}: {s.Name}")))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Dispatch_reads_dispatch_port_and_guard_envelope_only()
    {
        // SIG-04: dispatch never touches protection inputs or device currents of its own; amperes
        // reach it only through the guard's envelope (and its setup's hysteresis).
        var forbidden = new[] { typeof(ProtectionInputs), typeof(DeviceDrawSample), typeof(IProtectionInputSource), typeof(PhaseProtection), typeof(CurrentReading) };
        foreach (var type in new[] { typeof(DispatchEngine), typeof(Allocator), typeof(ForeignLoadTracker) })
        {
            var signatures = type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Select(f => f.FieldType)
                .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType))
                .Concat(type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly).SelectMany(m => m.GetParameters()).Select(p => p.ParameterType));
            Assert.DoesNotContain(signatures, forbidden.Contains);
        }

        var input = SignalArchitectureTests.TypeGraph(typeof(DispatchInput));
        Assert.DoesNotContain(typeof(ProtectionInputs), input);
        Assert.DoesNotContain(typeof(DeviceDrawSample), input);
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

    [Fact]
    public void Setup_is_invisible_to_guard_dispatch_effector_and_presentation()
    {
        var consumers = typeof(AppVersion).Assembly.GetTypes()
            .Where(t => t.Namespace is "Switchboard.Application.Guard" or "Switchboard.Application.Dispatch" or "Switchboard.Application.Effector" or "Switchboard.Application.Presentation")
            .ToList();
        var offenders = consumers
            .SelectMany(t => DeclaredSignatures(t).Where(s => s.Namespace?.StartsWith("Switchboard.Application.Setup", StringComparison.Ordinal) == true).Select(s => $"{t.Name}: {s.Name}"))
            .ToList();

        Assert.NotEmpty(consumers);
        Assert.Empty(offenders);
        foreach (var file in SourceFiles("src/Switchboard.Application/Guard", "src/Switchboard.Application/Dispatch", "src/Switchboard.Application/Effector", "src/Switchboard.Application/Presentation"))
        {
            Assert.DoesNotContain("Switchboard.Application.Setup", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_evidence_recorder_only_listens_to_the_guard_runner()
    {
        // Like the presentation hub: it reads the step it is handed and the narrow runtime view, and
        // has no way to hand anything back to the control loop. Stage C adds a read-only question to
        // the active self-test (was this transition its own?), which answers a bool and nothing more.
        var parameters = typeof(PhaseEvidenceRecorder).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToList();
        Assert.Equal([typeof(PhaseEvidenceStore), typeof(ISetupRuntimeView), typeof(SetupDefaults), typeof(int), typeof(IActiveTestTransitions)], parameters);
        Assert.Equal([typeof(bool)], typeof(IActiveTestTransitions).GetMethods().Select(m => m.ReturnType));
    }

    private static IEnumerable<Type> MemberSignature(MemberInfo member) => member switch
    {
        MethodInfo method => method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType),
        PropertyInfo property => [property.PropertyType],
        FieldInfo field => [field.FieldType],
        ConstructorInfo constructor => constructor.GetParameters().Select(p => p.ParameterType),
        _ => [],
    };
}
