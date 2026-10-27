# Changelog

All notable changes to this add-on. Newest version first; format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## 0.11.0

### Fixed

- A charger that declares only `draw.power` was counted as drawing nothing, so its limit was the bare headroom and a 6 A go-e never rose although the phase had room (seen live on 0.10.1: the go-e's `nrg_4` current stood still at 5.662 A for 11 minutes and went stale, `nrg_7` power refreshed every 2 s). For a `modulating` device that declares a `draw.power` and no `draw.current` on a phase with a mapped `phase_voltage`, the signal layer now derives the current as power / voltage, from a fresh power (or one silent after exactly 0) and a fresh voltage; otherwise the device is not trusted as before (no relief, no increases). The freshness rule of a draw is unchanged. The derived current is never above the real one (power factor at most 1), so every use of it errs towards less: a smaller limit, a smaller relief, a larger in-flight increase. A setpoint device with a power draw is now also verified against its setpoint, in amperes, like one with a current draw. Declare only `power` for a go-e (see `DOCS.md`).
- A start or increase of a device with `sources` that is withheld while the hourly balance's projection sits inside the band (the settlement rule says Hold) is now `balance_projection_low`, as it is for a device without `sources` and as 0.9 reports it, instead of `source_not_allowed` (which suggested the energy's source was not allowed). Only the reason of a refusal changes; which devices start does not.
- A start late in the hour now pays for the energy its `relay_min_on` holds past the :00: seen live, a heater started 3 minutes before the :00 with a 6-minute lockout could not be stopped for 3 minutes of the next hour, which had no surplus for it. The projection after a start is reduced by the candidate's power for the part of its lockout that falls in the next hour (refused as `balance_projection_low`; it only withholds starts, nothing is shortened, no new setting). See `docs/13-HOURLY-BALANCE.md`.

### Added

- Planner (v3.0, shadow): goals with a deadline per device (`goal:`), PV and house-load forecasts, forward price curve, a pure deterministic planner, `GET api/plan` (entries, cost, notes, last solve), a day-plan screen (`#/plan`) and a goal row on the device card with a manual "Want now" goal (`POST api/goals`, `DELETE api/goals/{device}`; `state.json` schema 5). `planner.dispatch` defaults to `off`: the plan is computed and shown, dispatch ignores it and decides byte for byte as before; `narrow` and `full` exist behind that flag. Reason codes list version 7 (`plan_deferred`, `goal_deadline_urgent`, `plan_discharge`, `soc_floor`, `soc_ceiling`).
- Displacement, with an `economics:` section: when a device worth more is refused only because the hourly projection is too low, but would start with a running cheaper device stopped, that cheaper device (lowest value first) is lowered one stage with reason `value_rank_below_cutoff`, and the dearer one starts on a later step by the ordinary rule. It needs a value gap of `economics.min_margin` and `economics.order_min_gap_fraction`, never stops a device before its `min_on`, never stops a zone-class device and displaces one device per step. Seen live: the car (2.03) could not start beside a running heater (0.35) with 2.4 kW free. No new setting, no new reason code. See `docs/14-ECONOMICS.md`.

## 0.10.1

### Fixed

- With an `economics:` section, a device could be started once from energy its `sources` do not allow: when the allowance gate refused or lowered a device worth more, the headroom it freed let a device behind it rise on the redone allocation without being judged. The gate now judges again on every redone allocation. Seen live in shadow as one `headroom_available` start of a heater from the afternoon peak with the hour importing. Decisions without an `economics:` section are unchanged.

## 0.10.0

Economic dispatch (v2.0 slices 1 to 6) and the command-path reason codes. Shadow stays the default and **Control allowed** stays off: the economics only shape the decisions that are logged, nothing is switched. Without an `economics:` section and without `sources` on a device the decisions are byte-identical to 0.9.0. Not a protective device; your breakers remain the protection.

### Added

