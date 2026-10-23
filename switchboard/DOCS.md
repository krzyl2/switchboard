# Switchboard

Switchboard distributes the capacity of your grid connection across high-power loads you control — EV chargers, water-heater elements, heat pumps, batteries. Its first job is to keep every phase under its main breaker by shedding loads in an order you choose; its second is to send the remaining headroom to whichever load a kWh is worth the most to. It reads your meter through Home Assistant and switches only the entities you declare yourself. It is best-effort load management, not a protective device: read [This is not a protective device](#this-is-not-a-protective-device) before you rely on it.

## This is not a protective device

Switchboard is best-effort load management based on data from Home Assistant. Sensor data can be late, wrong or missing, Home Assistant can restart, and this add-on can fail. It does **not** replace circuit breakers, residual-current devices, EVSE protection or any other electrical safety equipment. Never size, wire or protect a circuit on the assumption that Switchboard works.

## Status of this version (0.1.0)

0.1.0 is the skeleton release. It:

- installs and starts as an add-on,
- connects to Home Assistant over the Supervisor WebSocket,
- publishes one entity over MQTT discovery: `binary_sensor.switchboard_healthy`,
- serves a status page through ingress (sidebar or **Open Web UI**): mode, Home Assistant and MQTT connection, health, version.

It **controls nothing**. It sends no commands to any Home Assistant entity, in either mode. Meter mapping, devices, limits and the decision log arrive in later versions.

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

```sh
docker run -d --name switchboard --restart unless-stopped \
  -e SWITCHBOARD_HA_URL=http://192.168.1.10:8123 \
  -e SWITCHBOARD_HA_TOKEN=your-long-lived-token \
  -e SWITCHBOARD_MQTT_HOST=192.168.1.10 \
  -e SWITCHBOARD_MQTT_PORT=1883 \
  -e SWITCHBOARD_MQTT_USERNAME=switchboard \
  -e SWITCHBOARD_MQTT_PASSWORD=your-broker-password \
  -v switchboard-data:/data \
  ghcr.io/krzyl2/switchboard:0.1.0
```

The MQTT integration in Home Assistant must use the same broker, so that the health entity appears. The status page is built for ingress and is not reachable from outside the container in standalone mode in this version; use the health entity and the container log (`docker logs switchboard`) instead.

## Support

Report problems at <https://github.com/krzyl2/switchboard/issues>. Attach the Switchboard log at `debug` level; it never contains tokens or passwords.
