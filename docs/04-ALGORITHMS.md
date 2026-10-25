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
kettle 2 kW on a fully allocated phase → 22.8 A = 0.91 × In → below the rating
reserve 1.35 kW → 4.40 kW allocated; the same kettle → 27.8 A = 1.11 × In → inside the non-tripping band
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

**Version 3** (2026-10-25, maintainer survey): added `foreign_load` (a reduction, shed or refused increase whose cause is a foreign load detected under section 4, so the log tells "the house did this" from "the reserve is too small"; versions 1–2 used `reserve_threshold`) and `change_limit` (an increase deferred because another increase of this settling window is still in flight, section 3 "Simultaneous increases"; versions 1–2 used `settling_window_open`, which stays for a window that is open on the device itself).

```
guard_tier_exceeded         guard_budget_low          guard_stale_signal
reserve_threshold           headroom_insufficient     lockout_min_on
lockout_min_off             settling_window_open      awaiting_confirmation
value_rank_below_cutoff     goal_met                  guard_expression_false
device_unresponsive         manual_override           mode_shadow
safe_state                  plan_slot_target          headroom_available
kill_switch                 foreign_load              change_limit
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
| Phase current stale / unavailable / missing / rejected (one phase) | age ≥ max_age from last_reported, unavailable/unknown state, entity missing, refused sample → PhaseProtection.DataValid=false | every controlled device with a stage on that phase held off; budget holds, band timers pause; phase safe state recorded; dispatch wants no increase there | guard_stale_signal | data valid → safe_state_exited; holds released after persistence_up_cycles (6) good boundaries counted from the last hold cause | GuardSafeStateTests.Stale_phase_current_only_that_phase, Invalid_phase_reason_maps_to_its_code, Safe_state_exit_releases_only_after_persistence_up, One_phase_blip_between_boundaries_restarts_release_persistence; GuardSyntheticReplayTests.Frozen_phase_current_is_safe_state_90_seconds_after_its_last_report |
| Whole meter stale (no valid phase) | every phase invalid | every controlled device held off; system safe state, cause meter_invalid | guard_stale_signal (safe_state while currents are still awaited) | as above | GuardSafeStateTests.Whole_meter_stale_everywhere, System_safe_state_names_its_cause, Outage_between_boundaries_restarts_release_persistence |
| HA disconnected | source status not connected → phases and device states source_disconnected | everything held off; cause source_disconnected | safe_state | reconnect → currents awaiting (still safe state) → valid → release after 6 good boundaries | GuardSafeStateTests.HA_disconnection_everywhere, HA_reconnect_awaiting_is_safe_state_and_releases_by_streak; DeviceStateSignalTests.Disconnected_source_leaves_every_device_state_unknown |
| MQTT disconnected | MQTT link not connected | guard and dispatch unaffected; retained LWT 'offline' on switchboard/availability; health false → health entity OFF/unavailable; kill switch and overrides settable via API only | none (no guard decision) | reconnect republishes availability, discovery, state and re-subscribes exactly the allowed command topics | FailSafeTableTests.Mqtt_outage_reports_unhealthy_and_leaves_guard_decisions_unchanged; MqttLinkTests.Last_will_is_offline_and_retained_on_our_availability_topic; HeartbeatLoopTests.Beat_is_ON_only_when_both_Home_Assistant_and_MQTT_are_connected |
| Invalid configuration (incl. envelope violation) | parser/binder errors → no setup | devices of the last valid document of this process held off; system safe state, cause configuration_invalid; the gate may address those devices with off/minimum only, never an increase; dispatch wants nothing; overrides refused (API 409, MQTT override topics unsubscribed); no MQTT entity retracted | safe_state | valid document → release after 6 good boundaries | GuardSafeStateTests.Invalid_configuration_keeps_last_valid_devices_off, Invalid_configuration_at_start_records_a_system_event_only, Configuration_safe_state_tells_missing_from_invalid; EnvelopeTests.*; Stage3ArchitectureTests.Invalid_configuration_lets_the_gate_address_last_valid_devices_with_off_only; DispatchEndpointTests.Override_without_a_valid_configuration_is_409; DispatchEntitiesTests.Without_a_valid_configuration_earlier_entities_stay_unknown_and_no_override_is_listened_to |
| Missing configuration | file absent | as above, cause configuration_missing | safe_state | file restored and valid | GuardSafeStateTests.Missing_configuration_is_safe_state, Configuration_safe_state_tells_missing_from_invalid |
| Device removed from a valid configuration while targeted on | a reload no longer declares it | never addressed again, not even to switch it off (SAFE-05 read literally); one release record says it left control while targeted on; its MQTT entities retracted, its override topic unsubscribed | safe_state | — (question to the maintainer) | Stage3ArchitectureTests.Device_removed_while_targeted_on_is_not_commanded_and_recorded; DispatchEntitiesTests.Override_subscriptions_and_entities_follow_the_declared_devices |
| Start, no configuration or data yet | no store cycle / configuration not read | system safe state, cause starting, then meter_invalid until the first currents | safe_state | first valid currents + 6 good boundaries | GuardSafeStateTests.No_snapshot_yet_is_safe_state, Configuration_safe_state_tells_missing_from_invalid(NotRead) |
| Add-on shutdown | graceful stop; GuardService stops first | every device off, cause shutdown, with the last phase picture; decision log and state.json flushed, clean_shutdown | safe_state | next start; budget credited for downtime only after a clean stop and no clock step back | GuardSafeStateTests.Shutdown_records_safe_state_for_every_device, Shutdown_during_invalid_configuration_is_recorded_as_shutdown; HostLifecycleTests.Host_stop_records_the_shutdown_and_leaves_a_clean_state_file; GuardRestartTests.Budget_recovers_for_downtime_when_clock_moved_forward |
| Kill switch on | MQTT switch command, API POST, or restored from state.json | every controlled device held off within one evaluation; outranks every override (a force_on waits for the release and its persistence) | kill_switch | switch off → release after 6 good boundaries counted after the last engaged evaluation; the held level stays the intended one and dispatch restores the devices as recorded starts, start order, one per settling window (DISP-03, DISP-08), never silently | GuardKillSwitchTests.Holds_every_device_off_within_one_cycle, Kill_switch_release_waits_for_persistence_up, Kill_switch_flicked_between_boundaries_waits_for_persistence_up, Restored_kill_switch_holds_from_the_first_evaluation, Kill_switch_engages_when_state_file_unwritable; FailSafeTableTests.Kill_switch_outranks_a_forced_on_device_and_the_override_waits_for_its_release; DispatchEngineTests.Restores_after_a_guard_hold_are_recorded_starts_in_start_order, Holding_one_increase_back_never_lets_another_through_beyond_the_limit; GuardEntitiesTests.Kill_switch_release_is_distinguishable |
| Device state stale / unknown | age-based (default): age ≥ state_max_age; state_reports: on_change: only unavailable, unknown, unrecognized, entity missing or HA disconnected (no age limit); age-based or not: unavailable, unrecognized, missing | device unsafe, held off, still a shed candidate at full rating, never counted as relief; listed under its signal's dependent devices (SIG-05) | guard_stale_signal | fresh again → release after 6 good boundaries counted from the hold | GuardSafeStateTests.Stale_device_state_marks_unsafe_and_relieves_nothing, Missing_device_state_is_unsafe_too, Device_state_blip_between_boundaries_restarts_release_persistence; GuardTierReactionTests.Unknown_charger_state_hides_no_overload_at_reduce_next; GuardSyntheticReplayTests.Frozen_device_state_is_unsafe_after_state_max_age; Stage3SignalTests.On_change_state_is_fresh_while_available_with_no_age_limit, On_change_state_goes_unknown_on_unavailable_or_disconnect |
| Device draw signal stale / unavailable / missing | age ≥ draw max_age (devices.draw_max_age), unavailable/unknown, entity missing | not trusted: relief 0 (contribution falls back to rated current while a relay reports on, 0 for a setpoint device), that device's dispatch increases blocked, a shed step is never judged idle on it, left out of the SIG-09 device terms; NOT held off (a draw is no protection input): not among the device's unsafeSignals in GET /api/guard, labelled 'not trusted: no relief, no increases' on the Signals page | guard_stale_signal (on the blocked increase) | fresh again | GuardEndpointTests.A_stale_draw_is_never_named_as_making_its_device_unsafe; FailSafeTableTests.Stale_draw_through_the_store_relieves_nothing_and_holds_nothing_off; DispatchEngineTests.Stale_draw_blocks_that_devices_increases; LoadModelTests.Stale_draw_relieves_nothing_and_blocks_that_devices_increases; Stage3SignalTests.Stale_device_draw_is_left_out_of_the_balance |
| Circuit group sub-meter stale or negative | sub-meter current stale / unavailable / missing, or negative (read unsigned: sign_violation, a load-only circuit cannot export) → GroupProtection.DataValid=false | the group's members held off; group budget holds; dispatch wants no increase in the group | guard_stale_signal | data valid → release after 6 good boundaries | CircuitGroupTests.Stale_sub_meter_holds_the_members_off; Stage3SignalTests.Negative_group_sub_meter_reading_is_refused_not_read_as_export |
| Settling window timeout (lost sample) | a balance input of the window's closing set (phase power, phase current, sub-meter, the device's own draw; never the device state, never generation) has not reported after command + reaction time by settling.hard_timeout | window closed; one SettlingTimedOut record; verdict timed_out; no mismatch counted; the in-flight estimate ends (measurement rules) | settling_window_open | next command opens a new window | SettlingScenarioTests.Lost_sample_times_out_and_counts_no_mismatch, Delayed_sample_closes_the_window_when_it_arrives, Night_window_closes_with_the_inverter_asleep |
| Effector unresponsive | settling.mismatch_limit (3) consecutive mismatches of applied commands (active semantics only; shadow windows verify nothing); a setpoint raised but not drawn is inconclusive, never a mismatch; a foreign step in the window makes a measured change above the expected one inconclusive in either commanded direction; once refuted by a mismatch a command no longer counts as ours for foreign load detection | MarkedUnresponsive; target off (min_on deferral for a running relay); excluded from dispatch increases; protection unchanged | device_unresponsive | one probe increase after settling.unresponsive_retry_after: match → UnresponsiveCleared; mismatch → marked again at once (next probe one retry delay later); a restart or a changed declaration clears it (never persisted) | SettlingScenarioTests.Unresponsive_on_change_relay_is_marked_after_mismatch_limit_excluded_and_retried, Unresponsive_flag_clears_when_the_device_declaration_changes, Match_resets_the_mismatch_count, Foreign_load_arriving_mid_window_is_recorded_and_not_blamed_on_the_device, Foreign_load_arriving_mid_switch_off_window_is_not_blamed_on_the_device, A_relay_that_did_not_follow_its_command_hides_no_kettle; EffectorCommandTests.Car_limited_draw_after_setpoint_increases_never_marks_the_charger_unresponsive, Charger_that_keeps_drawing_above_a_lowered_setpoint_is_a_mismatch, Failed_probe_re_marks_at_once_without_counting_to_the_limit_again; DispatchRulesTests.Car_limited_draw_after_setpoint_increase_is_inconclusive_not_mismatch |
| Override with stale / invalid data | forced device's phase (or state, or group) invalid | forced device held off like any other (the hold records overridden=manual_override when forced on); the override acts only after the holds were released after persistence_up_cycles; the override itself persists | guard_stale_signal / safe_state | data valid + release persistence | DeferredIntentAndOverrideTests.Force_on_with_stale_data_stays_off_until_release_persistence, Guard_sheds_forced_on_device_and_records_manual_override |
| Forced increase into an overload or a low budget | force_on needs guard increases on every phase (main and group, reserve ignored) and the ceiling under the breaker allowance (may_add top × In − I_est) | refused: one Deferred record whose target is the forced level (refused_by headroom_insufficient or guard_budget_low), re-checked every cycle; a forced device the guard shed is not re-applied until the guard released it and the allowance fits; a forced-on device stays reserved in the allocation, and a forced increase counts against max_simultaneous_increases | manual_override (refused_by headroom_insufficient / guard_budget_low) | allowance fits at a boundary | DeferredIntentAndOverrideTests.Forced_on_device_shed_by_guard_is_not_restarted_into_an_overload, Force_on_outranks_headroom_and_rank; DispatchEngineTests.A_forced_on_device_keeps_its_headroom_reserved_after_its_lockout |
| Override for an undeclared device / retained override command | API id not in the current valid configuration; MQTT topic not of a declared device; retained MQTT message | refused: API 404 (409 without a configuration); MQTT topic never subscribed (topic guard re-reads the declared set), command ignored; retained command ignored | none | — | DeferredIntentAndOverrideTests.Override_for_undeclared_device_is_refused; DispatchEndpointTests.Override_of_an_undeclared_device_is_404; DispatchEntitiesTests.Override_command_topics_are_subscribable_only_for_declared_devices, Override_commands_are_exactly_the_options_and_only_for_declared_devices, Override_command_reaches_the_overrides_but_a_retained_one_never_does |
| Active mode requested | add-on option active (and UI choice active) | effective mode stays shadow, note active_not_enabled; nothing commanded: no production implementation of the command port; a port present in shadow mode is never called | mode_shadow | — (maintainer lifts the kickoff rule) | ModeStateTests.Active_option_stays_shadow_with_active_not_enabled; Stage3ArchitectureTests.Production_mode_is_shadow_and_records_nothing_commanded, Command_port_has_no_implementation_outside_tests, Only_tests_can_hand_the_gate_a_port, Gate_addresses_only_devices_of_the_parsed_configuration; EffectorCommandTests.A_port_in_shadow_mode_is_never_called |
| Clock step back | now < last evaluation | budget holds (no credit, no charge) until a new sample starts a new chain; band timers pause; every lockout (protection and target timelines) restarts; the store forgets samples | none (stale data → guard_stale_signal) | new samples | GuardBudgetTests.Clock_step_back_neither_recovers_nor_consumes; EffectorGateTests.Lockouts_restart_on_a_backward_clock_step; GuardRestartTests.Budget_stays_when_clock_stepped_back; DecisionLogTests.Paging_stable_across_clock_step_back |
| state.json corrupt / untrusted / too large | parse or validation failure (any Stage 3 key out of shape too: override mode, source, device or group name, group budget), size limit, unknown schema; a negative last-known setpoint (a discharging storage device) is valid, only a non-finite one is refused | set aside as state.json.corrupt, fresh start (full budgets, kill switch off, no override), warning in API and UI; schema 1 (0.3.x) read with no override; written as schema 1 while no override and no group budget exists, so a rollback keeps it; with one, 0.3.x sets the file aside | none | next write | RuntimeStateFileTests.Corrupt_state_starts_fresh_and_keeps_the_bad_file, Untrusted_content_is_refused_as_a_whole, A_file_above_the_size_limit_is_not_read, A_discharging_storage_device_survives_a_restart_with_the_kill_switch; DispatchPersistenceTests.Untrusted_stage_3_content_is_refused_as_a_whole, A_schema_1_file_cannot_carry_overrides, A_schema_1_file_of_0_3_x_is_read_with_no_override, Without_overrides_or_groups_the_file_stays_schema_1_so_a_rollback_keeps_it |
| Decision log or /data unwritable | IOException on write | guard and dispatch keep running; decisions kept in a bounded memory queue; log reports unwritable; gap line after recovery | none | disk writable again | GuardRunnerTests.Recorder_failure_never_stops_protection; DecisionLogTests.Disk_full_keeps_guard_running_and_reports_unwritable, Unwritable_log_memory_is_bounded; FailSafeTableTests.Unwritable_data_path_keeps_the_guard_running; RuntimeStateCoordinatorTests.Unwritable_state_file_is_reported_and_never_raised |
| Decision log line corrupt | partial last line, not a decision, impossible values, a format-1 line carrying Stage 3 fields, a format-2 line without origin/group/target | line skipped; start never blocked; 0.3.x (format 1) lines still read | none | — | DecisionLogTests.Partial_last_line_isolated, Lines_that_are_not_decisions_are_skipped, A_line_with_an_impossible_duration_is_skipped_and_never_blocks_the_start; DispatchPersistenceTests.Legacy_mode_note_line_still_parses, Legacy_guard_line_still_parses_and_cannot_carry_stage_3_fields |
| Budget exhausted (phase or circuit group) | budget ≤ top_tier_at_or_below (0) | everything controlled on that phase (group members on it) off (top tier) | guard_budget_low | recovers at dt/recovery_time (envelope ≥ 600 s) at or below continuous; release after 6 good boundaries with budget ≥ stop_increases_below | GuardBudgetTests.Budget_exhaustion_sheds_everything_to_the_tick; GuardSyntheticReplayTests.Thermal_memory_makes_the_second_excursion_empty_the_budget; EnvelopeTests.Budget_recovery_faster_than_the_envelope_is_refused; CircuitGroupTests.Group_budget_is_independent_of_the_main_phase |
| Guard loop failure | unhandled exception in the guard loop (listener exceptions are isolated) | add-on stops: health OFF/offline, Supervisor restarts it; stop path still flushes what was recorded; MQTT reads declared devices from the published snapshot, not the guard's input port, so it still reports OFF | none (process stops) | restart | HostLifecycleTests.A_failed_guard_loop_stops_the_add_on; GuardRunnerTests.A_failing_status_listener_never_stops_protection_or_the_other_listeners, A_failing_step_listener_never_stops_protection |
| Restart | process start | every lockout restarts from the start instant on both timelines (lockout_min_on recorded as overridden on protective holds); budgets (phase and group; a group budget crossing a threshold or dropping by state_budget_save_step is written at once, like a main one) and overrides restored (source restored; an override notification arriving out of order never overwrites a newer set); targets, deferred intents, settling windows, mismatch counts and unresponsive flags are never persisted: every device starts unset | lockout_min_on (as overridden) | — | GuardRestartTests.Lockouts_restart_at_process_start, Memory_holds_no_lockout; RuntimeStateFileTests.State_holds_no_lockout_expiry; DeferredIntentAndOverrideTests.Lockouts_restart_at_process_start_on_both_timelines, Override_survives_restart; DispatchPersistenceTests.Override_survives_a_restart_through_state_json, State_holds_no_dispatch_memory; DispatchEndpointTests.Override_set_through_the_api_survives_a_restart_of_the_host; RuntimeStateCoordinatorTests.Circuit_group_budget_drop_or_crossing_is_written_at_once, An_older_override_snapshot_arriving_late_never_overwrites_a_newer_one |
| Lockout expires with a deferred change | min_on / min_off on the target timeline expires (DueAt) between boundaries | a deferred decrease is applied at the expiry; a deferred increase is not made on the old wish: it waits for the next cycle boundary, where dispatch wants it afresh with guard permission, settling, DISP-08 and confirmation re-checked, and is applied there carrying its deferral | the wish's reason (refused_by lockout_min_on / lockout_min_off on the Deferred record) | — | DeferredIntentAndOverrideTests.Deferred_stop_is_applied_at_the_lockout_expiry_between_boundaries, Deferred_start_is_not_made_at_expiry_after_the_guard_stopped_increases, Start_refused_by_min_off_recorded_once_and_applied_at_the_first_boundary_after_expiry; EffectorCommandTests.Deferred_start_reaches_the_port_at_the_first_boundary_after_min_off_expires |
| Hold re-asserted without a new guard intent | active semantics (test double): another controller re-enables a held device, or its declaration changed and its target was reset, while the guard's limit is still in force | the gate re-applies the hold and records it (switch_off / reduce_to_minimum, origin guard, the hold's reason), so no command ever leaves without a decision; with active semantics a change that needs no command is recorded as not commanded and not refused by mode_shadow | the hold's reason (e.g. kill_switch) | guard release | EffectorCommandTests.Re_asserting_a_hold_after_an_external_re_enable_is_one_recorded_command, With_active_semantics_a_change_that_needs_no_command_is_not_refused_by_shadow_mode |
| Device redeclared with a changed specification | spec fingerprint differs at the start of a step | the ledger entry (target, deferral, window, mismatches, unresponsive flag) is reset before the guard and dispatch read it, so no level of the old declaration (another stage count) is ever planned; dispatch keeps running | none | — | DispatchEngineTests.A_stepped_device_redeclared_with_another_stage_count_starts_unset_before_dispatch_reads_it; SettlingScenarioTests.Unresponsive_flag_clears_when_the_device_declaration_changes |
| Storage device discharging / reported off the level grid | a negative storage setpoint, a setpoint below a modulating floor, a stage pattern the cumulative levels do not hold | left as reported unless dispatch plans a real, gated increase; leaving a discharge is budgeted and estimated as the whole step (the discharge plus the charge); never an ungated snap to the grid | headroom_available (only for a planned, gated increase) | — | DispatchEngineTests.Discharging_battery_is_left_alone_when_no_charge_is_planned, Leaving_a_discharge_is_budgeted_and_estimated_including_the_discharge, A_paused_charger_below_its_floor_is_not_raised_to_it_without_the_gates; LoadModelTests.Ending_a_storage_discharge_counts_the_whole_step |
| Idle device at a high setpoint chosen for a shed step | a fresh draw at or below the level the step leads to | the step is kept (shedding everything still holds it) but ordered after every step that relieves something, so a shed-one reaction takes the next real candidate | guard_tier_exceeded / guard_budget_low | — | ShedOrderTests.An_idle_charger_whose_draw_is_at_its_floor_never_takes_a_shed_step_before_a_real_candidate; GuardTierReactionTests.Idle_charger_ranked_lowest_never_spends_the_shed_step |