- An optional `economics:` section in `switchboard.yaml` (currency label, holiday list, tariff as a flat price or time-of-use zones with days, months and hours on the local clock, settlement model and export price) and the `economics:` control parameters (`min_margin`, `order_stability_cycles`, `order_min_gap_fraction`, `zone_end_margin`, `net_billing_window`). A file without the section loads and decides exactly as before. A dynamic price (`signal:`) is refused with a clear issue.
- `GET api/economics`: the tariff zone and price now, the next change, the settlement as configured, and the section's issues. Tauron G13 is a configuration template (`deploy/examples/economics-g13.yaml`, placeholder prices, verify against your contract), not code.
- New configuration issue codes `duplicate_zone_id`, `dynamic_price_not_supported`, `zone_gap` and `holidays_list_stale`.
- Per-device keys `sources` (`pv_only`, `pv_and_zone:<id>[,<id>]`, `any`), `value_follows` (`static`, `import_price`, `export_price`) and `value_factor` are read and checked; a zone that is not in the tariff, or a price that is not configured, is a warning (`sources_unusable`, `value_follows_without_price`) and narrows the device, never widens it. Members of a rotation group must now also agree in these keys.
- `GET api/economics` lists each device with its effective value per kWh now (`vEff`, `valueBasis`) and its sources, declared and narrowed. `settlement.model: net_metering` is refused with a message that it is deferred.
- Dispatch uses the economics, in shadow like everything (decisions are computed and logged, nothing is switched): a device that declares `sources` starts only on an energy that is open now (surplus by the settlement model, or the active tariff zone when it is one of its sources) and whose price its value covers, and not within `zone_end_margin` of the end of a zone; the heaters of a `pv_only` file no longer start from the grid at night, a car with `pv_and_zone:offpeak` does in the off-peak zone. A device that declares no `sources` keeps the 0.9 rule exactly. A zone-class device is not stopped by the hourly balance; a surplus-class one is. Nothing here can raise a load above what the guard and the 0.9 balance gate allow.
- The start order, and the order of the balance stops, follows the effective value (`value_follows`, `value_factor`) or `shed_priority`; a new order needs `order_stability_cycles` agreeing cycles and keys `order_min_gap_fraction` apart. The guard's shed order is unchanged and static.
- Reason codes, list version 6: `zone_allowed`, `source_not_allowed`, `value_below_cost`, `surplus_available`, `surplus_low`. The decision log reads versions 6, 5, 4, 3 and 2.
- Dispatch decision inputs carry an `economics` block (zone, import and export price and their basis, effective value and its basis, order key, energy class, sources, settlement); absent on older lines and without an `economics:` section. The decision log API (`GET api/decisions`) shows it as `economics`.

- Dynamic prices from a Home Assistant entity (shadow like everything; the add-on fetches nothing and has no outbound access): a signal of `kind: price` with `unit: <currency>/kWh` or `<currency>/MWh` (the currency as in `economics.currency`; a per-MWh price is converted to per kWh in the signal layer) and the optional roles `price_import` and `price_export`. A price spec becomes `signal:` + a required static `fallback` (+ an optional static `add`, usually the distribution zones) for `economics.tariff` and `settlement.export_price`: the price is the entity's value plus the add while the entity is fresh (basis `live`), otherwise the fallback (basis `fallback`), never the last known value. A missing fallback makes the file invalid (`price_fallback_missing`).
- New configuration issue codes `price_fallback_missing`, `unit_mismatch` and `price_signal_unused` (warnings: a price unit in another currency than `economics.currency`, a price role no price names). `dynamic_price_not_supported` now means only a dynamic price nested inside a `fallback` or an `add`.
- `GET api/economics` shows a dynamic price now: `kind: dynamic`, the live price and its basis, `fallbackInForce` and the signal (state, age). The Signals page lists price signals with their age. Decisions record `price_basis: live` or `fallback`. Entering and leaving the fallback is one log line each (`tariff_fallback`), not a decision (the reason code list stays at version 6). Example: `deploy/examples/economics-dynamic.yaml`.

- Savings accounting (shadow like everything; no decision changes): the statistics (`statistics.json`, now schema 2) record per 15-minute bucket the import and export price dispatch used, time-weighted over the bucket with their basis (`flat`, `zone`, `live`, `fallback`; a bucket with any fallback is flagged), and per device its energy at the effective value. A schema 1 file from 0.9 is still read: its buckets have no price data and nothing is back-filled.
- `GET api/economics` gains `savings` (null without an `economics:` section): what the controlled loads saved, per settlement period (the local hour under `hourly_netting`, the statistics bucket under `net_billing`), as value gained minus the extra settlement cost against the loads being off, for today, an optional `from` and `to` range, per device, since the first priced period, with a flag for periods that used a price at its fallback. An estimate for your own orientation, not a bill. `GET api/statistics` gains the prices per bucket and range. The dashboard card follows in a later version.
- Tariff and savings cards on the dashboard (shown only with an `economics:` section): the zone, import and export price with the currency label from the configuration, the price basis and the next change; today's savings as an estimate against the controlled loads being off, per device (a negative total is shown), since the first counted period, with the fallback flag and the running period apart. The decision-log summary shows the same savings instead of "not available" when they exist. The device card shows the value now, the sources in words and what is allowed now. English and Polish.
- `GET api/economics`: each device carries `class`, the energy class (`pv`, `zone`, `none`) of the last dispatch step.

