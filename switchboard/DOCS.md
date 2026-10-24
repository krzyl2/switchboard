# Switchboard

Switchboard distributes the capacity of your grid connection across high-power loads you control — EV chargers, water-heater elements, heat pumps, batteries. Its first job is to keep every phase under its main breaker by shedding loads in an order you choose; its second is to send the remaining headroom to whichever load a kWh is worth the most to. It reads your meter through Home Assistant and switches only the entities you declare yourself. It is best-effort load management, not a protective device: read [This is not a protective device](#this-is-not-a-protective-device) before you rely on it.

## This is not a protective device

Switchboard is best-effort load management based on data from Home Assistant. Sensor data can be late, wrong or missing, Home Assistant can restart, and this add-on can fail. It does **not** replace circuit breakers, residual-current devices, EVSE protection or any other electrical safety equipment. Never size, wire or protect a circuit on the assumption that Switchboard works.

## Status of this version

This version:

- installs and starts as an add-on,
- connects to Home Assistant over the Supervisor WebSocket,
- reads the meter signals you map in `switchboard.yaml` (see [Configuration file](#configuration-file-until-the-setup-wizard-exists)) and shows each one on the **Signals** page with its value, age and state,
- publishes one entity over MQTT discovery: `binary_sensor.switchboard_healthy`,
- serves a status page through ingress (sidebar or **Open Web UI**): mode, Home Assistant and MQTT connection, health, version.

It **controls nothing**. It sends no commands to any Home Assistant entity, in either mode. Limits, devices, shedding and the decision log arrive in later versions.

## Requirements

- Home Assistant OS or Supervised (apps need the Supervisor). For Home Assistant Container or Core, see [Standalone Docker](#standalone-docker).
- An MQTT broker provided by an app — normally the official **Mosquitto broker** — and the **MQTT** integration set up in Home Assistant. The Supervisor hands the broker credentials to Switchboard; there is nothing to enter.

## Installation

Home Assistant now calls add-ons *apps*; Switchboard is one.

1. **Settings → Apps → Install app → ⋮ → Repositories**, add `https://github.com/krzyl2/switchboard`.
2. Install **Switchboard** from the store.
3. On the app's **Info** tab, keep **Start on boot** on, and turn on **Watchdog** (restarts Switchboard if it stops unexpectedly) and **Show in sidebar**; both are off after a fresh install. Then start it.
4. Open **Switchboard** from the sidebar (or **Open Web UI** on the Info tab). Within a few seconds the status page shows Home Assistant and MQTT as connected and the health entity as healthy.

## Options

| Option | Values | Default | Meaning |
|---|---|---|---|
| `mode` | `shadow`, `active` | `shadow` | `shadow` computes and logs decisions without switching anything. `active` is reserved for a later version; 0.1.0 contains no command path and switches nothing. |
| `log_level` | `debug`, `info`, `warning` | `info` | How much detail goes into the add-on log. |

### Shadow mode is the default

Software that switches 5 kW loads must prove itself before it acts. A fresh install runs in `shadow`: it computes and logs what it would do and switches nothing. Anything other than exactly `active` — a missing, empty or unknown value — is treated as `shadow`. In 0.1.0 there is nothing to switch, and the log says so if you select `active`.

## Configuration file (until the setup wizard exists)

The meter mapping and the connection live in one YAML file, `switchboard.yaml`, in the app's own configuration folder. There is no setup wizard yet, so you write this file by hand: create or edit it with the **Studio Code Server** or **Samba share** app. It is included in Home Assistant backups.

- On the Home Assistant host it is `/addon_configs/<id>_switchboard/switchboard.yaml`. `<id>` depends on the repository you installed from: in Studio Code Server or the Samba share, open the `addon_configs` folder and use the one whose name ends in `_switchboard`.
- Changes are picked up within a few seconds; no restart is needed.
- If the file is missing or has an error, **no meter data counts as valid** until it is fixed. The **Signals** page and the app log list every problem with its line number. A misspelt key is an error, never silently ignored.

A complete example for a three-phase SolarEdge installation with an M1 meter is in the repository: [`deploy/examples/switchboard.yaml`](https://github.com/krzyl2/switchboard/blob/main/deploy/examples/switchboard.yaml). The shortest valid file:

```yaml
schema_version: 1
connection:
  topology: three_phase        # three_phase | three_phase_summed | single_phase
  breaker_rating_a: 25
  characteristic: B            # B | C | unknown
  voltage_v: 230
  reserve_w: { L1: 2500, L2: 2500, L3: 2500 }   # optional; kept free for loads Switchboard does not control
signals:
  grid_current_l1:
    entity: sensor.meter_current_l1
    kind: current
    unit: A
    sign: positive_import
    max_age: 90s
    role: phase_current
    phase: L1
  # ... grid_current_l2 and grid_current_l3 likewise
```

Each signal maps one Home Assistant entity:

| Key | Meaning |
|---|---|
| `entity` | The entity ID. One entity feeds one signal. |
| `kind` | `current` (A, mA), `power` (W, kW), `voltage` (V), `energy_counter` (Wh, kWh, MWh), `percentage` (%), `state` (no unit). |
| `unit` | The unit the entity reports. A sample whose `unit_of_measurement` differs is refused rather than converted on a guess. |
| `sign` | Current and power only. Seen from your house: `positive_import` (positive = power flowing in, from the grid or from your inverter), `positive_export` (positive = flowing out; SolarEdge M1 per-phase power), or `unsigned` (a magnitude only). |
| `direction_from` | Required for `unsigned`: the signed power signal of the **same phase** that gives the direction. `none` declares there is none: the value then always counts as import (cautious mode), and Switchboard cannot tell export from import. |
| `max_age` | The signal is stale when Home Assistant has not reported it for this long (`90s`, `2m`). A value that simply repeats stays fresh: Switchboard checks its `last_reported`. Keep `max_age` above twice the meter's refresh plus 10 s (a repeating value is confirmed half-way through `max_age`, and the check may take up to 5 s); `90s` suits meters that refresh every 10–30 s. At most `24h` unless `control.signals.max_age_limit` allows more. |
| `role` | What the signal is used for: `phase_current` (required on every phase; a `three_phase_summed` connection needs `total_current` instead), `phase_power`, `phase_voltage`, `inverter_power`, `inverter_phase_current`, `total_current`, `total_power`. A signal without a role is only shown. |
| `phase` | `L1`, `L2` or `L3`, for per-phase roles. |
| `sigma` | Optional measurement noise in the signal's unit. |
| `unavailable_as_zero` | `inverter_power` only: an inverter that goes `unavailable` at night produces 0 W instead of an unknown amount. |

Map **primary measurements only**: the meter's and the inverter's own entities. Never map a template, derivative, statistics or other calculated sensor (for example a "house consumption" computed as production minus grid): such sensors combine sources sampled at different moments and produce false dips. Switchboard derives house load itself.

`control:` overrides the built-in control parameters key by key; the defaults are documented in the repository (`src/Switchboard.Infrastructure/Configuration/control-defaults.yaml`). An unknown key there is an error too.

### The Signals page

Open Switchboard's web UI and choose **Signals** at the top of the page. It refreshes every two seconds and shows:

- **Configuration**: whether `switchboard.yaml` is valid, its path inside the app (`/config/switchboard.yaml`), when it was loaded, and every error or warning with its line number and the same message the log writes.
- **Phases**: per phase, whether its meter data is valid (and if not, why), the current with its direction (import or export), grid power, inverter generation, the derived house load, the balance check (the last residual with its age; a sample that fails it is counted and the previous value stays in use until it ages out), and the reserve you configured next to the minimum derived from your meter's refresh interval.
- **Signals**: every signal with its entity ID, the value Switchboard uses (A, W, V; import positive), the raw value as Home Assistant reported it, its age since Home Assistant last reported it, its state — *fresh*, *stale* (no report within `max_age`), *rejected* (the last sample was unusable, for example in another unit than declared; the reason is shown, and the signal is not used until a usable sample arrives), *unavailable* or *unmapped* (an optional slot you did not map) — the detected refresh interval, and where its direction comes from. *unknown — cautious mode* means the direction is not known right now and the value counts as import.

If the page cannot reach the app it says **Signals unavailable** and hides every value, so a frozen page never looks like a calm meter.

## The health entity

`binary_sensor.switchboard_healthy` (name **Switchboard Healthy**) exists so that **your own** Home Assistant automation can shed loads when Switchboard is not running properly.

| State | Meaning |
|---|---|
| `on` | Switchboard is running and connected to both Home Assistant and the MQTT broker. Refreshed every 10 seconds. |
| `off` | Switchboard is running but has lost its Home Assistant connection, or is shutting down. |
| `unavailable` | Switchboard is not running or has hung, or the MQTT broker (or Switchboard's connection to it) is down. If the process dies, the broker publishes its MQTT last will; if it hangs and stops refreshing, the state expires after at most 30 seconds. A normal stop also ends here. |
| `unknown` | Home Assistant has just started and no update has arrived yet. |

Treat anything other than `on` as "Switchboard is not looking after the loads".

### Example: shed your loads when Switchboard is not healthy

Switchboard does not install this — add it yourself if you want it, and replace `switch.your_heater` and `switch.your_ev_charger` with your own entities. It turns them off once the health entity has not been `on` for 60 seconds, and keeps turning them off every minute while that lasts — including after a Home Assistant restart.

The 60 seconds are counted by a template helper, from the last time the health entity was `on`. A state trigger or condition with `for:` on the health entity itself is not enough: its count starts again on every change between `off`, `unavailable` and `unknown`, so a Switchboard that keeps crashing and restarting would never trip it.

Add the helper to `configuration.yaml` and restart Home Assistant:

```yaml
template:
  - binary_sensor:
      - name: "Loads unattended by Switchboard"
        state: "{{ not is_state('binary_sensor.switchboard_healthy', 'on') }}"
        delay_on:
          seconds: 60
```

Then add the automation:

```yaml
alias: "Switchboard not healthy: shed my loads"
description: "Example from the Switchboard documentation. Replace the placeholder entities."
mode: single
triggers:
  - trigger: state
    entity_id: binary_sensor.loads_unattended_by_switchboard
    to: "on"
  - trigger: time_pattern
    minutes: "/1"
conditions:
  - condition: state
    entity_id: binary_sensor.loads_unattended_by_switchboard
    state: "on"
actions:
  - action: switch.turn_off
    target:
      entity_id:
        - switch.your_heater
        - switch.your_ev_charger
```

## Network and privacy

- No network port is exposed. The panel is reached only through Home Assistant ingress, behind Home Assistant's own login.
- Outbound connections go only to Home Assistant (through the Supervisor), to the MQTT broker and to the Supervisor API. There is no telemetry and no cloud service.
- Switchboard publishes MQTT messages only under its own `switchboard/` topics and its own discovery topics under `homeassistant/`.
- It never reads the Home Assistant Recorder database.

## Standalone Docker

For Home Assistant Container or Core, the same image runs under plain Docker. This path is documented but secondary; the add-on is the supported way to run Switchboard.

Without a Supervisor, settings come from environment variables:

| Variable | Required | Meaning |
|---|---|---|
| `SWITCHBOARD_HA_URL` | yes | Home Assistant base URL, e.g. `http://192.168.1.10:8123`. The WebSocket address is derived from it (`https` becomes `wss`); a `ws://` or `wss://` URL is used as given. |
| `SWITCHBOARD_HA_TOKEN` | yes | A long-lived access token (your profile → **Security** in Home Assistant). |
| `SWITCHBOARD_MQTT_HOST` | yes | MQTT broker host. |
| `SWITCHBOARD_MQTT_PORT` | yes | MQTT broker port, usually `1883`. |
| `SWITCHBOARD_MQTT_USERNAME` | no | Broker user name. |
| `SWITCHBOARD_MQTT_PASSWORD` | no | Broker password. |
| `SWITCHBOARD_MODE` | no | `shadow` (default) or `active`. |
| `SWITCHBOARD_LOG_LEVEL` | no | `debug`, `info` (default) or `warning`. |
| `SWITCHBOARD_CONFIG_PATH` | no | Where `switchboard.yaml` is read from; default `/config/switchboard.yaml`. |

```sh
docker run -d --name switchboard --restart unless-stopped \
  -e SWITCHBOARD_HA_URL=http://192.168.1.10:8123 \
  -e SWITCHBOARD_HA_TOKEN=your-long-lived-token \
  -e SWITCHBOARD_MQTT_HOST=192.168.1.10 \
  -e SWITCHBOARD_MQTT_PORT=1883 \
  -e SWITCHBOARD_MQTT_USERNAME=switchboard \
  -e SWITCHBOARD_MQTT_PASSWORD=your-broker-password \
  -v switchboard-data:/data \
  -v /path/to/switchboard-config:/config \
  ghcr.io/krzyl2/switchboard:0.1.0
```

The MQTT integration in Home Assistant must use the same broker, so that the health entity appears. The status page is built for ingress and is not reachable from outside the container in standalone mode in this version; use the health entity and the container log (`docker logs switchboard`) instead.

## Support

Report problems at <https://github.com/krzyl2/switchboard/issues>. Attach the Switchboard log at `debug` level; it never contains tokens or passwords.
