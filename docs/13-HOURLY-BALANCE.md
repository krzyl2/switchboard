# Hourly balance (v2.0 core)

Status: specification from the maintainer's decision of 2026-10-26 (replacing the Node-RED heater flow). Built so far: the balance of the running hour (BAL-01, 0.8) and, not yet released, the projection, band, allowance rule and its gate in dispatch (BAL-02 to BAL-05; section "As built" at the end). Not built: the presentation (BAL-07) and the cut-over (BAL-08). The utility bills on the energy balance netted per hour, so this is a Switchboard-wide quantity, not a heater feature: every discretionary load (heaters now, the EV module later) asks it whether energy is wanted. Protection is unchanged: the guard stays on instantaneous breaker current, and the hourly balance can only withhold or release increases, never override a phase limit (D2, D3). Off until configured; shadow mode applies (D7).

## Why the hour

Energy exported and imported inside one hour is netted 1:1, and only the hour's remainder is billed or credited. So a kettle in a minute with a surplus costs nothing, while the same minute at the end of the hour does. Two things follow:

- ending an hour below zero is billed as import at the full price;
- ending it far above zero sells the surplus at the (lower) average market price, so a small positive remainder is the target and the surplus above it is worth burning into a load.

The decision "run a heater now" is therefore a decision about the projected balance at the next :00, not about the instantaneous export.

## Signals

All primary, mapped in the signal layer (D5); no HA helper, template or utility meter is ever an input (07-PITFALLS).