### Changed

- A command to one of Switchboard's own entities (its override selects, its switches, its sensors) is now refused when it would be sent, not only warned about: nothing leaves, the Status page says "not sent: one of Switchboard's own entities", and the decision records `command_not_sent`. An entity of another integration with a similar name is still commanded.
- Reason codes, list version 5: `mode_active` (an effective active mode), `command_failed` (Home Assistant answered with an error or not at all) and `command_not_sent` (the command never left). They replace `manual_override`, `device_unresponsive` and `safe_state` where those stood in for them. The decision log reads versions 5, 4, 3 and 2.

## 0.9.0

The hourly balance becomes a gate of dispatch. Shadow stays the default and **Control allowed** stays off: in shadow the gate only shapes the decisions that are logged, nothing is sent. Not a protective device; your breakers remain the protection.

### Added

- A new `hourly_balance:` section in the control parameters: `floor_wh` 150, `ceiling_wh` 525, `burn_window` 15m, `smoothing_window` 5m, `min_remaining` 1m, `max_age` 60s.
- The allowance rule for devices that declare `start: headroom`: the balance projected to the next :00 (balance plus free power minus the load, over the time left) must stay above a target band that narrows from 525 Wh to 150 Wh over the last 15 minutes of the hour. A start needs the projection above the band with the new load still above the floor; a stop comes when it falls below the floor, held back in the last minutes while the projection is not negative. Free power is the time-weighted mean over 5 minutes of the grid export with every own load added back.
- Starts go to the most valuable device per kWh first; stops to the least valuable first, one stage at a time. It only ever narrows what dispatch may do, and it is active only while both energy counters are mapped.
- With the balance or the free power unknown the allowance is zero: nothing rises, and a device Switchboard started itself is lowered one stage (through its lockouts).
- Reason codes, list version 4: `balance_projection_low`, `balance_surplus`, `balance_unknown`. The decision log reads versions 4, 3 and 2.
- The dashboard shows the hourly balance: the balance of the running hour, the projection at the next :00 for the load as it is, the target band, minutes and seconds to :00, free power and own load (Wh and W, positive = export), and why it is unknown when it is.
- `GET api/balance` adds `known`, `unknownReason`, `freeW`, `loadW`, `projectionWh`, `band` and `ruleSecondsToNextHour`; the existing fields are unchanged.
- Dispatch decisions record the balance they were made beside (`balance` in the decision inputs of the log and of `GET api/decisions`). Older log lines still read.

## 0.8.1

A fix to the hourly balance of 0.8.0, found on the first live day. Still measurement only: nothing uses the balance yet.

### Fixed

- The balance was unknown most of the day. A cumulative counter that does not change is not reported again, so the export counter stood still (and aged past `max_age`) while the house imported, and the import counter while it exported; 0.8.0 required both to be fresh. The pair is now usable while neither counter is unavailable or refused and the meter is alive (a counter or a phase power reported within its `max_age`); its report time is the latest of those reports.

## 0.8.0

The balance of the running hour, read from the meter's own counters. Nothing is controlled differently: the balance is only measured and shown (`GET api/balance`); no decision uses it yet. Shadow stays the default and **Control allowed** stays off. Not a protective device; your breakers remain the protection.

### Added

- Two signal roles, `energy_export` and `energy_import` (kind `energy_counter`, unit `Wh`, `kWh` or `MWh`): the main meter's total exported and imported energy, summed over the phases. Map both or neither; the example maps `sensor.solaredge_m1_exported_kwh` and `sensor.solaredge_m1_imported_kwh`. A helper or utility meter (such as an hourly balance sensor) is not an input and gets the derived-sensor warning. The wizard keeps these two signals as written.
- `GET api/balance`: the balance of the running local hour in Wh (positive = net export), the start of the hour, the seconds to the next :00, and why it is unknown when it is (`unavailable`, `stale`, `no_baseline`, `counter_reset`, `clock_step_back`). The balance is the energy exported minus the energy imported since the last counter report at or before the local :00; it is never a guess: with no baseline it is unknown, not zero.
- The baseline of each hour is kept in `/data/balance-baseline.json`, so a restart inside an hour keeps the balance. A corrupt file is set aside and the balance starts unknown until the next full hour. Hours follow the local clock, including the 23 and 25 hour days.

### Changed

- Dispatch inputs carry the two counters, and only when both are fresh. Protection reads nothing new.

