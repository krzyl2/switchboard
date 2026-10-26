using Switchboard.Application.Effector;
using Switchboard.Application.Signals;
using Switchboard.Application.State;

namespace Switchboard.Infrastructure.Mqtt;

/// <summary>
/// Switchboard's own MQTT entities, known from what the add-on announced through MQTT discovery:
/// the discovery ledger (every object announced, also before a restart, so the answer holds while
/// the broker link is still down) and the watchdog entity. An entity ID is own only when it is
/// exactly the default entity ID of one of those objects (<see cref="MqttIdentity.DefaultEntityId"/>):
/// a name that merely starts like one, or the same object name under another domain, is not.
/// </summary>
public sealed class OwnMqttEntities(IDiscoveryLedger ledger) : IOwnEntityCheck
{
    private static readonly string HealthEntity = MqttIdentity.DefaultEntityId(HealthEntityDiscovery.Component, HealthEntityDiscovery.ObjectId);

    public bool IsOwn(EntityId entity)
    {
        if (string.Equals(entity.Value, HealthEntity, StringComparison.Ordinal))
        {
            return true;
        }

        return MqttIdentity.LedgerKeyOf(entity.Value) is { } key && ledger.Discovered.Contains(key);
    }
}
