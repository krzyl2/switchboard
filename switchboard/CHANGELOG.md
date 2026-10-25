# Changelog

All notable changes to this add-on. Newest version first; format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

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