## 0.7.1

A charger that is stopped through a select. The go-e has no switch that stops charging; its force-state select does (`1` don't charge, `0` neutral). After this update Switchboard still controls nothing until you set the option `mode: active` **and** switch **Control allowed** on: shadow stays the default and the switch stays off. Not a protective device; your breakers remain the protection.

### Added

- A modulating device's `stop_actuator` may be a `select` or `input_select`, with the new keys `stop_option` (stops the device) and `resume_option` (lets it run again), both required there and refused next to a switch; `1` and `"1"` are the same option. `switchboard.yaml` stays schema version 1. The example declares the go-e force state (`select.goe_422752_frc`, `stop_option: "1"`, `resume_option: "0"`).
- `select_option` joins the commands that can be sent: only to a select declared as a stop actuator, only with its declared stop or resume option, and, once Home Assistant reported the select's options, only an option it offers (otherwise the call is not sent, the command path's last failure names the option, and the Signals page warns). During an invalid configuration only the stop option is sent.
- Below its minimum a charger with a select stop is stopped by protection (full shed, shed one, budget), the safe state, the kill switch, the add-on stopping and the reconnect re-send, exactly as with a switch stop, and given back with its resume option. Only declared options are ever sent: a charger you had set to another option (`2`, force charging) is stopped anyway and resumed with the resume option, and the log names the option that was not restored, also across a restart. A charger you stopped yourself is never resumed by `restore_only`. A new `resume_option` alone, saved while Switchboard holds the charger stopped, keeps what is to be given back (it is resumed with the option declared then); a changed stop actuator or `stop_option` ends it, and the wizard warns.
- The setup wizard lists selects as stop actuators of a modulating device, with their options to choose from; it refuses missing, equal or not offered options, and Switchboard's own entities as an actuator, and warns when you change the stop actuator or stop option of a device Switchboard holds reduced.
- A hand-written file that declares one of Switchboard's own entities as an actuator or stop actuator gets a warning (`own_entity_actuator`).

### Changed

- With a stop actuator declared, dispatch (not only protection) stops a charger when even its minimum does not fit the reserve, and a `force_off` override stops it instead of leaving it at its minimum.
- A restore baseline taken while a device's state was unknown keeps the device's first reported level when that report is older than the acknowledgement of the stop (after a restart the acknowledgement and the first states can arrive together); 0.7.0 dropped it and left the device reduced.
- The 0.6.x UI mode choice is gone from the mode sensor's attributes (`ui_choice`), from `api/guard`, `api/dispatch` and mode decisions; `control_allowed` says what it stood for.
- A refusal at send time logs why (not declared, or an option the select does not offer).

### Fixed

