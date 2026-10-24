# Changelog

All notable changes to this add-on. Newest version first; format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

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
