using System.Buffers;
using System.Text.Json;
using Switchboard.Application.Configuration;
using Switchboard.Application.Effector;
using Switchboard.Application.Signals;

namespace Switchboard.Infrastructure.HomeAssistant;

/// <summary>What became of one service call.</summary>
public sealed record CallOutcome(CommandOutcome Outcome, string? Detail);

/// <summary>
/// Where the command port's service calls go: the protection link to Home Assistant, or (standalone
/// development replay only) the development sink. Only the command port uses a channel.
/// </summary>
public interface ICallServiceChannel
{
    /// <summary>Calls can be sent now.</summary>
    bool Ready { get; }

    /// <summary>Grows by one each time a new link has delivered its initial states.</summary>
    long Generation { get; }

    /// <summary>Sends one call; refuses (throws <see cref="HomeAssistantCommandRefusedException"/>) unless the allow-list and <paramref name="isDeclared"/> accept it.</summary>
    Task<CallOutcome> SendAsync(ActuatorCall call, ActuatorDeclaredCheck isDeclared, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>The one serialization of a service call, shared by the client and the development sink.</summary>
public static class CallServicePayload
{
    public static ReadOnlyMemory<byte> Build(int id, ActuatorCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("type", HomeAssistantCommandAllowList.CallService);
            writer.WriteString("domain", call.Domain);
            writer.WriteString("service", HomeAssistantCommandAllowList.ServiceName(call.Service));
            if (call.Value is { } value)
            {
                writer.WriteStartObject("service_data");
                writer.WriteNumber("value", value);
                writer.WriteEndObject();
            }
            else if (call.Option is { } option)
            {
                writer.WriteStartObject("service_data");
                writer.WriteString("option", option);
                writer.WriteEndObject();
            }

            writer.WriteStartObject("target");
            writer.WriteString("entity_id", call.Entity.Value);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenMemory;
    }
}

/// <summary>
/// The protection link as a channel: bound by the link service to the client it runs, so service
/// calls ride the same WebSocket as the subscription (one connection to Home Assistant for control).
/// </summary>
public sealed class HomeAssistantCallServiceChannel : ICallServiceChannel
{
    private HomeAssistantClient? _client;

    public bool Ready => Volatile.Read(ref _client)?.Ready == true;

    public long Generation => Volatile.Read(ref _client)?.SessionGeneration ?? 0;

    public void Bind(HomeAssistantClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        Volatile.Write(ref _client, client);
    }

    public Task<CallOutcome> SendAsync(ActuatorCall call, ActuatorDeclaredCheck isDeclared, TimeSpan timeout, CancellationToken cancellationToken) =>
        Volatile.Read(ref _client) is { } client
            ? client.CallServiceAsync(call, isDeclared, timeout, cancellationToken)
            : Task.FromResult(new CallOutcome(CommandOutcome.NotSent, "Home Assistant is not connected"));
}
