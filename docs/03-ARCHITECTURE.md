# Architecture

## Three loops

Separating these is the central structural decision. Each has a different period, a different input freshness requirement and a different authority.

| Loop | Period | Inputs | Authority | Can be slow? |
|---|---|---|---|---|
| **Guard** | fastest sample available (1–30 s) | per-phase current, device states | may shed anything, immediately | no |
| **Dispatch** | 10–30 s | all signals, device goals, headroom from Guard | may start, modulate and stop devices within the headroom Guard leaves | yes |
| **Planner** (v3.0) | 15 min | forecasts, prices, goals, device state | sets targets per time slot; Dispatch executes | yes |

Guard never asks Dispatch for permission. Dispatch never exceeds the envelope Guard publishes. Planner only produces intent.

```
        HA WebSocket (state_changed, push)
                 │
          ┌──────▼───────┐
          │ SignalStore  │  normalization, freshness, sign, balance check
          └──────┬───────┘
                 │ snapshot
    ┌────────────┼───────────────┐
    │            │               │
┌───▼────┐  ┌────▼─────┐   ┌─────▼──────┐
│ Guard  │  │ Dispatch │   │  Planner   │  (v3.0)
└───┬────┘  └────┬─────┘   └─────┬──────┘
    │ envelope   │ commands      │ targets
    └────────────┼───────────────┘
                 │
          ┌──────▼───────┐
          │ EffectorGate │  lockouts, settling windows, ΔP verification
          └──────┬───────┘
                 │
      HA service calls          MQTT discovery (published entities)
```

## SignalStore

The only component that knows about Home Assistant entities. Everything above it works on logical signals.

Signal definition:

```yaml
signals:
  phase_current_l1:
    entity: sensor.meter_l1_current
    kind: current           # current | power | energy_counter | state | percentage
    unit: A
    sign: positive_import   # positive_import | positive_export | unsigned
    direction_from: sensor.meter_l1_power   # required when sign = unsigned
    max_age: 120s
    sigma: 0.2
```

Responsibilities:

1. Subscribe once via `subscribe_entities`; never poll.
2. Normalize units and sign to the internal convention (import positive, watts, amperes).
3. Stamp every sample with the HA `last_reported` time, not arrival time.
4. Mark a signal `stale` past `max_age`, and publish that state.
5. Run the balance residual check and reject implausible samples.
6. Expose an immutable snapshot per cycle — no consumer ever reads a signal mid-change.

Derived signals (house load per phase, surplus, total controlled power) are computed here from primary measurements, never read from somebody else's template sensor. The predecessor's biggest data bug came from exactly that (see `07-PITFALLS.md`).

## Device model

Three layers, so that the common case needs no code and the rare case is still possible.

**Layer 1 — type.** Built-in physical models: `onoff`, `stepped`, `modulating`, `storage`. The type determines what commands exist and how power is estimated.

**Layer 2 — form fields.** Identical for every type:

```yaml
devices:
  - id: ev
    type: modulating
    phase: L1                 # L1 | L2 | L3 | three_phase
    circuit_group: carport    # optional sub-breaker group
    actuator: number.go_e_charger_current
    power_min: 1380
    power_max: 3680
    reaction_time: 20s
    min_on: 60s
    min_off: 60s
    value_per_kwh: 2.03       # fixed value or a Jinja template
    sources: [any]            # v2.0: pv_only | pv_and_zone:night | any
    goal:
      kind: soc               # soc | energy | none
      target: 50
      deadline: "06:30"
      hard: true
    guards:
      - "{{ states('binary_sensor.ev_connected') == 'on' }}"
```

**Layer 3 — Jinja templates.** Any numeric or boolean field accepts a template evaluated by Home Assistant, not by us. This covers conditional values, temperature guards and schedule-dependent goals without an embedded interpreter (decision D9).

## Circuit groups

A group is a named set of devices behind a shared downstream breaker, with its own per-phase limit and its own thermal budget. Guard evaluates the main connection and every group; the binding constraint is whichever is tightest. A device belongs to at most one group.

## EffectorGate

Every command passes through here. It owns:

- **Lockouts** — minimum on/off per device, counted from the actual command time, restarted at process start.
- **Settling windows** — opened on each command with the device's `reaction_time`; closed when every balance signal has a newer sample; hard timeout configurable.
- **In-flight model** — `estimate = last_measurement + Σ ΔP(open commands)`, used by Dispatch while a window is open.
- **Verification** — on window close, compare measured Δ with expected ΔP. Mismatch beyond tolerance increments a counter; N consecutive mismatches mark the device `unresponsive` and exclude it from dispatch until it recovers.
- **Deferred intent** — desired state is stored separately from applied state; a tick loop applies whatever is pending once a lockout expires. This is the reconciliation pattern carried over from the Node-RED predecessor.
- **Asymmetry** — reductions bypass the window; increases do not.

## Published entities (MQTT discovery)

```
sensor.switchboard_l1_headroom           # A, to the reserve threshold
sensor.switchboard_l1_breaker_headroom   # A, to the trip threshold
sensor.switchboard_l1_overload_budget    # %
sensor.switchboard_mode                  # shadow | active
sensor.switchboard_<device>_state        # state + reason as an attribute
binary_sensor.switchboard_healthy        # watchdog heartbeat
sensor.switchboard_last_decision         # reason code
```

The health entity exists so that an HA-side automation can shed loads if the add-on dies. That automation is documented, not installed by us.

## Module boundaries

```
Domain          — signals, devices, phases, limits, thermal budget, decisions.
                  No I/O, no HA types, no clock. Clock is injected.
Application     — Guard, Dispatch, EffectorGate, configuration, mode handling.
Infrastructure  — HA WebSocket/REST client, MQTT publisher, persistence.
Web             — ingress API + SPA.
Host            — composition, Supervisor token, lifecycle, shutdown to safe state.
```

Rules that matter:

- Domain code never sees an entity ID or a `DateTime.Now`.
- Time is `DateTimeOffset` throughout; local time only at the presentation edge. Daylight-saving transitions produce 23- and 25-hour days — the planner must tolerate them.
- Energy in kWh, power in W, current in A. Conversion happens in SignalStore only.
- Persisted state after restart: device applied states, lockout start times, overload budgets, mode. All timers restart from the restart instant.
