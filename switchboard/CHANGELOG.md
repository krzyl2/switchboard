# Changelog

All notable changes to this add-on. Newest version first; format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

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
