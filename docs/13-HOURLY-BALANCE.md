# Hourly balance (v2.0 core)

Status: specification from the maintainer's decision of 2026-10-26 (replacing the Node-RED heater flow). Nothing built. The utility bills on the energy balance netted per hour, so this is a Switchboard-wide quantity, not a heater feature: every discretionary load (heaters now, the EV module later) asks it whether energy is wanted. Protection is unchanged: the guard stays on instantaneous breaker current, and the hourly balance can only withhold or release increases, never override a phase limit (D2, D3). Off until configured; shadow mode applies (D7).

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
- A counter that decreases, or a clock step back, makes the balance unknown for the rest of the hour. Counters that are unavailable or older than `max_age` make it unknown only while they are: the counters are cumulative, so the balance is right again with the next report.

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

The closed list is extended in version 4, together with the EV proposals: `balance_projection_low` (a start refused or a stop because `proj < lo`), `balance_surplus` (a start permitted because `proj > hi`), `balance_unknown` (no allowance because the balance is unknown). Until then the nearest existing codes are used (`headroom_insufficient`, `headroom_available`, `guard_stale_signal`).

## Parameters (configuration, no literals in code)

`balance.floor_wh`, `balance.ceiling_wh`, `balance.burn_window`, `balance.smoothing_window`, `balance.min_remaining`, `balance.max_age`; per device `relay_min_on`, `relay_min_off`, `start`.

## Presentation

Dashboard: balance of the running hour, projection at :00 for the current load, the band, minutes to :00, with the unit Wh and the value in the sign convention above (positive = export). Entity IDs in monospace. Decision log inputs carry balance, free power, projection, band and `t_rem`.

## Decided by the maintainer (2026-10-26)

- Floor 150 Wh, ceiling 525 Wh and burn window 15 min stay as starting values.
- Smoothing window for free power: 5 min, a configuration parameter to tune in shadow.
- The balance gates the heaters now. The car joins it with the EV module: its PV-surplus tier (first in its merit order, `12-EV-MODULE.md`) is defined by this same hourly balance, while its grid-zone tiers are scheduled by price and calendar and are not gated by the balance. Until the module exists the car stays under the L1 limit and its merit order only.
- Equality with the utility's own hourly data is not pursued now; it stays an unverified assumption (a portal export would close it).

## Verification

Control logic on a virtual clock: hour boundary, baseline persistence across a restart, counter reset, 23 and 25 hour days, band and ramp, burn-down hold, merit order with two devices. Replay of the 72 h recording against the Node-RED decisions. Live: shadow beside the flow, N per cycle compared, then the cut-over through `force_cwu` with the maintainer present.