- A current changed on a charger while Switchboard holds it stopped (in the charger's app, by an automation) is now the level it is resumed at; 0.7.0 took it for a choice to stay stopped and never resumed it. This holds for a switch stop too.

### Compatibility

- A restore baseline of a select-stopped device is written with `option_before` in `state.json` (schema 3). **A downgrade to 0.7.0 sets such a file aside** (control locked off, the kill switch on until confirmed). Let the charger resume, or turn control off and confirm afterwards, before downgrading. Baselines without a select stop are written exactly as 0.7.0 wrote them.
- Mode decisions written by 0.7.1 carry no `ui_choice`; 0.7.0 skips them when it reads the log. Lines of 0.7.0 and older still read.
- A select stop is a setting kept in the charger: it stays at `stop_option` after the add-on stops, while the kill switch is on, when control is switched off or the add-on is uninstalled, until Switchboard resumes it or you change it.

## 0.7.0

Control. Switchboard can now switch the devices you declared, but only when you allow it twice: the add-on option `mode: active` **and** the new **Control allowed** switch, which is off after install and after this update. Until you do both it still controls nothing: shadow stays the default. Not a protective device; your breakers remain the protection.

### Added

- **Control allowed** switch: in the panel (`GET`/`POST api/control`) and as `switch.switchboard_control_allowed` over MQTT; kept in `state.json`. Commands need the option `active`, the switch on, a valid configuration and a confirmed state; `GET api/mode` lists whatever is missing, and every change is a mode decision in the log. Switching it off sends nothing: devices stay where they are, and the panel lists the devices Switchboard left reduced (the one exception: an automatic phase test that has its device on sends that test's own off once).
- Commands: service calls only on the entities you declared as `actuator` or `stop_actuator`, only `turn_on`/`turn_off` on a `switch` or `input_boolean` and `set_value` on a `number` or `input_number` (in the actuator's unit, on its step grid, within its range). Each call is checked again against the configuration loaded at that moment. Nothing else that changes anything in Home Assistant can be sent.
- `"commanded": true` means Home Assistant acknowledged the call; an error, no answer within 5 s or a lost link is logged as not commanded (`device_unresponsive`); only an error counts against the device, a lost link leaves the verdict to its reported state. A device whose state does not show an acknowledged command within its reaction time plus 30 s is flagged and counted as one mismatch. After a reconnect, a switch-off the device does not show is sent again; a switch-off or lowered setpoint that failed or got no answer (from protection, dispatch or the phase test) is sent again at every cycle boundary while the device still shows otherwise.
- Safe state with commands: at start before the first reading, on stale data, Home Assistant disconnected and shutdown, every controlled device is switched off. During an invalid configuration only the offs (and minimum setpoints) of the last valid file are sent. The kill switch sends every off once.
- `start:` per device and `control.devices.default_start`: `restore_only` (default) brings a device back only to the level Switchboard took it from; `headroom` lets dispatch start it with free capacity. What Switchboard took away is remembered until it was really given back: across switching control off and on, and across a restart (in `state.json`), including a switch-off the start-up safe state had to send; a level you changed yourself meanwhile is never raised. A device Switchboard started that you remove from the file is switched off once more (waiting for Home Assistant if it is reconnecting); a device it only restored to your own state is left alone.
- Active phase self-test in Setup: with control in force and headroom on every phase, Switchboard switches one idle relay on and off itself and judges the meter's steps; it stops and switches the device off the moment protection acts anywhere. Phase evidence (passive and active) is kept in `/data/phase-evidence.json`.
- A hand-written `switchboard.yaml` is checked for derived (template) sensors as measurement inputs whenever another configuration is in force (start, every reload), and again every minute while Home Assistant's registry cannot be read: warnings on the Signals page and in the log, never a refusal.
- Reason codes, list version 3: `foreign_load` (a reduction or refused increase caused by a detected foreign load; was `reserve_threshold`) and `change_limit` (an increase deferred while another increase of the settling window is in flight).
- `effector.confirm_timeout` (30 s) in `control:`.

### Changed

- `POST api/mode` is removed; the setup wizard records no mode any more and shows the mode in force after saving.
- An untrusted `state.json` (corrupt or unreadable) starts with control locked off and the kill switch on until you confirm the state in the panel.
- `Mqtt:ShutdownTimeout` 3 s → 2 s, so the last switch-offs of a stop fit the container's stop grace.

### Compatibility

- `state.json` is now written in schema 3 (control switch, observation record, restore baselines). **A downgrade to 0.6.x sets it aside**: the kill switch starts released, the overload budgets full and the manual overrides forgotten. Turn control off before downgrading.
- A 0.6.x `state.json` is read; its UI mode choice is ignored and control starts off.
- Decision log lines written by 0.6.x (reason codes list version 2) still read.
- No new add-on option. New file in `/data`: `phase-evidence.json`.

## 0.6.1

Still shadow only; switches nothing.

### Changed

- The setup wizard refuses a measurement (meter, inverter, device draw) that is not in Home Assistant's entity registry instead of only warning about it. Such a sensor is defined in YAML without a `unique_id`, so the wizard cannot tell whether it is a template sensor; on the maintainer's installation the derived house-load sensor was exactly such an entity. Give the sensor a `unique_id` or pick the primary measurement. A device's state entity outside the registry is still only flagged. A hand-written `switchboard.yaml` is not affected.

## 0.6.0

The setup wizard. Still shadow only: Switchboard **controls nothing** and switches nothing in this version, whatever the `mode` option says, and the wizard never switches anything either. Not a protective device; your breakers remain the protection.

### Added

- **Setup** (screen 03) in the add-on panel: five steps (meter, connection, sources and storage, loads, mode) that edit `/config/switchboard.yaml` for you. A clean install can be configured without touching the file; re-running the wizard opens the existing file with every device, signal, circuit group and `control:` override, and keeps what it does not edit.
- Entity pickers fed by Home Assistant (states, entity registry and device registry, read with read-only queries over a second, on-demand connection that closes when the wizard is idle). Derived sensors (template, statistics, powercalc and similar) are listed but refused as inputs, also when the entity list is too long to show in full; an entity that is not in the entity registry, or that cannot be checked because Home Assistant's list cannot be read, is flagged as unverifiable.
- Live readouts beside each picked entity: its value, the age of its last report and its detected refresh.
- Validation before saving: entities exist and report in the declared unit and device class, values are plausible for the connection, the phases sum to the total (when a total is mapped), and each `max_age` fits the detected refresh (a meter `max_age` far longer than its refresh is flagged too). Every refusal names the field to fix.
- The reserve explained with your own numbers: where a kettle on a fully allocated phase lands on the breaker curve, and the minimum safe reserve derived from the meter's refresh (shown, never enforced).
- Passive phase self-test: switch one declared device yourself, on and then off (in Home Assistant or by hand), while the wizard watches which meter phase steps; switchings the running add-on sees (Node-RED switching a heater, for example) count as evidence too. A verdict needs a switch on and a switch off that agree, and another declared device switching at the same moment makes a switching inconclusive. A swapped phase confirmed both ways blocks the save; a single switching, or a test that proves nothing, is reported as inconclusive, never as a pass and never as a refusal.
- Device templates (EV charger, water-heater element, heat pump or AC, generic on/off) that prefill type, limits and lockouts from the shipped defaults. A device's draw sensor in kW or mA is read in its own unit.
- Saving writes the file atomically after the same parser the add-on uses has accepted it, keeps a timestamped backup next to it (at most 10), and the running add-on reloads it without a restart. A hand-written file's comments cannot be kept by a structured save: the wizard asks first, and the backup keeps them. Unsaved changes survive leaving the panel in the same browser tab.
- The decision log screen pages through the whole retained history in `/data/decisions`, not only the decisions in memory (`GET api/decisions/history`); while `/data` cannot be written, its first page still shows the newest decisions held in memory.
- `GET` and `POST api/mode`: the UI mode choice, persisted in `state.json` and in force without a restart. Only shadow can be chosen in this version; active is refused because commanding is not enabled.

### Compatibility

- `switchboard.yaml` keeps schema version 1: a file the wizard writes is read by 0.5.x too.
- No new add-on option and no new file in `/data`; backups of `switchboard.yaml` are written next to it in the add-on configuration folder.

## 0.5.1

Still shadow only; switches nothing.

### Fixed

- With the car not charging, the go-e's draw sensors fall silent after reporting 0, so the charger's draw was not trusted, the house load showed as unknown on the Dashboard and Flows, and dispatch could not raise the charger. A draw that falls silent after exactly 0 now counts as an idle device at 0 W. A draw that falls silent at any other value is still not trusted.
- A damaged `statistics.json` with absurdly large energy values is now set aside like any other corrupt file instead of breaking the statistics page.

## 0.5.0

The runtime screens. Still shadow only: Switchboard **switches nothing** in this version, whatever the `mode` option says, and every screen says so. Not a protective device; your breakers remain the protection.

### Added

- New screens in the add-on panel, with the mockup's navigation: **Dashboard** (phase bars, power balance, allocation queue, reserve banner), **Flows** (where the power comes from and goes, per phase), **Device card** (connection, value per kWh, state, history of changes and their checks), **Limits and overloads** and **Decision log** (filters, signal health, daily summary). Signals and Status stay under Diagnostics. Setup and the day plan are listed as not in this version.
- Light and dark theme: follows the system, with a toggle kept per browser. Usable on a phone.
- `GET api/power`, `api/statistics`, `api/excursions` and `api/devices` for those screens (read only, ingress only); `api/decisions` filters by kind.
- Energy statistics: the power picture of every control step is integrated in 5-minute buckets on Switchboard's own clock (never read from the Recorder). Time without valid data is counted as unobserved, never as zero. Kept for 26 hours in `/data/statistics.json`, written when a bucket closes and at shutdown, so a crash loses at most the open bucket. A damaged file is set aside as `statistics.json.corrupt` and the statistics start fresh; it never stops the add-on.
- The last and the worst excursion above the reserve threshold per phase, for the Limits chart, in `/data/excursions.json`.
- The daily summary counts possible breaker trips: a phase voltage lost on one or two phases while the others stay up and Home Assistant is connected (a loss of one grid phase looks the same). A loss on every phase at once is counted as a supply outage, because a grid outage and a trip of a multi-pole breaker cannot be told apart. A trip that also stops Home Assistant or the meter cannot be seen, and without a phase voltage signal trips are shown as not measured, never as 0. Savings are not shown (they need a tariff, planned for v2); the value of the energy delivered at each device's `value_per_kwh` is shown without a currency.
- `devices[].name` in `switchboard.yaml`: an optional display name (1 to 40 characters). It never takes part in a decision. The example configuration names its devices.

### Changed

- The dashboard is the home page; the status page moved to Diagnostics → Status.
- The add-on log says the guard *watches* the declared devices (it said *protects*): Switchboard is not a protective device.

### Downgrade

- 0.4.x ignores `statistics.json` and `excursions.json`.
- 0.4.x refuses `name:` under `devices:` (unknown key): the configuration becomes invalid and the guard holds every device in safe state. Remove the names before going back.

## 0.4.1

Still shadow only; switches nothing.

### Fixed

- At night dispatch had no data and could raise no device: an inverter that reports 0 W at dusk and then falls silent until morning (SolarEdge) counted as an unknown production. With `unavailable_as_zero: true` it now counts as 0 W, like an inverter that goes `unavailable`. An inverter that falls silent while producing still counts as unknown.

### Changed

- Example configuration: the go-e charger's setpoint uses `state_reports: on_change`, because the go-e reports it only when it changes. Its draw sensors stay age-based, so an idle charger's draw is not trusted (no relief, no increases).

## 0.4.0

Dispatch, in shadow mode only. Switchboard now also works out which device it would start, raise, lower or stop to use the headroom the guard leaves, and records it, but it **still switches nothing**: dispatch decisions are computed and logged, no command is sent to any device, whatever the `mode` option says (with `active` it stays in shadow mode and says that commanding is not enabled). Not a protective device; your breakers remain the protection.

### Added

- Dispatch: per phase it fills the headroom up to the reserve threshold, worth the most per kWh first (the reverse of the shed order), with separate start and stop thresholds, six calm cycles before a start, one increase in flight at a time, one stage per cycle, and relay lockouts. A change a lockout refuses is recorded once as deferred; a reduction is carried out when the lockout ends, a start or increase at the next cycle if the guard still allows it. After each change it waits for the meter to report again (settling window, at most 90 s) before the next increase. For every device it keeps what it would have it at next to what Home Assistant reports, and records an intention only when it changes. Devices the guard held off (safe state, kill switch, overload) are brought back as recorded starts, most valuable first, one at a time.
- Effector verification: when someone else switches a device, the meter's (or the device's own) measured step is recorded next to the expected one; in shadow mode nothing of Switchboard's is verified, because nothing was applied.
- Foreign load events: power that appears on a phase without a change of Switchboard's.
- Device type `storage` (a battery's charging current; only charging is ever planned, a battery discharging on its own is left alone, and a planned charge is budgeted including the discharge it ends) and new device keys: `draw` (the device's own current and/or power sensor), `state_reports: on_change` (a relay that Home Assistant reports only when it changes), `stop_actuator` (a switch that stops a modulating device), `rotation_group` (identical elements on different phases, started on the phase with the most headroom first) and `circuit_group`.
- `circuit_groups:` in `switchboard.yaml`: a downstream breaker with its own rating, tier table and overload budget, enforced alongside the main limit on its members only. An optional sub-meter must never read negative; a negative reading holds the members off.
- Manual override per device (force on, force off, automatic) on the Limits page and as `select.switchboard_device_<id>_override`; it outranks dispatch, never protection, and survives a restart.
- `sensor.switchboard_device_<id>_dispatch`: what Switchboard would have the device at, with the reported state, reason, deferral, settling window and override as attributes.
- The **Signals** page lists each device's own draw signals; while one is not fresh its device is shown as *not trusted* (no relief, no increases). A stale draw holds nothing off.
- The Limits page shows dispatch: the headroom it works with per phase, and per device the intended state next to the reported one, what it waits for, the override and the last check of a change.

### Changed

- How much a reduction relieves is counted only where it can be real: never more than a device's measured draw, nothing for a setpoint without a draw (a charger at 20 A that draws nothing relieves nothing), a relay's rating only while it reports on. In 0.3.x an idle charger's setpoint could hide a later overload from the guard.
- A modulating device without a stop actuator no longer takes a shed step that could not happen, and a device whose own draw shows it relieves nothing (an idle charger at a high setpoint) is shed only after the devices that really draw; the next real candidate is shed instead.
- "Nothing to reduce: the phase is exporting" is recorded once per export episode.
- `sensor.switchboard_last_decision` shows `kill_switch_disengaged` or `safe_state_exited` when a kill switch or a safe state ends, instead of the reason of the condition that ended.
- The decision log records who decided (guard, dispatch or override), the circuit group and the intended level; lines written by 0.3.x stay readable.
- The example configuration declares each heater element on the meter phase its switching moves in the recorded history, the heaters as `on_change` in a rotation group (instead of `state_max_age: 24h`) rated 7.0 A / 1700 W as measured, and the charger's own draw.

### Downgrade

- Going back to 0.3.x keeps `state.json` as long as no override is set, no circuit group is declared and no storage device was discharging when it was last saved; otherwise 0.3.x sets it aside as damaged and starts with full overload budgets and the kill switch off.

## 0.3.1

Still shadow only; controls nothing.

### Fixed

- Switchboard entities whose value never changes (overload budget at 100 %, recovery time 0, mode, kill switch) stayed `unknown` in Home Assistant: their first state could reach Home Assistant before the entity existed. Every entity state is now sent again with each heartbeat (10 s).

### Changed

- The overload budget thresholds (`budget.stop_increases_below`, `budget.shed_one_below`) can be raised in `control:` but no longer lowered below the built-in values.

## 0.3.0

Guard, in shadow mode only. Switchboard now decides what it would do to protect each phase and records it, but it **still controls nothing**: no device is switched or changed, whatever the `mode` option says. Not a protective device; your breakers remain the protection.

### Added

- `devices:` in `switchboard.yaml`: the loads Switchboard may one day control (`onoff`, `stepped` or `modulating`), each with its actuator, phase, rated current, value per kWh and optional shed priority, `no_shed` and lockouts. The state of each device is read from Home Assistant; a state not reported within `state_max_age` makes the device unsafe.
- Breaker model per phase: the tier table for your breaker characteristic (B, C, or `unknown`, which allows no current above the rated one indefinitely) and an overload budget that is spent above the continuous limit and refills slowly below it.
- Guard: for every meter sample it works out which device it would shed, and how fast, from the tier the phase current is in and the budget; only devices on the overloaded phase, cheapest first, a charger first reduced to its minimum. It would hold every device off when data is missing or stale, when Home Assistant is disconnected, when `switchboard.yaml` is missing or invalid, and when the add-on stops. Held devices are let go only after several calm cycles.
- Kill switch: `switch.switchboard_kill_switch` (also through the API) holds every device off within one evaluation and survives a restart. In this version it, too, only records what it would hold off.
- Decision log: every decision with its reason code and the inputs behind it, one file per UTC day under `/data/decisions/` (30 days by default). Every entry says it was not commanded.
- `/data/state.json` keeps the kill switch, the overload budgets and the last known device states across restarts. Lockouts always restart with the add-on.
- MQTT entities per phase (headroom in A and W, headroom to the continuous limit, overload budget, recovery time, guard state), per device (guard state) and global (mode, last decision, kill switch).
- **Limits** page: per phase current, tier, budget with recovery time and headroom; devices in shed order with their guard state; safe-state and kill-switch banners; the last 50 decisions as sentences; the tier table.
- The **Signals** page lists the devices that depend on each signal.

### Changed

- The breaker tier table and the budget's recovery time can be tightened under `control:`, but an override that loosens them past the built-in safety limits is refused with its line number.
- The minimum safe reserve counts one more meter refresh of blind time, because a meter's current can lag its own power by one poll.

## 0.2.0

Signals. Reads and checks the meter; still controls nothing: no device is switched, whatever the mode option says.

### Added

- Configuration file `switchboard.yaml` in the app's configuration folder (`/addon_configs/<id>_switchboard/`): connection (topology, breaker, voltage, reserve per phase) and the meter signals, validated with line numbers for every problem and reloaded without a restart. An invalid or missing file means no meter data counts as valid.
- Meter signals from Home Assistant: unit and sign normalization, direction of unsigned currents from the power of the same phase (cautious mode when unknown), freshness from `last_reported`, staleness after `max_age` (24 h at most by default).
- Detected refresh interval per signal and the minimum safe reserve per phase derived from it.
- House load per phase derived from the meter and the inverter. A sample that makes the balance impossible (a negative house load) is refused and counted; the previous value stays in use until it ages out.
- **Signals** page listing every signal with entity, value, age and state, and per phase whether the meter data is valid, the house load, the balance check with its age, and the reserve against its derived minimum.

### Changed

- The web page is checked for a newer version on every load, so after an update the browser shows the new page without a manual reload.

## 0.1.0

Stage 0 skeleton. Controls nothing.

### Added

- Add-on packaging: custom repository, ingress page (sidebar or Open Web UI), amd64 and aarch64 images.
- Connection to Home Assistant over the Supervisor WebSocket, with keepalive and reconnect.
- MQTT discovery entity `binary_sensor.switchboard_healthy`, with MQTT last will and `expire_after`, so your own automation can react when Switchboard is not running.
- Status page through ingress (sidebar or Open Web UI): mode, Home Assistant and MQTT connection, health, version.
- `mode` option, `shadow` by default.
- Standalone Docker path configured through environment variables.
