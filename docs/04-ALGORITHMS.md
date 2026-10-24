# Algorithms

Everything here is parameterized. No constant in this document may appear as a literal in code.

## 1. Breaker thermal model

### Why a single limit is wrong

A miniature circuit breaker is a thermal device. IEC 60898 defines two reference points for characteristic B at rated current `In`:

- **1.13 × In** — the breaker must *not* trip within the conventional time (1 hour for `In ≤ 63 A`).
- **1.45 × In** — the breaker *must* trip within that same hour.

Between them, manufacturers publish only a band. So:

- Treating `In` as a hard ceiling makes the system shed a load every time a kettle starts, for no reason — the hardware tolerates it comfortably.
- Treating `1.45 × In` as "allowed headroom" is dangerous, because that is the point at which tripping is guaranteed, with the time unknown.

The usable rule: **1.13 × In is the continuous-safety boundary; anything above it is a debt that must be repaid quickly.**

Worked example for the reference installation (3 × 25 A, 230 V):

```
In              = 25 A        → 5.75 kW per phase
continuous safe = 28.25 A     → 6.50 kW per phase
reserve 2.5 kW  → the system allocates at most 3.25 kW per phase to its own loads
kettle 2 kW on a fully allocated phase → 27.8 A = 1.11 × In → inside the non-tripping band
```

The consequence is counter-intuitive and worth stating in the UI: **the reserve may be smaller than the largest uncontrolled load**, because the load does not have to fit under the limit — it has to fit under the curve.

### Tier table (defaults, configurable)

| Multiple of In | Allowed duration | Reaction |
|---|---|---|
| ≤ 0.90 | unlimited | may add load |
| 0.90 – 1.00 | unlimited | hold, add nothing |
| 1.00 – 1.13 | 15 min | reduce during the ordinary cycle |
| 1.13 – 1.25 | 3 min | reduce next cycle |
| 1.25 – 1.45 | 30 s | shed one device immediately |
| > 1.45 | 0 s | shed everything controlled on that phase |

Characteristic C shifts the whole table up; "unknown" collapses the continuous threshold to 1.00 × In.

### Budget with thermal memory

A bimetal cools slowly, so three 30-second excursions within a quarter of an hour are not equivalent to one. Per phase, and per circuit group:

```
budget ∈ [0, 1], starts at 1

while I > I_continuous:
    budget -= dt / allowed_duration(I / In)

while I ≤ I_continuous:
    budget += dt / recovery_time        # recovery_time default 600 s, longer than the worst tier
    budget = min(budget, 1)
```

Thresholds on the budget, not only on the instantaneous current:

- `budget < 0.5` → stop allowing increases on that phase
- `budget < 0.25` → shed one device even if the current is currently legal
- `budget ≤ 0` → treat as the top tier

This is what prevents a slow failure in a house where someone cooks while the car charges.

## 2. Guard loop

Runs on every new sample of any phase current. Decision order, stop at the first match:

1. **Any phase signal stale** → safe state (all controlled off on that phase; if the meter as a whole is stale, everywhere).
2. **I > tier threshold** → shed per the tier's reaction.
3. **budget below its thresholds** → shed as above.
4. **I > reserve threshold** (`In − reserve`) → request a reduction from Dispatch; Dispatch chooses which device.
5. Otherwise publish the headroom envelope per phase and do nothing.

Guard only ever reduces. It never starts a device. That asymmetry is what makes it safe to run it fast on noisy data.

### Shedding order

Within the phase that is over its limit, ordered by ascending value per kWh, then by descending shed speed:

```
shed_order = sort(devices_on_phase, key = (value_per_kwh, -shed_speed))
```

Modulating devices are first reduced to their minimum, and only stopped if that is not enough. A device whose lockout forbids stopping is skipped, and the next one is taken — lockouts never outrank protection, but a device that physically cannot stop (compressor in a protected cycle) is declared `no_shed` and excluded.

Crucially: only devices **on the overloaded phase** are candidates. Energy cannot be moved between phases; only between devices on the same phase.

## 3. Dispatch loop

