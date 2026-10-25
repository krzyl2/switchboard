# Switchboard

Switchboard distributes the capacity of your grid connection across high-power loads you control — EV chargers, water-heater elements, heat pumps, batteries. Its first job is to keep every phase under its main breaker by shedding loads in an order you choose; its second is to send the remaining headroom to whichever load a kWh is worth the most to. It reads your meter through Home Assistant and switches only the entities you declare yourself. It is best-effort load management, not a protective device: read [This is not a protective device](#this-is-not-a-protective-device) before you rely on it.

## This is not a protective device

Switchboard is best-effort load management based on data from Home Assistant. Sensor data can be late, wrong or missing, Home Assistant can restart, and this add-on can fail. It does **not** replace circuit breakers, residual-current devices, EVSE protection or any other electrical safety equipment. Never size, wire or protect a circuit on the assumption that Switchboard works.

## Status of this version

This version:

- installs and starts as an add-on,
- connects to Home Assistant over the Supervisor WebSocket,
- has a **setup wizard** that writes `switchboard.yaml` for you (see [Setup wizard](#setup-wizard)); the file can still be written by hand (see [Configuration file](#configuration-file)),
- reads the meter signals you map in `switchboard.yaml` and shows each one on the **Signals** page with its value, age and state,
- reads the devices you declare there and runs the **guard** on them: per phase (and per circuit group) it compares the measured current with your breaker's limits and works out, every time a new meter sample arrives, which device it *would* reduce or switch off, and why (see [Devices and the guard](#devices-and-the-guard-shadow-only)),
- runs **dispatch** on the same devices: it works out which device it *would* start, raise, lower or stop to use the headroom the guard leaves, worth the most per kWh first, and keeps for every device what it would have it at next to what Home Assistant reports (see [Dispatch](#dispatch-computed-and-logged-nothing-switched)),
- takes a **manual override** per device (force on, force off, automatic) from the web UI or a Home Assistant select entity; protection still outranks it,
- writes every decision, with its reason, to a decision log,
- publishes over MQTT discovery the health entity `binary_sensor.switchboard_healthy`, the guard's entities (headroom, overload budget, recovery time, guard state per phase; mode, last decision, kill switch; one state entity per device) and per device a dispatch state and an override select,
- serves a status page through ingress (sidebar or **Open Web UI**): mode, Home Assistant and MQTT connection, health, version.

It **controls nothing**. Dispatch decisions are computed and logged; **Switchboard still switches nothing**. It sends no commands to any Home Assistant entity, in either mode: this version contains no way to. Everything the guard and dispatch decide is recorded as "would switch off", "would start", "would set 10 A", never carried out, and every decision says `"commanded": false`. Switching arrives in a later version, and only after the decision log has been reviewed in shadow mode.

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
| `mode` | `shadow`, `active` | `shadow` | `shadow` computes and logs decisions without switching anything. `active` is reserved for a later version: commanding is not enabled in this one, so it runs in shadow mode even with `active` (the status page and `sensor.switchboard_mode` say `shadow`, and the Limits page and the decision log note that commanding is not enabled). |
| `log_level` | `debug`, `info`, `warning` | `info` | How much detail goes into the add-on log. |

### Shadow mode is the default

Software that switches 5 kW loads must prove itself before it acts. A fresh install runs in `shadow`: it computes and logs what it would do and switches nothing. Anything other than exactly `active` — a missing, empty or unknown value — is treated as `shadow`. In this version there is nothing that could switch, and the log says so if you select `active`.

## Setup wizard

**Setup** in the add-on panel configures Switchboard in five steps, in this order: the main meter, the connection (breaker and reserve), sources and storage (PV inverter, battery), the loads, and the mode. It edits `switchboard.yaml` for you, so a new installation needs no file editing. It switches nothing, ever.

- **Entity pickers** list what Home Assistant offers, filtered by domain and device class. Switchboard reads the list with read-only queries over a second connection that it closes again when the wizard is idle. A sensor computed from other sensors (template, statistics, powercalc and similar) is listed but cannot be picked as an input, with the reason shown; an entity that is not in Home Assistant's entity registry is flagged as unverifiable.
- **Live readouts** beside every picked entity show its value, the age of its last report and its detected refresh, so a wrong pick is visible at once.
- **Checks** before you continue: the entities exist and report in the declared unit and device class, the values are plausible for your breaker, the phases sum to the total (when you map a total; otherwise the check says *skipped*, never *passed*), and each `max_age` fits the detected refresh. Every refusal names the field to fix and the wizard takes you to it.
- **The reserve** is explained with your own numbers (where a kettle on a fully allocated phase would land on the breaker curve), next to the minimum safe reserve derived from your meter's refresh. It is shown, never enforced.
- **Phase assignment test** (passive): Switchboard does not switch the device for this test. You switch one declared on/off device yourself, on and then off, in Home Assistant or by hand, while the wizard watches which meter phase steps. Switchings the running add-on sees (Node-RED switching a heater, for example) count too. A verdict needs a switch on and a switch off that agree; a single switching, another device switching at the same moment, no step at all or a step too small for the meter's noise are reported as *inconclusive*, never as a pass. A swapped phase confirmed both ways blocks saving until the phase (or the meter mapping) is fixed.
- **Loads** start from template cards (EV charger, water-heater element, heat pump or AC, generic on/off) that prefill type, limits and lockouts from the shipped defaults. Only you declare an actuator: discovery suggests, you pick.
- **Mode**: *shadow* is the only mode offered in this version; *active* is shown but cannot be chosen while commanding is not enabled. Before finishing you confirm that Switchboard is not a protective device.

**Saving** writes `switchboard.yaml` in one step after the same parser the add-on runs has accepted it, keeps the previous file next to it as `switchboard.yaml.<UTC time>.bak` (the newest 10 are kept), and the running add-on loads the new file without a restart. A file you wrote by hand keeps every device, signal, circuit group and `control:` override when you re-run the wizard, and keys the wizard does not edit are kept as written; its comments cannot be kept by a structured save, so the wizard asks first and the backup keeps them. Unsaved changes survive leaving the panel (to switch a device in Home Assistant, for example) in the same browser tab.

## Configuration file

The meter mapping and the connection live in one YAML file, `switchboard.yaml`, in the app's own configuration folder. The [setup wizard](#setup-wizard) writes it for you; you can also create or edit it by hand with the **Studio Code Server** or **Samba share** app. It is included in Home Assistant backups.

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
| `unavailable_as_zero` | `inverter_power` only: an inverter asleep at night produces 0 W instead of an unknown amount. Asleep means it goes `unavailable`, or it falls silent after reporting 0 W (SolarEdge does this). Silence after a production stays unknown. |

Map **primary measurements only**: the meter's and the inverter's own entities. Never map a template, derivative, statistics or other calculated sensor (for example a "house consumption" computed as production minus grid): such sensors combine sources sampled at different moments and produce false dips. Switchboard derives house load itself.

`control:` overrides the built-in control parameters key by key; the defaults are documented in the repository (`src/Switchboard.Infrastructure/Configuration/control-defaults.yaml`). An unknown key there is an error too. The breaker tier table and the overload budget's recovery time and thresholds may be made stricter, never looser than the built-in safety limits (for example, everything controlled is shed above 1.45 × In, the budget refills no faster than over 600 s, and increases stop below 50 % budget at the latest); an override past them is an error naming its line.

### The Signals page

Open Switchboard's web UI and choose **Signals** at the top of the page. It refreshes every two seconds and shows:

- **Configuration**: whether `switchboard.yaml` is valid, its path inside the app (`/config/switchboard.yaml`), when it was loaded, and every error or warning with its line number and the same message the log writes.
- **Phases**: per phase, whether its meter data is valid (and if not, why), the current with its direction (import or export), grid power, inverter generation, the derived house load, the balance check (the last residual with its age; a sample that fails it is counted and the previous value stays in use until it ages out), and the reserve you configured next to the minimum derived from your meter's refresh interval.
- **Signals**: every signal with its entity ID, the value Switchboard uses (A, W, V; import positive), the raw value as Home Assistant reported it, its age since Home Assistant last reported it, its state — *fresh*, *stale* (no report within `max_age`), *rejected* (the last sample was unusable, for example in another unit than declared; the reason is shown, and the signal is not used until a usable sample arrives), *unavailable* or *unmapped* (an optional slot you did not map) — the detected refresh interval, where its direction comes from, and the devices that depend on it (while it is not fresh they count as unsafe and are held off; a device's own `draw` is the exception: while it is not fresh the device is shown as *not trusted*, relieves nothing and gets no increase, but is not held off). *unknown — cautious mode* means the direction is not known right now and the value counts as import.

If the page cannot reach the app it says **Signals unavailable** and hides every value, so a frozen page never looks like a calm meter.

## Devices and the guard (shadow only)

The guard is Switchboard's protection loop. In this version it **only observes**: it decides what it would do and records it, and it commands nothing. Use it to see, in your own house and before anything is ever switched, whether its decisions are the ones you would make.

### Declaring devices

Devices go under `devices:` in `switchboard.yaml`. The `actuator` (and a `stop_actuator`, where declared) is the only entity that may ever be commanded for that device (by a later version, never by this one); Switchboard never touches an entity you have not declared. Each device's current state is read from its actuator, or from `state_entity`, and must be reported by Home Assistant within `state_max_age` (or, with `state_reports: on_change`, simply be available); otherwise the device counts as possibly on.

**Declare each device on the phase whose meter current it loads.** A device declared on the wrong phase makes the guard shed it for an overload it cannot relieve, while the device that does load the phase keeps running. Check it once: switch one element on by hand and see which phase's current rises on the **Signals** page.

```yaml
devices:
  - id: heater_l1                # lower-case letters, digits, '_'; unique
    type: onoff                  # onoff | stepped | modulating
    actuator: switch.heater_1    # onoff, stepped: switch. or input_boolean.
    phase: L1                    # L1 | L2 | L3 | three_phase
    rated_current_a: 6.6         # per phase, when on
    rated_power_w: 1500
    value_per_kwh: 0.35          # what a kWh is worth to this device; lower sheds first
    state_reports: on_change     # a relay that reports only when it changes (Modbus)
  - id: ev_charger
    type: modulating
    actuator: number.ev_current  # modulating: number. or input_number. (a current setpoint)
    phase: L1
    min: 6                       # the setpoint range, in A (unit: mA if the entity uses mA)
    max: 20
    step: 1
    value_per_kwh: 2.03
    draw:                        # what the charger itself measures it draws
      current: sensor.ev_current_l1   # A
      power: sensor.ev_power_l1       # W, positive = consumption
      max_age: 60s
```

| Key | Meaning |
|---|---|
| `type` | `onoff` (one relay), `stepped` (several relays switched as stages, under `stages:`, each with its own `actuator`, `phase`, `rated_current_a`, `rated_power_w`), `modulating` (a current setpoint between `min` and `max` in whole `step`s) or `storage` (a battery's current setpoint; `min` ≤ 0 < `max`, positive charges; Switchboard only ever plans charging, between 0 and `max`, never discharge. A battery that its own system is discharging is left alone unless a charge fits, and a planned charge is budgeted for the whole step: the discharge it ends plus the charge). |
| `state_entity` | Optional; where the device's current state is read when it is not the actuator itself (`switch.`, `input_boolean.`, `binary_sensor.` for relays; `number.`, `input_number.`, `sensor.` in A or mA for modulating and storage devices). |
| `state_max_age` | Optional, default `10m`. A state Home Assistant has not reported for this long is stale: the device then counts as on (the worst case) for shedding, never as relief, and the guard holds it off. |
| `state_reports` | Optional, `age` (default) or `on_change`. `on_change` is for a relay that Home Assistant reports only when it changes (Modbus switches do): its state counts as fresh for as long as it is available, without an age limit; `unavailable`, `unknown`, a missing entity or a lost connection to Home Assistant still make it unknown. Not together with `state_max_age`. It applies to a device's state only, never to a meter signal. |
| `draw` | Optional; the device's own measurement of what it draws: `current` (A, or `current_unit: mA`) and/or `power` (W, or `power_unit: kW`, positive = consumption), each one `sensor.` entity (for a device on `three_phase`, one per phase: `{ L1: sensor…, L2: …, L3: … }`), and `max_age` (default `devices.draw_max_age`, 90 s). A direct sensor of the device only, never a template. Used to tell how much a reduction really relieves, to check that the device followed a change, and to explain the house load. A stale draw is not trusted: it relieves nothing and dispatch makes no increase of that device, but it does not hold the device off. A draw that falls silent after reporting exactly 0 is an idle device and counts as 0 (a device that reports only on change, like the go-e, says 0 once at rest); a draw that falls silent at any other value stays not trusted. |
| `stop_actuator` | Optional, `modulating` only: a `switch.` or `input_boolean.` that stops the device altogether (a charger's charging switch). Without one, a modulating device can only be reduced to its `min`. |
| `rotation_group` | Optional, `onoff` only: a name shared by identical elements on different phases (equal `rated_current_a`, `rated_power_w` and `value_per_kwh`, no `shed_priority`, at least two). Dispatch then starts the element on the phase with the most headroom first, re-ranking only after several agreeing cycles. |
| `circuit_group` | Optional; the circuit group (below) whose breaker the device is behind. |
| `value_per_kwh` | Required. Shedding takes the device with the lowest value first. |
| `shed_priority` | Optional; replaces `value_per_kwh` as the shed order key. |
| `reaction_time` | Optional; how long the load takes to change after a command. On equal value, the faster device sheds first. |
| `no_shed` | `true` for a device that must never be shed by the tier reactions (it is still held off in safe state and by the kill switch). |
| `min_on`, `min_off` | Optional lockouts; defaults from the built-in parameters. Lockouts never outrank protection: a device still inside `min_on` is shed last, never spared. Every lockout restarts when Switchboard restarts. |

A modulating device is first reduced to its minimum and then switched off through its `stop_actuator`. A current setpoint such as an EV charger's usually cannot go to 0 A: without a `stop_actuator` the guard takes it to its minimum and spends no further step on it.

How much a reduction relieves is counted only where it can be real: a device with a `draw` relieves at most what it is measured to draw (an idle charger at a 20 A setpoint relieves nothing), a modulating device without one relieves nothing (a setpoint is a ceiling, not a draw), and a relay without one relieves its rated current only while it reports on. An element whose thermostat has opened draws nothing although it reports on; without a `draw` Switchboard cannot see that, which is why the decision log records what the meter showed when someone else switched it.

### Circuit groups

Devices behind a downstream breaker of their own (a carport, a garage sub-board) form a circuit group. The guard applies the same tier table and overload budget to the group's breaker as to the main one, alongside it, and sheds only the group's members; dispatch keeps the group's headroom as well. Every decision a group causes names it.

```yaml
circuit_groups:
  - id: carport
    breaker_rating_a: 16
    characteristic: B            # B | C | unknown
    phases: [L1]
    reserve_w: { L1: 0 }         # optional; default control.circuit_groups.default_reserve_w (0)
    current: { L1: sensor.carport_current_l1 }   # optional sub-meter, A (current_unit: mA)
    current_max_age: 90s         # required with current
```

Each member names the group with `circuit_group: carport`, and its phases must be phases of the group. Without a sub-meter the group's current is the worst case of its members: a member's measured draw, a relay's rating while on (or unknown), a modulating device's setpoint. A stale sub-meter holds the members off. A downstream circuit only ever draws, so its sub-meter must never read negative: a negative reading (a current clamp mounted the wrong way round) is refused like an unusable sample and holds the members off too. A group breaker rated above the main breaker is accepted with a warning: the main limit binds first.

### What the guard decides

For every phase it compares the measured current with your breaker (`breaker_rating_a`, `characteristic`) using the tier table in `control-defaults.yaml`: up to 0.90 × In (In = the rated current) load may be added, up to 1.00 × In nothing is added, up to 1.13 × In (the current a breaker of characteristic B or C carries indefinitely) it reduces in the ordinary cycle, and above that it reduces faster and faster, down to switching off everything controlled on the phase at once above 1.45 × In. The durations are deliberately shorter than the breaker standard allows. An overload budget per phase remembers recent excursions: it is spent while the current is above the continuous limit and refills slowly below it, so three short overloads in a row count more than one. Only devices on the overloaded phase are candidates: energy cannot move between phases.

It also holds every controlled device off (records "would switch off", reason `safe_state` or `guard_stale_signal`) when it has no valid data: a stale or missing meter current (that phase only, or everywhere when the whole meter is stale), Home Assistant disconnected, a missing or invalid `switchboard.yaml`, and when the add-on stops. When the data is good again, devices are released only after several calm cycles in a row.

### Dispatch (computed and logged, nothing switched)

Dispatch decides what Switchboard *would* start or raise with the headroom the guard leaves, and what it would lower or stop when the headroom shrinks. It never goes past what the guard allows, and the guard's decisions come first in every evaluation.

- **Headroom.** Per phase (and circuit group), dispatch may fill the phase up to the reserve threshold (rated current minus the reserve), counted from the measured current plus every change it would already have made. A device it would start counts in full from that moment, so the shadow log never plans two devices into the same headroom.
- **Order.** Devices worth more per kWh get headroom first: exactly the reverse of the shed order. A device's setpoint is budgeted at its ceiling, not at what it happens to draw: a charger set to 20 A needs room for 20 A, so dispatch would lower its setpoint to what fits.
- **No chattering.** A start needs `dispatch.start_hysteresis_w` (300 W) of headroom left after it and six calm cycles in a row; a running device keeps its level down to `dispatch.stop_hysteresis_w` (−200 W) below the threshold, and is lowered only after two cycles below it, or when the guard asks for a reduction. One increase is in flight at a time; reductions are never held back.
- **Lockouts.** A relay keeps `relay_min_on` (6 min) and `relay_min_off` (90 s) between dispatch's changes. A change refused by a lockout is recorded once as **deferred**. A deferred reduction is carried out when the lockout expires; a deferred start or increase at the first cycle after that, if dispatch still wants it and the guard still allows it. Protection ignores lockouts.
- **Settling.** After every change dispatch waits until the meter has reported again (each balance signal newer than the change plus the device's `reaction_time`, at most `settling.hard_timeout`, 90 s) before the next increase. A measurement that never comes closes the window anyway (`settling_window_open`), without counting against the device.
- **Verification.** In shadow mode nothing was applied, so nothing is verified. When someone else switches a device, Switchboard records what the meter or the device's own draw showed (the device card history on the Limits page). In a later version with commanding enabled, a device that repeatedly does not follow is marked as not responding and left out of dispatch until a probe succeeds.
- **Foreign load.** Power that appears on a phase without a change of Switchboard's (a kettle, an oven) is recorded as a foreign load event.
- **After a hold.** When the guard lets go of what it held off (after a safe state, the kill switch or an overload), the devices are not simply back on: dispatch restores them as ordinary starts, most valuable first, one at a time, each after the usual calm cycles and with room left, and each restore is recorded.

Dispatch keeps, per device, what it **intends** next to what Home Assistant **reports**. In shadow mode the two differ whenever Switchboard would have switched something: an intention is recorded once, when it changes, never repeated every cycle.

### Manual override

Each device can be forced on, forced off or left to dispatch (automatic): on the **Limits** page, or with `select.switchboard_device_<id>_override` in Home Assistant. An override outranks dispatch (its order, headroom and hysteresis), never protection: a forced device is still held off by stale data, a safe state or the kill switch, still shed by the guard when a phase needs it, and a forced increase still waits for the guard to allow increases and for the breaker to have room. Overrides are kept across restarts; in shadow mode they, too, only change what is recorded.

### The Limits page

Choose **Limits** at the top of the web UI. It refreshes every two seconds and shows:

- a banner while the kill switch is on or a safe state is active (which phases, why, since when); in shadow mode it says what *would* be switched off. The kill switch cannot be changed from this page yet;
- per phase: the current drawn against the reserve threshold, the breaker's rated current and the continuous limit; the tier it is in and that tier's reaction; the overload budget with the time until it is full again; the headroom to the reserve threshold (A and W) and to the continuous limit; whether load may be added;
- the devices in shed order: what the guard holds each one to and why, the state each reports (a stale one counts as on, relieves nothing and is marked unsafe, with the signals that make it so), its value per kWh and the lockout time left;
- dispatch: per phase the headroom it works with, the change in flight and the foreign load; per device, in start order, what Switchboard would have it at next to what Home Assistant reports, why, what it waits for (a deferral with the lockout time left, an open settling window, a device not responding), the manual override with a selector, and the last check of a change;
- the last 50 decisions, with each reason as a sentence;
- the tier table for your breaker, in amperes.

If the page cannot reach the app it says **Limits unavailable** and hides every value.

### Kill switch

`switch.switchboard_kill_switch` holds every controlled device off while it is on, within one evaluation, whatever else is going on, manual overrides included; it survives a restart. In this version it, too, only records what it would hold off. It is a control switch for Switchboard, not an emergency stop and not a protective device.

### Decision log and runtime state

Every decision is one line in `/data/decisions/YYYY-MM-DD.jsonl` (one file per UTC day; kept `decision_log.retention_days`, 30 days by default): time, device, phase or circuit group, action, reason code, mode, who decided (`guard`, `dispatch` or `override`), the level the device is intended at, and the inputs that produced it (current, band, budget, device state; for dispatch the headroom, hysteresis, settling window and verification). Only changes are recorded, not every unchanged cycle. Every line says `"commanded": false`. Lines written by 0.3.x stay readable.

The **Decision log** page pages through every file the retention keeps, newest first, filtered by device and kind. While `/data` cannot be written, its first page also shows the newest decisions held in memory.

The kill switch, the overload budgets (per phase and circuit group), the manual overrides and the last known device states are kept in `/data/state.json` across restarts; dispatch's intentions, deferrals and settling windows are not, and every lockout restarts with the add-on. If that file is damaged it is set aside as `state.json.corrupt` and Switchboard starts with full budgets, no override and the kill switch off. If `/data` cannot be written (a full disk), the guard keeps running from memory and the API reports it.

Going back to 0.3.x: as long as no override is set and no circuit group is declared, `state.json` stays in the format 0.3.x reads. Otherwise 0.3.x sets it aside as damaged and starts with full budgets and the kill switch off.

### Guard entities

| Entity | Meaning |
|---|---|
| `sensor.switchboard_l1_headroom` | A left on L1 until the reserve threshold (rated current minus the reserve), from the measured current. Negative when over. |
| `sensor.switchboard_l1_headroom_power` | The same in W, at the configured voltage. |
| `sensor.switchboard_l1_breaker_headroom` | A left on L1 until the continuous limit (1.13 × In for B and C, 1.00 × In for unknown). |
| `sensor.switchboard_l1_overload_budget` | The overload budget, % (100 = no recent overload). |
| `sensor.switchboard_l1_recovery_remaining` | Seconds until the budget is full again. |
| `sensor.switchboard_l1_guard_state` | `normal`, `hold`, `reducing`, `shedding`, `safe_state` or `unknown`. |
| `sensor.switchboard_mode` | `shadow` (in this version always). |
| `sensor.switchboard_last_decision` | The reason code of the latest decision; device, phase and action as attributes. When a kill switch or a safe state ends, the action itself (`kill_switch_disengaged`, `safe_state_exited`), so the end of a condition cannot be mistaken for it. |
| `switch.switchboard_kill_switch` | See [Kill switch](#kill-switch). |
| `sensor.switchboard_device_<id>_state` | `allowed`, `reduced`, `shed` or `unsafe`, with the reason as an attribute. |
| `sensor.switchboard_device_<id>_dispatch` | What Switchboard would have the device at: `on`, `off`, `partial` (some stages of a stepped device) or `unknown`. Attributes: the reported state, setpoints, reason, deferral, settling window, not responding, override, and `commanded: false`. |
| `select.switchboard_device_<id>_override` | `auto`, `force_on` or `force_off`; see [Manual override](#manual-override). |

L2 and L3 entities exist for three-phase connections. A value Switchboard does not know is `unknown`, never a stale number. When you remove a device from `switchboard.yaml`, its entities are removed from Home Assistant; while the file is invalid, no entity is removed and no override is accepted.

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
- Switchboard publishes MQTT messages only under its own `switchboard/` topics and its own discovery topics under `homeassistant/`. It subscribes only to Home Assistant's `homeassistant/status`, its own kill switch command `switchboard/kill_switch/set` and, for each device declared in a valid `switchboard.yaml`, that device's override command `switchboard/device_<id>_override/set`. A retained message on a command topic is ignored.
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
| `SWITCHBOARD_DATA_PATH` | no | Where `state.json` and the decision log are written; default `/data`. |

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

The MQTT integration in Home Assistant must use the same broker, so that the health entity and the guard's entities appear. Mount `/data` as above: without a writable data folder the decision log and the kill switch are kept in memory only. The status page is built for ingress and is not reachable from outside the container in standalone mode in this version; use the health entity and the container log (`docker logs switchboard`) instead.

## Support

Report problems at <https://github.com/krzyl2/switchboard/issues>. Attach the Switchboard log at `debug` level; it never contains tokens or passwords.
