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

## 8. Fail-safe table

Every row has a named test (VER-03). In shadow mode a reaction is a recorded "would switch off"; nothing is commanded.

| Condition | Detection | Reaction (shadow: recorded 'would', never commanded) | Reason code | Recovery | Tests |
|---|---|---|---|---|---|
| Phase current stale / unavailable / missing / rejected (one phase) | age ≥ max_age from last_reported, unavailable/unknown state, entity missing, refused sample → PhaseProtection.DataValid=false | every controlled device with a stage on that phase held off; budget holds, band timers pause; phase safe state recorded | guard_stale_signal | data valid → safe_state_exited; holds released after persistence_up_cycles (6) good boundaries counted from the last hold cause | GuardSafeStateTests.Stale_phase_current_only_that_phase, Invalid_phase_reason_maps_to_its_code, Safe_state_exit_releases_only_after_persistence_up, One_phase_blip_between_boundaries_restarts_release_persistence; GuardSyntheticReplayTests.Frozen_phase_current_is_safe_state_90_seconds_after_its_last_report |
| Whole meter stale (no valid phase) | every phase invalid | every controlled device held off; system safe state, cause meter_invalid | guard_stale_signal (safe_state while currents are still awaited) | as above | GuardSafeStateTests.Whole_meter_stale_everywhere, System_safe_state_names_its_cause, Outage_between_boundaries_restarts_release_persistence |
| HA disconnected | source status not connected → phases and device states source_disconnected | everything held off; cause source_disconnected | safe_state | reconnect → currents awaiting (still safe state) → valid → release after 6 good boundaries | GuardSafeStateTests.HA_disconnection_everywhere, HA_reconnect_awaiting_is_safe_state_and_releases_by_streak; DeviceStateSignalTests.Disconnected_source_leaves_every_device_state_unknown |
| MQTT disconnected | MQTT link not connected | guard unaffected; retained LWT 'offline' on switchboard/availability; health false → health entity OFF/unavailable; kill switch settable via API only | none (no guard decision) | reconnect republishes availability, discovery, state | FailSafeTableTests.Mqtt_outage_reports_unhealthy_and_leaves_guard_decisions_unchanged; MqttLinkTests.Last_will_is_offline_and_retained_on_our_availability_topic; HeartbeatLoopTests.Beat_is_ON_only_when_both_Home_Assistant_and_MQTT_are_connected |
| Invalid configuration (incl. envelope violation) | parser/binder errors → no setup | devices of the last valid document of this process held off; system safe state, cause configuration_invalid; no MQTT entity retracted | safe_state | valid document → release after 6 good boundaries | GuardSafeStateTests.Invalid_configuration_keeps_last_valid_devices_off, Invalid_configuration_at_start_records_a_system_event_only, Configuration_safe_state_tells_missing_from_invalid; EnvelopeTests.* |
| Missing configuration | file absent | as above, cause configuration_missing | safe_state | file restored and valid | GuardSafeStateTests.Missing_configuration_is_safe_state, Configuration_safe_state_tells_missing_from_invalid |
| Start, no configuration or data yet | no store cycle / configuration not read | system safe state, cause starting, then meter_invalid until the first currents | safe_state | first valid currents + 6 good boundaries | GuardSafeStateTests.No_snapshot_yet_is_safe_state, Configuration_safe_state_tells_missing_from_invalid(NotRead) |
| Add-on shutdown | graceful stop; GuardService stops first | every device off, cause shutdown, with the last phase picture; decision log and state.json flushed, clean_shutdown | safe_state | next start; budget credited for downtime only after a clean stop and no clock step back | GuardSafeStateTests.Shutdown_records_safe_state_for_every_device, Shutdown_during_invalid_configuration_is_recorded_as_shutdown; HostLifecycleTests.Host_stop_records_the_shutdown_and_leaves_a_clean_state_file; GuardRestartTests.Budget_recovers_for_downtime_when_clock_moved_forward |
| Kill switch on | MQTT switch command, API POST, or restored from state.json | every controlled device held off within one evaluation | kill_switch | switch off → release after 6 good boundaries counted after the last engaged evaluation | GuardKillSwitchTests.Holds_every_device_off_within_one_cycle, Kill_switch_release_waits_for_persistence_up, Kill_switch_flicked_between_boundaries_waits_for_persistence_up, Restored_kill_switch_holds_from_the_first_evaluation, Kill_switch_engages_when_state_file_unwritable |
| Device state stale / unknown | state age ≥ state_max_age, unavailable, unrecognized, missing | device unsafe, held off, still a shed candidate at full rating, never counted as relief; listed under its signal's dependent devices (SIG-05) | guard_stale_signal | fresh again → release after 6 good boundaries counted from the hold | GuardSafeStateTests.Stale_device_state_marks_unsafe_and_relieves_nothing, Missing_device_state_is_unsafe_too, Device_state_blip_between_boundaries_restarts_release_persistence; GuardTierReactionTests.Unknown_charger_state_hides_no_overload_at_reduce_next; GuardSyntheticReplayTests.Frozen_device_state_is_unsafe_after_state_max_age |
| Clock step back | now < last evaluation | budget holds (no credit, no charge) until a new sample starts a new chain; band timers pause; every lockout restarts; the store forgets samples | none (stale data → guard_stale_signal) | new samples | GuardBudgetTests.Clock_step_back_neither_recovers_nor_consumes; EffectorGateTests.Lockouts_restart_on_a_backward_clock_step; GuardRestartTests.Budget_stays_when_clock_stepped_back; DecisionLogTests.Paging_stable_across_clock_step_back |
| state.json corrupt / untrusted / too large | parse or validation failure, size limit | set aside as state.json.corrupt, fresh start (full budgets, kill switch off), warning in API and UI | none | next write | RuntimeStateFileTests.Corrupt_state_starts_fresh_and_keeps_the_bad_file, Untrusted_content_is_refused_as_a_whole, A_file_above_the_size_limit_is_not_read |
| Decision log or /data unwritable | IOException on write | guard keeps running; decisions kept in a bounded memory queue; log reports unwritable; gap line after recovery | none | disk writable again | GuardRunnerTests.Recorder_failure_never_stops_protection; DecisionLogTests.Disk_full_keeps_guard_running_and_reports_unwritable, Unwritable_log_memory_is_bounded; FailSafeTableTests.Unwritable_data_path_keeps_the_guard_running; RuntimeStateCoordinatorTests.Unwritable_state_file_is_reported_and_never_raised |
| Decision log line corrupt | partial last line, not a decision, impossible values | line skipped; start never blocked | none | — | DecisionLogTests.Partial_last_line_isolated, Lines_that_are_not_decisions_are_skipped, A_line_with_an_impossible_duration_is_skipped_and_never_blocks_the_start |
| Budget exhausted | budget ≤ top_tier_at_or_below (0) | everything controlled on that phase off (top tier) | guard_budget_low | recovers at dt/recovery_time (envelope ≥ 600 s) at or below continuous; release after 6 good boundaries with budget ≥ stop_increases_below | GuardBudgetTests.Budget_exhaustion_sheds_everything_to_the_tick; GuardSyntheticReplayTests.Thermal_memory_makes_the_second_excursion_empty_the_budget; EnvelopeTests.Budget_recovery_faster_than_the_envelope_is_refused |
| Guard loop failure | unhandled exception in the guard loop (listener exceptions are isolated) | add-on stops: health OFF/offline, Supervisor restarts it; stop path still flushes what was recorded | none (process stops) | restart | HostLifecycleTests.A_failed_guard_loop_stops_the_add_on; GuardRunnerTests.A_failing_status_listener_never_stops_protection_or_the_other_listeners, A_failing_step_listener_never_stops_protection |
| Restart | process start | every lockout restarts from the start instant (lockout_min_on recorded as overridden on protective holds); budgets restored | lockout_min_on (as overridden) | — | GuardRestartTests.Lockouts_restart_at_process_start, Memory_holds_no_lockout; RuntimeStateFileTests.State_holds_no_lockout_expiry |