```
1. take snapshot; mark stale signals
2. if any fail-safe condition → safe state, end cycle
3. compute available headroom per phase:
      headroom = min(In − reserve, breaker_tier_allowance) − measured_phase_current
      (minus the same for the device's circuit group, if any)
4. if a settling window is open → work from the model estimate; allow reductions only
5. rank devices by value per kWh among those whose goals and guards permit running
6. allocate power top-down: each device gets min(its max, remaining headroom on its phase)
7. apply hysteresis, lockouts, max change per cycle, max simultaneous increases
8. emit commands; open settling windows; record decisions with reasons
```

Hysteresis uses separate thresholds for starting and stopping, with the start threshold strictly higher, so a device never oscillates around a boundary.

### Settling window

```
on command to device d with expected ΔP:
    t_cmd       = now
    window_open = true
    required    = t_cmd + d.reaction_time

window closes when every balance signal has last_reported > required
hard timeout: max_settling (default 90 s) → close anyway, flag the signal set
```

While open:

```
estimate(signal) = last_value_before(t_cmd) + Σ ΔP(all open commands affecting it)
```

On close, verification:

```
measured_delta = value_now − value_before
if |measured_delta − expected_ΔP| > tolerance:
    mismatch_count += 1
    if mismatch_count ≥ N:  device.state = unresponsive
else:
    mismatch_count = 0
```

Why this matters beyond data quality: a water-heater element whose thermostat has opened draws nothing when commanded on. Without verification the system believes it is dumping 1.5 kW into the tank and makes every subsequent decision on a false premise. With verification it learns the tank is hot.

### Simultaneous increases

Default: one increase per settling window. With two simultaneous increases and a missing power rise, the mismatch cannot be attributed to a device. Decreases are unlimited — attribution does not matter when the direction is safe.

## 4. Foreign load detection

A foreign load is any power that appears without a command of ours:

```
foreign_power(phase) = measured_power(phase)
                     − Σ estimated_power(our devices on that phase)
```

Tracked per phase as a slow-moving baseline plus a fast residual. A step in the residual beyond a threshold is a foreign load event. It is not an error — it is the thing the reserve exists for. The UI shows it in its own colour so the user can tell "the system is doing this" from "the house is doing this".

## 5. Phase priority for multi-element loads

Where several identical elements sit on different phases (three heater elements, one per phase), the choice of which to run is free and should relieve the most loaded phase first:

```
rank = sort(phases, key = descending voltage)      # or ascending current
```

A new ranking is only adopted after `N` consecutive cycles agreeing (default 3, i.e. 90 s), to stop the order flapping when a kettle switches on somewhere. Carried over from the Node-RED predecessor, where this exact problem appeared.

## 6. Reason codes

Every decision carries one. The list is closed and versioned; the UI renders a human sentence per code.

**Version 2** (2026-10-24): added `headroom_available` (a start, increase or restore permitted by headroom and rank — v1 had no code for the positive case) and `kill_switch` (SAFE-03, distinct from `safe_state` so the log says who stopped everything).

```
guard_tier_exceeded         guard_budget_low          guard_stale_signal
reserve_threshold           headroom_insufficient     lockout_min_on
lockout_min_off             settling_window_open      awaiting_confirmation
value_rank_below_cutoff     goal_met                  guard_expression_false
device_unresponsive         manual_override           mode_shadow
safe_state                  plan_slot_target          headroom_available
kill_switch
```

## 7. Default parameters

Carried over from the working Node-RED flow; they are starting values, not truths. The complete parameter list, including signal and reserve-derivation parameters, is the embedded `control-defaults.yaml` (`src/Switchboard.Infrastructure/Configuration/`).

| Parameter | Default | Origin |
|---|---|---|
| start hysteresis | +0.30 kW | chattering around the threshold |
| stop hysteresis | −0.20 kW | as above |
| relay min on | 6 min | contact life |
| relay min off | 90 s | contact life |
| max step change | ±1 stage per cycle | avalanche of simultaneous stops |
| phase ranking stability | 3 cycles | priority flapping |
| balance credit | 0.25 kWh | a reachable state was unreachable without it |
| persistence up / down | 6 / 2 cycles | asymmetric safety |
| settling hard timeout | 90 s | — |
| overload recovery | 600 s | longer than the worst tier |
| default reserve | 2.5 kW per phase | one kettle plus margin |