| Role | Entity (maintainer's install) | Notes |
|---|---|---|
| `energy_export_total` | `sensor.solaredge_m1_exported_kwh` | meter total, kWh, `total_increasing`; the meter nets the phases, so this is the summed-phase counter, not the per-phase ones |
| `energy_import_total` | `sensor.solaredge_m1_imported_kwh` | same |
| grid power | the existing `phase_power` signals | free power estimate only (below) |

Verified 2026-10-26: over 35 consecutive hours the per-hour difference of these counters equals `sensor.bilans_godzinowy_tauron` exactly (0.000 kWh), and the total counters are smaller than the sum of the per-phase counters, i.e. netted across phases like the billing meter. Not yet verified: the equality with the utility's own hourly data (portal export or bill).

## Balance of the running hour

```
hour_start    = the last local :00 boundary of the injected clock (60-minute hours, also on 23 and 25 hour days)
baseline      = (export_total, import_total) as last reported before hour_start
balance_wh    = ((export_total - baseline.export) - (import_total - baseline.import)) * 1000   positive = net export
```

- The baseline is the last counter value reported **at or before** the boundary (the counters refresh about every 10 s), never the first one after it. It is persisted in `state.json` so a restart inside the hour keeps it.
- A restart or an outage with no valid baseline for the running hour makes the balance **unknown until the next :00**. The Recorder is never read to recover it.
- A counter that decreases, or a clock step back, makes the balance unknown for the rest of the hour. Counters that are unavailable make it unknown only while they are, and so does a meter that is not alive: the counters are cumulative, so the balance is right again with the next report.
- **A counter that does not change is not reported again** (found live, 0.8.0): the export counter stands still while the house imports and the import counter while it exports, so a counter's own age proves nothing. The pair is usable when neither counter is unavailable or refused and the meter is demonstrably alive: a counter or a phase power was reported within its `max_age`. The pair's report time is the latest such report (the values are valid as of then).

## Free power and projection

```
free_w(t)   = mean over balance.smoothing_window of ( grid_net_export_w + Σ own_load_estimate_w )
t_rem       = hours until the next :00, floored at balance.min_remaining
proj(L)     = balance_wh + (free_w - L) * t_rem          L = power of the loads Switchboard would run
```

`grid_net_export_w` is the sum of the signed phase powers (positive = export); `own_load_estimate_w` is what the controlled devices were drawing at each sample (rated power for a running relay, their own draw signal where one exists), added back so that the projection does not shrink by what Switchboard itself switched on. This replaces the Node-RED baseline (`consumption_5m - heaters_5m`), which was a derived helper.

## Target band

```
lo       = balance.floor_wh                                  hard floor, flat
hi(t)    = lo + (balance.ceiling_wh - lo) * ramp             ramp = clamp(t_rem / balance.burn_window, 0, 1)
```

Carried over from the Node-RED flow as starting values, not truths: `floor_wh` 150, `ceiling_wh` 525, `burn_window` 15 min. The band collapses to `lo` over the last `burn_window`, which is the "burn-down" of the surplus; it is not a separate branch.

## Allowance rule

For the load power `L` currently running and the next candidate of power `P`:

```
start / increase   if proj(L) > hi  and  proj(L + P) >= lo
stop / decrease    if proj(L) < lo
otherwise hold
```

Hysteresis is the band itself (`hi` above `lo`), plus the existing lockouts and the one-stage-per-cycle limit. In the burn window a decrease is withheld while `proj(L) >= 0` (do not undo a start made to burn the surplus), unless something else forces it: a guard reduction, a thermostat-open measured draw, a kill switch, an override.

The rule only decides whether energy is **wanted**. Whether it is **allowed** stays with the guard and the dispatch envelope (headroom per phase, tiers, settling, DISP-08); the candidate is the next device in merit order whose phase has headroom. With several devices the energy is allocated top-down by value per kWh (DISP-02): `L` is the sum over devices already running, so a higher-value device (the car) takes the surplus before a heater.

## Fail-safe

| Condition | Reaction |
|---|---|
| balance unknown (counter stale, unavailable, reset, no baseline) | allowance is zero: no balance-driven start or increase; a load Switchboard started for the balance is stopped through its normal lockouts. Guard and protection unaffected |
| free power unknown (grid power stale) | as above |
| clock step | the hour is re-derived from the injected clock; a backward step makes the balance unknown for the rest of the hour |
| HA disconnected | existing safe state (everything off) |

## Heater specifics (replacing the Node-RED flow)

- Loads: the three CWU elements, identical, `rotation_group` picks the phase with most headroom. Voltage ranking and temperature caps are out of scope (maintainer: thermostat on the tank; no voltage ranking).
- `relay_min_on` 6 min and `relay_min_off` 6 min (the flow blocked a relay for 6 min in both directions; the default off is 90 s).
- `start: headroom` (not `restore_only`) so that Switchboard may start a heater from off.
- Cut-over: `input_boolean.force_cwu` halts the Node-RED flow. Switchboard control is enabled only after it is on, and never while the flow can still command. A read-only shadow period beside the flow compares N per cycle first.
- The flow's phase labels are swapped against the measured mapping: `grzalka_2` = L1, `grzalka_1` = L2, `grzalka_3` = L3.

## Reason codes

The closed list was extended in version 4 (the command-path proposals followed in version 5, the economic codes in version 6, `14-ECONOMICS.md`): `balance_projection_low` (a start refused or a stop because `proj < lo`), `balance_surplus` (a start permitted because `proj > hi`), `balance_unknown` (no allowance because the balance is unknown). Versions 1–3 used the nearest existing codes (`headroom_insufficient`, `headroom_available`, `guard_stale_signal`).

## Parameters (configuration, no literals in code)

`balance.floor_wh`, `balance.ceiling_wh`, `balance.burn_window`, `balance.smoothing_window`, `balance.min_remaining`, `balance.max_age`; per device `relay_min_on`, `relay_min_off`, `start`.

## Presentation

Dashboard (built, BAL-07): balance of the running hour, projection at :00 for the current load, the band, minutes to :00, with the unit Wh and the value in the sign convention above (positive = export). Entity IDs in monospace. Decision log inputs carry balance, free power, projection, band and `t_rem`.

## Decided by the maintainer (2026-10-26)

- Floor 150 Wh, ceiling 525 Wh and burn window 15 min stay as starting values.
- Smoothing window for free power: 5 min, a configuration parameter to tune in shadow.
- The balance gates the heaters now. The car joins it with the EV module: its PV-surplus tier (first in its merit order, `12-EV-MODULE.md`) is defined by this same hourly balance, while its grid-zone tiers are scheduled by price and calendar and are not gated by the balance. Until the module exists the car stays under the L1 limit and its merit order only.
- Equality with the utility's own hourly data is not pursued now; it stays an unverified assumption (a portal export would close it).

## Verification

Control logic on a virtual clock: hour boundary, baseline persistence across a restart, counter reset, 23 and 25 hour days, band and ramp, burn-down hold, merit order with two devices. Replay of the 72 h recording against the Node-RED decisions. Live: shadow beside the flow, N per cycle compared, then the cut-over through `force_cwu` with the maintainer present.

## As built (BAL-02 to BAL-05, 2026-10-26)

Domain, pure and tested on explicit times: `FreePowerWindow` (smoothed free power), `HourlyBalanceRule` (projection, band, `Decide` returning start, stop, hold or unknown) with the parameters in `HourlyBalanceParameters`. Dispatch (`DispatchEngine.Balance.cs`) runs the rule as a gate between the allocation and the simultaneous-increase limit; the allocation is redone once if the gate narrowed anything.

- **Parameters** live under `control.hourly_balance` in the shipped defaults (never the signal layer's `signals.balance`, which is the residual check of SIG-09): `floor_wh` 150, `ceiling_wh` 525, `burn_window` 15m, `smoothing_window` 5m, `min_remaining` 1m, `max_age` 60s. The last two are new starting values with no origin in the predecessor. Overrides are checked: ceiling at least the floor, every duration above zero, `min_remaining` no longer than `burn_window`.
- **Which devices are gated.** The ones that declare `start: headroom`, while both energy counter roles are mapped; a `restore_only` device (the default) is never started by dispatch and the balance has nothing to say about it, and without the counters nothing is gated (the balance is off until configured). Overrides (force on or off), an unresponsive device and the device of a running self-test are outside the gate. Setting a heater to `start: headroom` is therefore the step that hands it to the balance; the live file has none yet.
- **Free power** is sampled every dispatch step as `Σ over phases (own load − net import)` and averaged over `smoothing_window`, weighted by how long each sample held. An unknown sample (a phase whose power is not valid, a device whose own power is not known, such as a setpoint device without a power draw), a gap longer than `max_age` or a clock step back starts the window afresh. `max_age` is also how old the monitor's balance reading may be at the step that uses it; a reading of an earlier hour is not used.
- **Load `L`** is what Switchboard would run: the measured draw of every own device, except that a gated device counts at its planned level (target, else reported), so a start decided but not yet drawing is already in the projection and is not made twice. In shadow mode, where nothing is applied, the decisions therefore read as the balance would drive the heaters.
- **Stops** first, lowest value first: while `proj(L) < lo` (and not held by the burn window) each running gated device is lowered one stage and `L` drops by it. The decrease still waits `persistence_down_cycles` boundaries and the relay's min-on lockout.
- **Starts** walk the gated devices in value order. A device is a candidate only if the allocation shows it could rise now (guard permission on its phases, headroom, fresh data); each granted stage adds its power to `L` before the next is judged, so what is left of the surplus goes to the most valuable device that fits. The candidate's power is its rated power (relays) or its ceiling at the nominal voltage (setpoints); a candidate with no known power is never started. Starts keep their existing persistence (`persistence_up_cycles`, counted only on boundaries where the balance said start), the one-stage-per-cycle limit and `max_simultaneous_increases`.
- **Unknown** (balance, free power or any own load unknown, a reading older than `max_age`, no reading, counters mapped but not reporting): zero allowance, every gated device keeps its level and its pending reason is `balance_unknown`. A device Switchboard started under `start: headroom` (the ledger's self-started mark, so only in active mode) is lowered one stage through its normal lockouts; a device the user switched on is left alone.
- **Reason codes** v4 as in 04 §6: a start made for the balance is `balance_surplus`, a stop below the floor `balance_projection_low`, a withheld start `balance_projection_low` or `balance_unknown`. Unlike the spec's wording, `balance_projection_low` also covers a start refused because the projection sits inside the band (not above `hi`).
- **Exposure (BAL-07).** `DispatchEvaluation.Balance` (balance, free power, load, time to the :00, band, projection) reaches three readers. `DispatchStatus.Balance` (set by `DispatchStatusFactory` from the same step) feeds `GET api/balance`, which keeps its measured fields (`wh`, `hourStart`, `secondsToNextHour`, `reason`) and adds `known`, `unknownReason` (the tracker's reason, else `balance_not_used`, `free_power_unknown`, `own_load_unknown` or `no_dispatch_step`), `freeW`, `loadW`, `projectionWh`, `band {loWh, hiWh, burning}`, `ruleSecondsToNextHour` (the time to :00 the rule used) and `ruleAt` (the dispatch step); an unknown is null with its reason, never 0. Every dispatch decision's inputs carry `balance` (`balance_wh`, `free_w`, `load_w`, `projection_wh`, `lo_wh`, `hi_wh`, `t_rem_s`, `burning` in the JSONL; camelCase in `api/decisions`); the key is absent on lines written before, and null in the API while no counters are mapped, so old logs still read. The dashboard's Hourly balance card (`web/src/HourlyBalance.tsx`) polls `api/balance` and shows the balance of the running hour, the projection at :00, the band, minutes:seconds to :00, free power and own load, in Wh and W with positive = export, and the reason when something is unknown. It shows no entity IDs.

With economics (slice 3 of `14-ECONOMICS.md`) this gate generalises to an allowance gate (`DispatchEngine.Balance.cs`): the rule above is its surplus branch under `hourly_netting`, unchanged, and it is the whole gate for every device that declares no `sources`. Devices that declare `sources` may also be allowed by the active tariff zone, are stopped by this rule only while they run on surplus, and report the economic reason codes (`04-ALGORITHMS.md`, section 6, version 6).

### A start late in the hour pays for its min_on tail (0.10.2)

Found live on 0.10.1: `heater_l2` started at 09:57:00 with 180 s to the :00 and `relay_min_on` 360 s. The projection stopped at the :00, but the lockout kept the heater on until 10:03, and in the new hour (balance 0, 5.3 kW of own load against 2.4 kW free) nothing was allowed to stop it, so the car came down instead.

The rule now charges what a start commits beyond the :00 to the hour it is made in:

```
tail     = candidate_power * max(0, min_on - t_until_next)          Wh, min_on of the device (the step turns a relay stage or a setpoint device on)
start    if proj(L) > hi  and  proj(L + P) - tail >= lo
```

- **Refuse, not shorten.** The lockout is a hard rule (relay protection), so a start cannot be made shorter; the only choice is to make it or not. A refused start reports the existing `balance_projection_low` (the projection is too low for what the start commits); no new code, list version unchanged.
- **Full charge, no credit.** The next hour's free power and loads are not known, so the tail is subtracted in full. This only narrows starts: it never starts, raises or keeps anything the old rule would not, and never asks for a stop. The stop branch and the burn-window stop hold are unchanged.
- **No new parameter.** It uses the device's own `min_on` and the existing floor. A tail that falls in the next hour is zero when `min_on <= t_until_next` (every start earlier than `min_on` before the :00) and for an increase that turns nothing on (a setpoint raised while on).
- **Scope.** Hourly netting only (`HourlyBalanceRule`, and `HourlyNettingModel` for devices with `sources`). Net billing has no hour and ignores it; the zone class is not balance-gated.
- **Not covered.** A heater started earlier than `min_on` before the :00 can still be on while the next hour's projection turns negative; that is the displacement problem below (see "Displacement" in `14-ECONOMICS.md`).

Tests: `HourlyBalanceProjectionTests.A_start_is_charged_for_the_energy_its_min_on_holds_past_the_next_hour`, `HourlyBalanceGateTests.A_start_in_the_burn_window_pays_for_what_min_on_holds_past_the_hour`.

Known edges, left open: the gate does not look at the heaters' thermal state (maintainer: the tank has its own thermostat); the first start of an hour waits `persistence_up_cycles` (3 min) after the projection turns favourable; the rule is applied per device stage, so a stepped device rises one stage per cycle.

