# Pitfalls

Each of these cost real debugging time on the predecessor system or is a known trap of the platform. They are requirements in disguise.

## Metering

**`last_changed` is not `last_reported`.** When a value repeats, Home Assistant does not emit a state change. Freshness computed from `last_changed` makes a perfectly healthy constant signal look stale, and makes a frozen sensor look fresh for as long as its last value happens to repeat. Always use `last_reported`.

**Differential sensors lie periodically.** The predecessor's house-load sensor was computed by someone else as production minus consumption from two sources sampled out of step. It produced periodic ~4.5 kW dips — exactly the combined power of the three heater elements, which made them look like a device fault for weeks. Never consume a derived sensor for threshold logic; derive from primary measurements inside SignalStore.

**Unsigned current hides direction.** Some meters report current without sign, so 16 A can mean import or export. Without a signed power entity the system cannot tell, and during a large export it will start shedding loads for no reason. Hence SIG-03 and the cautious mode.

**Current and power are not interchangeable.** The breaker responds to current; economics are counted in watts. Deriving power from current times voltage overstates it for loads with a poor power factor. Use current for protection, power for dispatch, never the reverse.

**The meter sees net flow.** With local generation, the measured phase current is the difference between load and production on that phase — and that is exactly what flows through the breaker, so protection needs nothing more. Dispatch does: without the inverter signal it cannot tell an idle phase from one whose 3 kW load is covered by production.

**Swapped phase assignment is the worst misconfiguration.** The system then sheds load on the phase that is not overloaded and trips the breaker it was installed to protect. Only an active test catches it; no amount of validation of values will.

## Control

**Chattering.** Without hysteresis a device oscillates around any threshold. Separate start and stop thresholds, start strictly higher.

**Shutdown avalanche.** Recomputing the target number of running stages each cycle without a rate limit produces all-off then all-on. Limit the change to one stage per cycle.

**Priority flapping.** Re-ranking phases by voltage every cycle switches devices between phases on noise. Require N consecutive agreeing cycles.

**Relay wear.** Contactors switching every cycle fail. Asymmetric lockouts (long on, short off) proved right in practice.

**Unreachable states.** A target that is mathematically reachable may be unreachable in practice because the arrival condition is evaluated before the credit accumulates. The predecessor needed a balance credit term to make a two-stage state reachable at all. Expect this class of bug and test for reachability, not just safety.

**Thermostats break the model.** A heater element commanded on draws nothing when its thermostat has opened. Without effector verification the system keeps "spending" 1.5 kW that never flows, and every downstream decision is wrong.

**Deferred intent.** Desired state must live separately from applied state, with a tick loop that applies pending changes once a lockout expires. Otherwise commands issued during a lockout are silently lost.

## Platform

**Add-ons need the Supervisor.** HA Container and Core users cannot install one. Decide early whether the standalone Docker path is supported, and say so in the store description.

**Riemann integral sensors go stale.** Integration sensors need `max_sub_interval`, or they stop accumulating when the source value is constant.

**Energy dashboard pickers need `state_class: measurement`.** An entity without it simply does not appear, with no error.

**MQTT discovery needs stable unique IDs.** Changing the ID scheme later orphans every entity the user has put on dashboards. Decide the scheme before the first release and treat it as an API.

**Published entities must be retracted.** Removing a device from configuration without retracting its discovery topic leaves a permanently unavailable entity in the user's instance.

**The ingress panel cannot read the HA theme.** Follow `prefers-color-scheme` and offer an explicit toggle.

**Daylight saving.** Hourly accounting on 23- and 25-hour days breaks naive implementations. Store `DateTimeOffset`, convert at the edge.

## Product

**Shadow mode is not optional.** Software that switches 5 kW loads in strangers' houses cannot default to acting. It also gives a week of free field data per user.

**The reserve is not the largest appliance.** It is tempting to tell users to reserve the kettle's full power. The breaker curve means they do not need to, and the smaller reserve is the difference between charging a car and not. Explain the arithmetic in the UI rather than hiding it behind a default.

**Never command an undeclared entity.** Auto-discovery of "things that look controllable" is a feature request that must be refused. Discovery may suggest; only the user declares.

**Do not promise protection.** Say in onboarding and in the store description that this is best-effort load management from HA data, not a protective device, and that it does not replace breakers or EVSE protection.
