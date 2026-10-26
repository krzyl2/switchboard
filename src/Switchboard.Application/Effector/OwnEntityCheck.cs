using Switchboard.Application.Signals;

namespace Switchboard.Application.Effector;

/// <summary>
/// Whether an entity is one of Switchboard's own MQTT entities (its override selects, its switches,
/// its sensors). Commanding one would loop back into Switchboard instead of reaching a device, so
/// the command path never sends to one, whatever the configuration declares. Answered from what the
/// add-on itself announced over MQTT discovery, by exact entity ID: an entity of another integration
/// with a similar name is not one of them.
/// </summary>
public interface IOwnEntityCheck
{
    bool IsOwn(EntityId entity);
}
