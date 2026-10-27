# Economic dispatch (v2.0)

Status: design of 2026-10-26; slices 1-6 UI built, live verification pending (2026-10-26, unreleased, shadow only): tariff module, `economics:` configuration and read-only `GET api/economics` (slice 1); settlement module (`hourly_netting`, `net_billing`), `PriceBook`, `Settle`, effective value, `sources`, `value_follows`, `value_factor` parsed and echoed (slice 2); the allowance gate, the live value order, reason codes list version 6 and the `economics` decision-input block in dispatch (slice 3, "As built" below); dynamic prices from a Home Assistant entity: signal kind `price`, roles `price_import` and `price_export`, `signal:` + `fallback` + `add` price specs, `PriceInputs` to dispatch, the `fallback` basis and its log event (slice 4, "As built (slice 4)" below); savings accounting: the time-weighted prices and the value per device in the statistics (`statistics.json` schema 2), the device-off counterfactual per settlement period and the savings in `GET api/economics` (slice 5, "As built (slice 5)" below); slice 6 UI: the tariff and savings cards, the savings in the decision-log summary and the device card's value, sources and allowed-now ("As built (slice 6 UI)" below); the live shadow week on the maintainer's contract is not done. Builds on the hourly balance (`13-HOURLY-BALANCE.md`, released 0.9.0), which is the first settlement model and is not redone here. Requirement IDs `ECO-01..16` (`02-REQUIREMENTS.md`), slice plan at the end. Protection is unchanged: the guard stays on instantaneous breaker current and never sees a price (D2, D3, D4). Economics only decides which load gets the headroom the guard and the balance leave, and whether energy is wanted at its price. Shadow mode applies (D7).

## Goals and non-goals

Goals:

- Value per kWh becomes live: the start order of dispatch follows what a kWh is worth to each device now, not a number fixed in the file.
- A tariff module (flat, zoned by the local clock such as Tauron G13, dynamic from an HA entity) says what a kWh bought costs.
- A settlement module (D6) says what a kWh not exported earns and how a period is settled; hourly netting (BAL) and net billing ship in 2.0.
- Per-device `sources` (surplus only, surplus plus named zones, any) replace the 0.9 situation where every `start: headroom` device is judged by the balance alone and may start from the grid at night.
- Savings in the UI, replacing "not available, needs a tariff (v2)".

Non-goals (2.0):

- No planner, no forecast, no deadline goal, no "wants energy" flag: v3.0 and the EV module. A device with a zone source runs whenever that zone is active and the price test passes; limiting it is the user's choice of `sources` until goals exist.
- No battery arbitrage; a `storage` device keeps its static value.
- No net metering (credit ratio, running credit balance) and no monthly or annual settlement: deferred, the interface leaves room (below).
- No fetching of any price by the add-on (D10). Dynamic prices arrive from a Home Assistant entity mapped as a signal.
- No economics step in the setup wizard; `economics:` is edited in `switchboard.yaml`. The wizard must preserve it (ECO-02).
- No currency symbol anywhere in code or defaults; no VAT or fixed-fee modelling (prices are all-in variable prices per kWh as the user enters them).
- No holiday signal from HA (Workday integration) in 2.0; a dated list in configuration.

## The model

### Price signal and tariff

A **price** is currency per kWh at an instant, with a basis for the log: `flat`, `zone`, `live` (from the entity), `fallback` (the entity is not usable, the configured static price stands in). One price spec shape serves the import price (tariff) and the export price (settlement):

| Spec | Meaning |
|---|---|
| a number | flat |
| `zones` + `default_zone` | zoned: a time-of-use schedule on the local clock |
| `signal` + `fallback` (+ optional `add`) | dynamic: the price is the signal's value plus `add(t)` (a static spec, usually the distribution zones); `fallback` is a **required** static spec |

Zoned schedule: a zone has an `id`, a `price` and `when:` rules. A rule has `days` (`workday` = Monday to Friday and not a holiday, `weekend`, `holiday`, or weekday names; default all), `months` (default all) and `hours` (`"HH:MM-HH:MM"`, end exclusive, `24:00` allowed, a range past midnight wraps). Zones are tried in listed order, the first match wins, `default_zone` takes the rest. Evaluation is on the local time of the injected clock (the `TimeProvider.LocalTimeZone` the hourly balance already uses): the instant is converted with the zone rules, so a 23-hour day has no 02:xx and a 25-hour day has 02:xx twice, and a rule `02:00-03:00` matches the repeated hour both times. Holidays are `economics.holidays`, a list of dates; a list with no date in the current local year is a warning (`holidays_list_stale`), never an error. The module also answers `NextChange(t)` (the next local boundary where the zone differs), which the UI and the zone-end margin use.

Tauron G13 is a **configuration template**, not code (CLAUDE.md: Tauron specifics belong in configuration). It ships as `deploy/examples/` YAML and as a wizard-independent snippet in `DOCS.md`; no domain or application code names it.

### The value of a kWh per device

`value_per_kwh` keeps its meaning (what a kWh is worth to the device: the fuel it displaces, the energy it replaces) and becomes the **static value and the fallback**. Two optional keys make it live:

```
value_follows: static | import_price | export_price     default static
value_factor:  number                                    default 1.0  (a heat pump's COP, a conversion)
v_eff(d, t) = value_per_kwh                                  if static, or the followed price is unavailable
            = value_factor * price(value_follows, t)         otherwise
```

An unavailable followed price here means no such price spec exists (configuration warning `value_follows_without_price`, the device stays static) or a dynamic price at its fallback (the fallback spec's value is used, basis `fallback`). Never the last known value (a guess).

What a kWh costs depends on which energy it is:

| Class | Cost of a kWh to this device at t |
|---|---|
| `pv` (surplus: energy that would otherwise be exported) | `ExportValue(t)`: the forgone export income (settlement model) |
| `zone:<id>` (bought in the active zone) | `import_price(t)` of that zone |

The economic gain of running d on a class is `v_eff - cost`. A class is **worth using** while `v_eff - cost >= economics.min_margin` (control-defaults, currency per kWh, default 0). A device whose value is below the export price therefore does not burn surplus that is worth more sold; one whose value is below the zone price does not buy.

### Sources and the surplus

`sources` per device (03 syntax kept): `pv_only`, `pv_and_zone:<id>[,<id>]`, `any` (surplus plus every zone). Omitted = **unspecified**, which behaves exactly as 0.9 (the balance gate when both counters are mapped, ungated otherwise) so a 0.9 file keeps its decisions. A device that declares `sources` is strict: the PV class needs the balance (or free power) known; a declared source that cannot be evaluated (no tariff for a zone source, no counters for `pv_only` under hourly netting) is a warning (`sources_unusable`) and the device is narrowed to the classes that can be evaluated, down to none (it never starts). Narrowing is the only direction a configuration problem may move a device.

**Surplus** is net export energy the loads may take, defined from quantities that exist already:

```
free_w     = FreePowerWindow (grid net export + own loads added back, smoothed)      13-HOURLY-BALANCE
surplus_w  = free_w - L                    L = what Switchboard would run (as in BAL-04)
```

Under hourly netting the settlement model decides whether surplus energy is **wanted** with the existing rule (`proj(L) > hi and proj(L+P) >= lo` to start, `proj(L) < lo` to stop, band and burn-down): the surplus is the projected balance above the band at the next :00. Under net billing there is no hour to project: surplus is `surplus_w` over `economics.net_billing_window`, a start needs `surplus_w >= P + dispatch.start_hysteresis_w`, a stop `surplus_w < dispatch.stop_hysteresis_w`. Surplus does not require PV: it is whatever would be exported; at night it is negative and the PV class is never open.

**Allowance of device d** (pure, evaluated per candidate in the dispatch walk):

```
pv_ok   = pv in sources(d)  and settlement says Start (or Hold when running)  and worth(v_eff, ExportValue)
zone_ok = zone(t) in sources(d)  and worth(v_eff, import_price(t))  and (running or NextChange(t) - t >= economics.zone_end_margin)
start / increase: pv_ok or zone_ok        stop: neither (Hold keeps a running device while either holds)
```

A device running on the zone class is not stopped by the balance (importing in an allowed zone is intended); a device running on surplus is stopped by the settlement rule as in 0.9. `zone_end_margin` stops a start in the last minutes of a zone, which the relay's min-on lockout would carry into the dearer zone. The projection already contains every running device in `L`, so a zone start lowers the surplus projection of the devices behind it, which is the physical truth (one meter, no separate "grid kilowatts").

## Settlement module (D6)

A model decides three things; the tariff supplies the import side.

```
ISettlementModel                                    (Domain, pure, no clock, no entity)
  Id                          hourly_netting | net_billing
  NettingPeriod               Hour | Step            the interval inside which import and export cancel
  Allowance Decide(SurplusView s, Watts load, Watts candidate)     Start | Hold | Stop | Unknown, with reason
  Price?  ExportValue(DateTimeOffset t, PriceBook p)               what a kWh not exported earns; null only when no export price is configured
  Money   Settle(PeriodFlows f, PriceBook p)                       money for one period, positive = receivable
PriceBook                      import and export price at t, each with its basis
```

| Model | 2.0 | Netting | Surplus | `Settle` of a period with net export `b` (kWh) |
|---|---|---|---|---|
| `hourly_netting` | ships | local hour | BAL projection (wraps `HourlyBalanceRule`, unchanged) | `b >= 0`: `b * export_price`; `b < 0`: `b * import_price` (a cost) |
| `net_billing` | ships | step (statistics bucket) | instantaneous `surplus_w` | exports and imports apart: `export_kwh * export_price - import_kwh * import_price` |
| feed-in tariff | ships as `net_billing` with a flat `export_price` | step | as net billing | as net billing |
| `net_metering` | deferred | credit window | credit balance | needs a persisted credit balance, a credit ratio and a monthly or annual period; no counterpart on the maintainer's install; `ExportValue` would return `credit_ratio * variable import price` |

The hourly price of a period is the time-weighted mean of the prices in force during it (G13 zones and hourly market prices both change on the :00). The dispatch rules never depend on `Settle`; it serves savings and the settlement estimate.

## How value feeds the merit order

Today (0.9) the start order is the exact reverse of the shed order, both keyed by `shed_priority ?? value_per_kwh` (static), ties by reaction time, rotation phase ranking, name. v2.0:

- **Dispatch start order** and balance-gate stops use `order_key(d, t) = shed_priority ?? v_eff(d, t)`; everything else in the key chain is unchanged. Start highest first, stop lowest first.
- A new order is adopted only after `economics.order_stability_cycles` consecutive boundaries propose exactly it, and only when the keys differ by at least `economics.order_min_gap_fraction` (a price tick must not reshuffle devices; same mechanism as the phase ranking, DISP-10). The first proposal and a configuration change are adopted at once.
- **The guard's shed order stays static** (`shed_priority ?? value_per_kwh`). The guard runs on fresh breaker data only, a price signal may be stale or absent, and "protection outranks everything" and D4 say it needs no prices. Consequence, accepted: under overload the guard may shed in a different order than dispatch would stop; the decision log shows both orders' keys.
- Nothing in this section can raise a load above what the guard's envelope allows, widen the balance gate, or bypass lockouts, settling windows, `max_simultaneous_increases` or persistence. Order decides who gets headroom first; the allowance (above) decides whether energy is wanted; the envelope decides whether it is allowed. Gate order in the dispatch walk: guard permission and envelope, allocation by `order_key`, allowance (sources, price test, settlement), persistence and step limits. The 0.9 balance gate becomes the PV branch of the generalised allowance gate in `DispatchEngine.Balance.cs`; it still only narrows.

## Configuration

Two places, as with the balance:

- **Data** (the user's tariff, prices, sources): top-level `economics:` of `switchboard.yaml`, and per device `value_follows`, `value_factor`, `sources`. `schema_version` stays 1: every key is optional and a 0.9 file loads unchanged. `RootKeys` gains `economics`; the device parser's `LaterKeys` stops accepting `sources` with a warning and parses it.
- **Algorithm parameters**: new section `economics:` in `control-defaults.yaml` (and `control.economics` overrides), no literal in code:

```yaml
economics:
  min_margin: 0              # currency per kWh by which value must exceed cost; starting value, no origin
  order_stability_cycles: 3  # boundaries a new value order must be proposed before it is adopted (as phase_ranking_stability_cycles)
  order_min_gap_fraction: 0.05   # relative key difference below which two devices keep their order; no origin
  zone_end_margin: 6m        # no zone-class start this close to the end of the zone (equals relay_min_on; no origin)
  net_billing_window: 60s    # smoothing of surplus_w under net billing; no origin
```

Checks (config issues, invalid document only where it is unsafe to guess): zone ids unique; `default_zone` exists; every rule's hours parse; at least `default_zone` or full coverage (otherwise the uncovered time is warned `zone_gap` and falls to `default_zone` when set, else the document is invalid); prices finite and at least 0; `fallback` present on every dynamic spec; `value_follows` names an existing price; `economics.currency` is a free label up to 8 characters; `settlement.model` known; a rotation group's members equal in `value_per_kwh`, `value_follows`, `value_factor` and `sources`.

### Worked example (maintainer's installation: G13, hourly netting, 3 heaters, a car charger, no battery)

Prices are **placeholders**; fill them from the invoice. Hours as the maintainer remembers them, to be checked against the contract (open question 2).

```yaml
economics:
  currency: PLN
  holidays: [2026-11-01, 2026-11-11, 2026-12-24, 2026-12-25, 2026-12-26]
  tariff:                          # what a kWh bought costs (all-in variable price)
    default_zone: offpeak
    zones:
      - id: morning_peak
        price: 1.10
        when:
          - { days: [workday], hours: ["07:00-13:00"] }
      - id: afternoon_peak
        price: 1.60
        when:
          - { days: [workday], months: [apr, may, jun, jul, aug, sep], hours: ["19:00-22:00"] }
          - { days: [workday], months: [oct, nov, dec, jan, feb, mar], hours: ["16:00-21:00"] }
      - id: offpeak
        price: 0.60                # also the default zone: nights, weekends and holidays
        when: []
  settlement:
    model: hourly_netting          # the BAL gate; control.hourly_balance keeps its parameters
    export_price: 0.35             # last month's average market price, edited monthly (open question 5)

devices:
  - id: heater_l1                  # heater_l2, heater_l3 identical
    value_per_kwh: 0.35
    sources: pv_only               # surplus only; never from the grid, at night or in a cheap zone
    start: headroom
  - id: ev_charger
    value_per_kwh: 2.03            # petrol displaced
    sources: pv_and_zone:offpeak   # surplus, and the off-peak zone; never in a peak zone
    start: headroom
```

Reading it: at 23:00 on a workday only the car may start (zone `offpeak`, 2.03 against 0.60); the heaters get `source_not_allowed`. At 11:00 with the balance projected above the band the car and then the heaters start on surplus in value order (car 2.03, heaters 0.35 against 0.35 export: `value >= cost + min_margin` holds with margin 0). At 11:00 without surplus nothing starts: the morning peak is not in the car's sources. At 19:30 in summer with surplus, the car and the heaters still run on surplus; without it nothing. Setting `export_price` above 0.35 makes the heaters `value_below_cost` (selling is worth more).

## Signals and roles

Two new roles, both optional, both through the signal layer (D5): `price_import` and `price_export` (SPA `SIGNAL_ROLES` and `SignalRole` gain them, and a signal kind `price`). A price signal declares `unit: <label>/kWh` or `<label>/MWh` where `<label>` equals `economics.currency` (else `unit_mismatch`); the MWh to kWh conversion happens only in the signal layer (kWh, W, A and so on everywhere above it). Freshness is from `last_reported` and `max_age` as every signal (SIG-05, SIG-06). A price signal feeds `economics.tariff` or `economics.settlement.export_price` through `signal:`; a signal with a role that no spec names is a warning. Prices never enter `ProtectionInputs`; dispatch receives them in a separate `PriceInputs`, and an architecture test keeps the guard assembly free of them. Only the **current** value is read in 2.0 (a forward price curve is a v3.0 planner input).

## Reason codes (list version 6)

Version 5 (command path) is already in the tree, so these are **version 6**; the list only appends, the decision log keeps reading versions 6, 5, 4, 3 and 2, and `ReasonCodes.InVersion` treats a line of an older version that names one of them as not a decision (as for earlier versions). SPA list, en and pl sentences and contract tests follow in the same slice. Updates `04-ALGORITHMS.md` section 6 when built.

| Code | Meaning | Versions 1 to 5 used |
|---|---|---|
| `zone_allowed` | a start or increase permitted because the active tariff zone is one of the device's sources and its value covers the price | `headroom_available` |
| `source_not_allowed` | a start or increase refused, or a stop, because no energy class that is open now (surplus, the active zone) is among the device's `sources` | `headroom_insufficient` |
| `value_below_cost` | a start or increase refused, or a stop, because the available energy costs more (zone price, or forgone export) than the device's value per kWh plus `min_margin` | `headroom_insufficient` |
| `surplus_available` | a start or increase permitted because the instantaneous surplus (net billing) covers the device | `headroom_available` |
| `surplus_low` | a start refused or a stop because the instantaneous surplus (net billing) does not cover the device | `headroom_insufficient` |

Hourly-netting starts and stops keep `balance_surplus`, `balance_projection_low`, `balance_unknown`. Several classes may refuse at once; the reason reported is that of the first class **allowed by the sources** in the order surplus, then zones by ascending price; `source_not_allowed` only when no class allowed by the sources is open. `value_rank_below_cutoff` keeps its meaning (a higher-value device took the headroom).

Decision inputs gain an optional `economics` block (key absent on old lines, null in the API without an `economics:` section, a present but malformed block makes the line no decision, like `balance`): `zone`, `import_price`, `export_price`, `price_basis` (`flat`, `zone`, `live`, `fallback`), `v_eff`, `value_basis` (`static`, `followed`, `fallback`), `order_key`, `class` (`pv`, `zone`, `none`), `sources`, `settlement`.

## Savings

**Definition (open question 4): the device-off counterfactual per settlement period.** For each period (hour for hourly netting, statistics bucket for net billing), with `E_d` the controlled energy of device d, `E = sum E_d`, `b` the period's actual net export:

```
value_gained = sum_d E_d * v_eff_d               (time-weighted over the period)
extra_cost   = Settle(b + E) - Settle(b)         what the period's settlement lost by the devices running (>= 0)
savings      = value_gained - extra_cost
```

`b + E` is the balance had the controlled loads been off. This accounts for netting exactly: a kWh run inside an hour that ends net export costs the export price, one in an hour that ends net import costs the import price, whatever "source" it seemed to be at that instant. `extra_cost` is split across devices in proportion to `E_d` for the per-device view. Savings are the sum over periods; a negative device total is shown, not hidden (it means the sources or value are mis-set). The statistics store the figures already used (15-minute UTC-aligned buckets, per device measured energy) plus, per bucket, the time-weighted import and export price and basis, and per device the value amount; `statistics.json` gets a schema version bump and an old file reads as "no price data" for its buckets. Savings count only periods whose prices were known and are shown "since <first priced hour>"; nothing is back-filled. A period with any price at `fallback` basis is counted and flagged. The 0.9 stand-in "value of energy delivered at value_per_kwh" stays as the `value_gained` figure, relabelled.

## UI

- **Savings** (decision-log daily summary metric and, new, a dashboard card): replaces "not available - needs a tariff (v2)" when `economics` yields prices; the number with the currency label (verbatim from configuration, never a hard-coded symbol), the basis line ("against the controlled loads being off"), per-device and per-source split on the card. No `economics` section: the current "not available" text remains, with the hint to add one. en and pl strings.
- **Tariff status** (dashboard card): current zone (id in monospace), import price, export price, basis pills (`zone`, `live`, `fallback`), time and zone of the next change.
- **Device card (UI-12)**: value per kWh (static and effective now, with `value_follows`), sources in words (en, pl), and "allowed now": which class is open, the reason code sentence. The goal and command history rows stay "not available in this version" (goal is v3.0), so UI-12 itself stays unticked; ECO-13 covers the value and source part.
- **API**: `GET api/economics` (currency, tariff now and next change, settlement model, per-device `vEff`, `sources`, class now and reason, savings of the day and range); `api/statistics` gains the price and cost fields; `api/decisions` the `economics` input block. No new port.
- Not in 2.0: a wizard step, a day-plan screen, the mockup's "scenario without control" wording (the mockup text predates this definition; the copy follows the definition above).

## Failure behaviour

Principle: an economic input that fails **narrows or falls back to the configured static value, never widens, never guesses**; protection is untouched. Each row gets a named test (ECO-14).

| Condition | Detection | Reaction | Reason code / visible as |
|---|---|---|---|
| Dynamic price entity stale, unavailable, unknown, wrong unit | signal state not fresh | the spec's static `fallback` is used for the price; log inputs carry `price_basis: fallback`; dashboard pill | none (basis in inputs); a `tariff_fallback` event on enter and exit (an application log line, no decision: As built, slice 4) |
| `add` or `fallback` missing on a dynamic spec | parse | document invalid (`price_fallback_missing`): safe state as any invalid configuration | `safe_state` |
| Followed price for `value_follows` unavailable | no such spec | `v_eff` is the static `value_per_kwh`, warning at load | `value_basis: static` |
| No tariff, a device declares a zone source | parse | zone sources dropped (`sources_unusable`), device narrowed to `pv` | `source_not_allowed` when nothing else is open |
| Balance unknown (counters stale) with surplus the only class | BAL-05 | zero allowance, 0.9 behaviour; a zone class stays available if its source is allowed and the zone is known | `balance_unknown` |
| Free power unknown under net billing | free power window | no surplus-class start, a self-started device is lowered through lockouts | `balance_unknown` |
| Export price unknown (signal stale) | signal state | its `fallback`; no export price configured at all means the PV cost test is off (stated in the API and the card), not guessed | basis in inputs |
| Clock step, DST | injected clock | zone re-derived from the clock each step; a backward step keeps the adopted order, lockouts restart as in the existing table | none |
| Value order flapping | stability cycles and gap | the old order stays | none |
| Statistics file with an older schema | version | read, no price data for those buckets, savings "since" starts later | none |
| `economics:` removed or invalid on reload | parser | invalid: safe state; removed: 0.9 behaviour exactly | `safe_state` / unchanged |
| Home Assistant disconnected, MQTT outage, restart | existing rows of 04 section 8 | unchanged; economics adds no persistent state beyond the statistics file | as 04 |

Safe state, the kill switch and every row of 04 section 8 outrank all of the above.

## Test strategy

Control logic on the virtual clock (`FakeTimeProvider`) with an injected zone; the Warsaw zone fixtures cover the 2026-03-29 (23-hour) and 2026-10-25 (25-hour) days. The tariff and settlement modules are pure and tested on explicit instants. Each test states why it matters (Rule 9).

- **Tariff**: G13 hour table at every boundary minute (06:59/07:00, 12:59/13:00, 18:59/19:00, 21:59/22:00 in summer, 15:59/16:00, 20:59/21:00 in winter), both sides of the April and October month change, weekend and holiday, midnight wrap, DST days (zone at the repeated 02:xx hour and over the lost hour), `NextChange`, `default_zone`, gaps, holiday list stale warning.
- **Settlement**: `Settle` on the four cases (net export, net import, zero, mixed) for both models; hourly netting wraps the existing `HourlyBalanceRule` with the BAL tests as an unchanged oracle (the BAL tests must pass untouched); net billing hysteresis; the counterfactual `extra_cost` equals a hand computation.
- **Value and order**: `v_eff` static, followed, fallback; the order adopts only after N agreeing boundaries and at least the gap; `shed_priority` pins; the guard shed order is byte-identical with and without prices (an architecture test also refuses a price type in the guard assembly).
- **Allowance**: a table of (sources, zone, surplus, value, cost, running) to class and reason code, including the narrowing cases and `zone_end_margin`; the heaters never start from the grid at 23:00 (`source_not_allowed`), the car does; the stop of a surplus-class device below the floor does not stop a zone-class device.
- **Never widens**: a randomized property test over price, zone and order inputs asserts the allowance never exceeds what the envelope and the 0.9 gate would permit and that no increase happens without guard permission.
- **Backward compatibility**: with no `economics:` section the dispatch replays (22 LiveReplay, synthetic) and the 0.9 balance tests give byte-identical decisions (ECO-15).
- **Dynamic price**: fresh, stale at `max_age`, unavailable, unit mismatch (`PLN/MWh` converts, another label refused), fallback in and out, no entity ID outside the signal layer, no symbol literal in `src/` (source scan, like the entity-ID scan).
- **Savings**: hour with net export, net import, mixed; DST hour; bucket prices at fallback flagged; old statistics file; bounded sums stay finite (existing statistics bounds).
- **Reason codes**: version 6 closed-list tests, older lines read, a version 5 line naming a version 6 code is not a decision, SPA list and en/pl sentences complete.
- **Live, shadow, control off**: after each slice that changes decisions, compare the log against the maintainer's expectation for a day; the zone shown against the Tauron contract before any price is trusted; no change to the live file without consent.

## As built (slice 3)

Dispatch only, shadow like everything; no guard, signal or UI change. `DispatchSetup.Economics` (null without an `economics:` section, and then dispatch is the 0.9 one, byte for byte) carries the two price specs, the holidays, the settlement model (`HourlyNettingModel` over `control.hourly_balance`, or `NetBillingModel` over `dispatch.start_hysteresis_w`, `dispatch.stop_hysteresis_w` and `economics.net_billing_window`), the parameters and per device the value spec and the narrowed sources. The tariffs are evaluated on the injected clock's local zone: `DispatchEngine` takes the `TimeProvider` only for its `LocalTimeZone` (the source the hourly balance uses), and is still handed `now` by every call.

- **Allowance gate** (`DispatchEngine.Balance.cs`, `ApplyAllowanceGate`, replaces `ApplyBalanceGate`). A gated device (Auto, `start: headroom`, not unresponsive, not the self-test's) that declares no `sources` is the old balance rule untouched, and exists only while the counters are mapped. A device that declares `sources` is judged per candidate stage, after the first allocation and before DISP-08: `pv_ok` = the model's `Decide(surplus, L, P)` says Start and `ValueTest.Worth(v_eff, export price)` (off with no export price), `zone_ok` = the active zone is a source, `Worth(v_eff, import price)`, and for a start from off `NextChange - now >= zone_end_margin` (a device that already runs, or rises another stage, ignores the margin). A start needs `pv_ok or zone_ok`; the class is surplus when both hold; the load of the granted stage is added before the next device whichever class allowed it. Stops come first, in reverse start order: a running device is kept while `zone_ok` (ignoring the margin) or the model says Hold or Start with the value covering the export price; otherwise it is lowered one stage through the lockouts, with the reason of the class that no longer holds. Unknown inputs: the surplus class gives no allowance (reason `balance_unknown`), a device Switchboard itself started is lowered; a zone class stays available. The gate only ever calls `Cap` or `LowerOneStage`, so it narrows `MaxAllowed` and nothing else.
- **No `economics:` section, `sources` declared.** The keys are inert (a 0.9 file with sources written but no section is the 0.9 behaviour), because the section is what gives the sources a tariff and a settlement model; with the section present a declared source is strict.
- **Surplus under net billing** is `free - load` over `economics.net_billing_window`, from its own `FreePowerWindow` (the hourly balance's smoothing window is not used) and it needs no energy counters; a start needs the candidate plus `dispatch.start_hysteresis_w`, a stop is below `dispatch.stop_hysteresis_w`. Under hourly netting without counters a device with `sources` has no surplus class (`balance_unknown`) and never starts on it.
- **Value order.** `order_key = shed_priority ?? v_eff` at the boundary; the keys become tiers (`ValueOrder`): keys within `order_min_gap_fraction` of the best key of a tier share it and then tie on the ordinary chain (reaction time, rotation, name); the tier assignment is adopted by `StableAdoption` (the phase ranking's rule, which now stands on the same class): the first at once, a new one after `order_stability_cycles` boundaries that propose exactly it, a configuration change at once, a clock step back keeps it. It is active only while some device's value follows a price (`EconomicsSetup.OrderFollowsPrices`); otherwise the start order is the old static one with exact keys, so static files keep their order to the byte. It orders the start walk and the gate's stops only.
- **Reason codes (version 6).** The reported reason of a refusal is that of the first class among the sources that is open now (surplus, then the active zone): surplus unknown `balance_unknown`; surplus wanted but the value below the export price `value_below_cost`; surplus present but the candidate does not fit, or the projection inside the band (Hold) `balance_projection_low` (`surplus_low` under net billing); the active zone a source with its price not covered `value_below_cost`; otherwise `source_not_allowed` (also a zone start inside `zone_end_margin`, and a device whose sources are open to no one now, such as `pv_only` at night). Stops use `balance_projection_low` or `surplus_low` (the settlement rule), `value_below_cost` and `balance_unknown`; the spec's `source_not_allowed` for a stop is not produced (a running device is kept or stopped by the class that holds, never by the absence of a source), and a permitted start is `balance_surplus` (hourly netting), `surplus_available` (net billing) or `zone_allowed`.
- **Decision inputs and exposure.** `economics` in the dispatch inputs (zone, import and export price, basis, `v_eff`, value basis, order key, class `pv|zone|none`, sources, settlement); absent without a section. `DispatchEvaluation.Economics` and `DispatchStatus.Economics` carry the step's view read-only (zone, prices, per device `v_eff`, key, sources, class of the last boundary); `api/dispatch` and `api/economics` do not show it yet (slice 6). The price basis is `flat` or `zone`; `live` and `fallback` are read by the log already and come with slice 4.

## As built (slice 4)

Dynamic prices, shadow only; the guard, the protection inputs and the dispatch decision lists are untouched (reason codes stay at list version 6). A file without a dynamic spec decides byte for byte as before (the ECO-15 digests pass unchanged).

- **Signal layer** (`SignalKind.Price`, `SignalRole.PriceImport`, `PriceExport`, `PriceValue`). A `kind: price` signal declares `unit: <label>/kWh` or `<label>/MWh` (`SignalUnits.TryParsePrice`); no `sign`, `direction_from`, `phase` or `sigma`; `role` is optional. The label must equal `economics.currency`: the parser stamps each price signal with the section's currency (`SignalDefinition.PriceCurrency`, null without a section), a mismatch is the warning `unit_mismatch` at load, and at run time the normalizer refuses every sample of such a signal (`SignalDetail.UnitMismatch`, state rejected on the diagnostics page), as it does a reported unit that differs from the declared one. The `/MWh` to `/kWh` conversion is `SignalUnits.PriceToKwh`, a division (450 per MWh is exactly 0.45), called only from the normalizer; an architecture test holds that and that the domain economics module contains no unit conversion. Unavailable, unknown, not numeric, stale (`now - last_reported >= max_age`) and refused samples carry no value. A negative market value passes the signal layer.
- **Price specs.** `DynamicPrice(signal, fallback, add)`: `signal:` names a price signal declared under `signals:`; `fallback` is required and static (a number or zones), without it the document is invalid (`price_fallback_missing`, safe state); `add` is optional and static; a dynamic spec nested in either is `dynamic_price_not_supported` (the code now means only that); `kind:` is an unknown key. Both `economics.tariff` and `economics.settlement.export_price` may be dynamic. A price role that no spec names is the warning `price_signal_unused`. The zone ids a device may name in `sources` are those of the add and of the fallback.
- **Evaluation** (`Tariff.PriceAt(t, PriceInputs)`). While the signal is usable: `signal + add(t)`, basis `live`, zone the add's; otherwise the fallback spec's price with basis `fallback` and its own zone, never the last known value. A sum that is not finite or is negative is not a price the log can carry and is not clamped (that would be a guess): the fallback stands in. `NextChange` and the zone follow the schedule in force. Without inputs (no price signal mapped, a replay) a dynamic spec is at its fallback.
- **`PriceInputs`** (`Domain/Economics`): per price signal its state, detail, value (only while usable) and report time, built by the signal store each cycle. It travels as `SignalSnapshot.Prices`, `ControlInputs.Prices` and `DispatchInput.Prices`, beside the dispatch inputs and never inside `ProtectionInputs` or `DispatchInputs`; architecture tests refuse a price type reachable from them or named in the guard sources.
- **Dispatch.** The economics view uses the prices: the import price, the export price (the PV cost test) and `value_follows` read the live price. In the decision-input block `price_basis` is `fallback` when either price is at its fallback, else `live` when either is live, else the import price's (else the export price's) `flat` or `zone`; `value_basis` is `fallback` for a device that follows a price at its fallback (`factor * fallback price`) and for one that follows no existing price (as in slice 3), `followed` for a live price.
- **The event.** Entering and leaving the fallback, import and export apart, is `DispatchEvaluation.TariffFallback`, once per change; it is not a decision (no reason code exists for it and the list stays at version 6) and not a new decision action. `DispatchStatusHolder` writes one application log line per change: `tariff_fallback entered` (warning) and `tariff_fallback left` (information), naming the logical signal, never an entity.
- **API and diagnostics.** `GET api/economics`: a dynamic tariff or export price is `kind: dynamic` with the live price, its `priceBasis` (`live`, `fallback`), `fallbackInForce` and `signal` (name, state, detail, value before the add, age in seconds from `last_reported`); static prices carry `signal: null`, `fallbackInForce: false`. `GET api/signals` lists price signals (kind `price`, value per kWh, unit `<currency>/kWh`, state, age, rejected count); the SPA `SIGNAL_KINDS`, `SIGNAL_ROLES`, `ISSUE_CODES` and the en and pl labels follow, the contract tests hold them.
- **Wizard.** A price signal is an unowned signal and `economics:` a preserved section: a save keeps both unchanged (tested with `deploy/examples/economics-dynamic.yaml`).
- **Not done, for later slices.** The dashboard cards (slice 6). `PeriodPrice.Mean` reads a tariff without prices, so for a dynamic spec it returns the fallback: slice 5 must account the live price per bucket from what dispatch saw, not from this function. No forward price curve (v3.0).

## As built (slice 5)

Accounting only, shadow like everything: no dispatch decision, no guard file and no reason code changes (list version 6; the ECO-15 digest tests pass unchanged). `GET api/economics` and `GET api/statistics` gain fields; the SPA has no card for them yet (slice 6).

- **What the statistics record** (`EnergyAccumulator`, per 15-minute UTC bucket as before). Prices are **the ones dispatch actually used each step**: the economics view of the step's `DispatchStatus` (import price, export price with their basis, and each device's `v_eff`), held sample-and-hold over [t_k, t_k+1) on the signal clock and split at bucket edges, like every other figure. `PeriodPrice.Mean` is not used (it reads a tariff without price inputs and returns the fallback for a dynamic spec): the prices in force per step are accumulated instead. Per bucket `BucketPrices`: for import and for export a `PriceTrack` (price times seconds, and the seconds by basis `flat`, `zone`, `live`, `fallback`; the mean is price-seconds over priced seconds, and any fallback second flags the bucket), plus `ControlledOffsettingImport` (the part of the controlled energy that ran in steps that imported; with the loads off that much less would have been imported and the rest exported, which net billing prices apart). Per device `Value` is now the energy at the step's effective value, time-weighted (the static value without an `economics:` section, so a 0.9 file's numbers are unchanged). A bucket with no economics view records no `Prices` (null).
- **statistics.json schema 2.** Buckets gain `prices` (null, or `import`, `export` tracks and `controlled_import_offset_kwh`). Schema 1 is still read: its buckets have no price data, nothing is back-filled, it is not a failure and the next write is schema 2. A schema above 2, a schema 2 bucket without `prices`, a schema 1 bucket with it, an unknown key, a negative or non-finite number, more priced time than observed time, a mean price above `MaxPricePerKwh` (1e6) or an offset above the bucket bound set the file aside as `.corrupt` as every other corruption does. The existing bounds are unchanged, so every sum over the retained buckets stays finite (tested at the bounds). Writes stay atomic and size-capped as before.
- **Savings** (`SavingsAccounting`, computed on read from the retained buckets and the configured settlement model; nothing is stored). The period is the model's: the **local hour** for `hourly_netting` (the hour start of the hourly balance, `HourlyBalanceTracker.HourStartOf`, so a 23-hour day has 23 periods and a 25-hour day 25, the repeated 02:00 hour counted twice) and the **statistics bucket** for `net_billing`. Per period `E_d` is a device's controlled energy (measured plus the rated-model estimate, as the statistics' device total), `b` the period's net export from the grid energies of the buckets; `value_gained = sum Value_d`, `extra_cost = Settle(b + E) - Settle(b)` at the period's time-weighted prices (`Counterfactual.ExtraCost`; net billing counterfactual flows move `E` first off the import it ran against and the rest to export), `savings = value_gained - extra_cost`; the extra cost is split over the devices by `E_d`, and a negative total is shown. A period is **counted** when complete (its end is not after now) and priced (every price kind that was recorded at all was known for all of the period's observed time, and `Settle` needs no price it does not have); an **hourly period must also have been observed throughout**, otherwise it is `incomplete` (its net export is not known); an unpriced period is `unpriced`; the period in progress is `running`, reported apart and in no total. A period with any price at fallback is counted and flagged. `since` is the start of the first counted period in the retained buckets (retention is at most 8 days, so the totals cover what is retained, not a lifetime).
- **`GET api/economics`** gains `savings` (null without an `economics:` section, and then the dashboard keeps its "not available" text): `basis` (`against the controlled loads being off`), `currency` (verbatim from the file), `model`, `since`, `today` (the local day of the clock, 23, 24 or 25 hours), `range` (from the `from` and `to` query parameters, read as `api/statistics` reads them, a bad one is 400; null when not asked for), `retained` (from `since` to now) and `running`. A total has the counted `periods`, the `fallbackPeriods`, the `unpricedPeriods` and `incompletePeriods` after `since`, `valueGained`, `extraCost`, `savings`, a `fallback` flag and per device `energyKwh`, `valueGained`, `extraCost`, `savings`. **`GET api/statistics`**: every totals object (bucket, range, last hour) gains `prices` (null when none was recorded; else `import` and `export` with the time-weighted `perKwh`, `pricedS`, `bases`, `fallback`, and `controlledImportOffsetKwh`), and a device's `value` is the effective-value figure.
- **Not done.** No split by source class (`pv` or zone): the counterfactual is per period and per device, and the class of a kWh is not recorded per bucket (the design itself says the source at the instant is irrelevant under netting); the dashboard cards and the card strings (slice 6); savings beyond the statistics retention. Changing `settlement.model` re-reads the retained buckets under the new model (the buckets hold the flows and prices, not the model).

## As built (slice 6 UI)

SPA only plus one read-only field in `GET api/economics`; no dispatch, guard or signal change. The live shadow week (ECO-16, zones checked against the contract) is not done and the version is not bumped.

- **Client** `web/src/api/economics.ts`: strict parser (`bool`, `oneOf` on closed lists), `economics.sample.json` written by the real serializer (`SWITCHBOARD_WRITE_SPA_SAMPLES=1`). `EconomicsSpaContractTests` holds the lists `PRICE_BASES`, `PRICE_KINDS`, `VALUE_BASES`, `VALUE_FOLLOWS`, `SETTLEMENT_MODELS`, `ENERGY_CLASSES` (backend `EnergyClass` plus `none`) and `SAVINGS_BASES` to the backend and the sample's keys to the endpoint's. Sources are checked against the configuration grammar.
- **Backend field** (the one contract change): each device in `api/economics` gains `class` (`pv`, `zone`, `none`; null before a dispatch step), read from `DispatchStatus.Economics` of the last step, because "allowed now" needs the class and `api/dispatch` did not expose it. The reason sentence is the device's own `reason` from `api/dispatch`; no new reason.
- **Dashboard** (`Economics.tsx`): *Tariff* card (zone id in monospace, import and export price with the currency label as configured, basis pills `flat`, `zone`, `live`, `fallback`, next change time and zone, the price signal's name, state and age, a fallback note, a stale holiday list note) and *Savings* card (today's figure, the line "against the controlled loads being off", "since" the first counted period, per device with a negative total shown and flagged, the fallback flag with the number of periods, periods not counted, the running period apart and "not yet counted above", and that it is an estimate, not a bill, not a settlement and not a guarantee). Both are absent without an `economics:` section, without savings in it, or while the answer cannot be read. With no counted period the figure is "-", not 0.
- **Decision log summary**: the savings row shows the same body when savings exist; otherwise the old text stays, followed by the hint to add an `economics:` section. The value row then uses the currency label and says it is the effective value.
- **Device card**: static value and value now (with what it follows and the basis in words), sources in words (surplus only / surplus and the named zones / any, a dropped zone shown with what was declared, "not declared: judged as before"), and "allowed now" (the class and the reason sentence), shown only for a device that declares sources. Without an `economics:` section the 0.9 words stay (value without a currency, sources not available). The goal and history rows are unchanged; UI-12 stays open.
- **Currency**: the label comes from the data only; `currency.test.ts` scans every shipped source for a symbol or an ISO code.
- **Checked**: vitest/jsdom for every card in en and pl; the built SPA served against the sample payloads was looked at in a browser at 375 px in the light and dark themes (no horizontal overflow). Not done: the live check against real data.

## Slice plan

Each slice: one implementation subagent, ends with green build (0 warnings), tests, and something releasable in shadow. Slices 1 and 2 have no effect on decisions. Version numbers are suggestions; 2.0.0 is slice 6 plus a week of shadow.

| # | Slice | Scope and areas | Requirements | Acceptance (verifiable) | Depends on |
|---|---|---|---|---|---|
| 1 | Tariff module and `economics:` config, read-only | Domain `Economics` (price spec, zone schedule, holidays, `NextChange`); parser for `economics:` (RootKeys, validation, issues), `control-defaults` section and binder; `GET api/economics` (tariff now, next change); wizard round-trip keeps `economics:` and device keys; DOCS.md snippet and G13 example file. No consumer in dispatch. 0.10.0 | ECO-01, ECO-02, ECO-16 (part) | tariff tests on the G13 table and both DST days; a 0.9 file loads unchanged; invalid specs give the listed issues; wizard save preserves the section; live: `api/economics` shows the right zone for the maintainer's contract with control off | none |
| 2 | Settlement module and value model, pure | Domain `Settlement` (`ISettlementModel`, `hourly_netting` wrapping the BAL rule, `net_billing`, `PriceBook`, `Settle`, `ExportValue`); `EffectiveValue` (`value_follows`, `value_factor`, fallback) with parser keys; `SourceSet` type and `sources` parsing (parsed and validated, not used by dispatch). 0.10.1 | ECO-03, ECO-04 | `Settle` tables for both models; BAL tests untouched and green; `v_eff` cases; `sources` grammar and narrowing warnings; architecture tests: guard has no price type, no symbol literal | 1 |
| 3 (built) | Dispatch: live order, sources, price test, reason codes v6, shadow | Generalise `ApplyBalanceGate` to the allowance gate (surplus branch unchanged), value order with stability and gap (`PhaseRanking` pattern), guard shed order static; reason codes v6 (Domain, `ReasonCodes` versions, `DecisionJson`, SPA list, `reasons.ts`, en/pl); `economics` decision-input block; `control.economics` binder; 04 section 6 and 7a updated. 0.11.0 | ECO-05, ECO-06, ECO-07, ECO-08, ECO-09, ECO-14 (rows), ECO-15, ECO-16 | allowance table; randomized never-widens test; no-`economics` replay byte-identical; older log lines read; heaters `pv_only` do not start at night while the car with `pv_and_zone:offpeak` does (shadow log); `api/decisions` shows the block | 1, 2 |
| 4 (built) | Dynamic prices from an HA entity | Signal kind `price`, roles `price_import` and `price_export` (Domain, SPA `SIGNAL_ROLES`, diagnostics page, setup role list), unit normalisation and `<label>/MWh`, freshness, `signal:` + `add` + `fallback` in price specs, `PriceInputs` to dispatch, fallback basis and `tariff_fallback` event. 0.11.1. Droppable alone if the maintainer defers dynamic prices (open question 3): `kind: dynamic` is then refused at parse | ECO-10, ECO-14 (rows) | stale, unavailable, wrong-unit and fallback scenarios on the virtual clock; no entity ID in domain; guard assembly price-free; diagnostics shows the signal and its age | 3 |
| 5 (built) | Savings accounting | Statistics: per-bucket time-weighted prices and basis, per-device value amount, schema version bump with old-file read; counterfactual accounting per settlement period; `api/economics` savings and `api/statistics` fields; bounds. 0.12.0 | ECO-11 | hand-computed hour fixtures (export, import, mixed, DST), old file read, fallback flag, "since" start, sums finite | 2, 3 (4 for dynamic basis) |
| 6 (UI built; live pending) | UI and live verification | SPA: savings metric and dashboard card, tariff status card, device card value and sources and allowed-now (UI-12 value part), en/pl strings, currency label, tests; live shadow run beside the maintainer's real G13 config for a week (with consent for the live file). 2.0.0 after the week | ECO-12, ECO-13, ECO-16 | web tests; both themes and phone width; no symbol literal; copy reviewed against the definition in Savings; live checklist: zone and price equal the contract, decisions as expected, savings plausible against one hand-checked day | 3, 5 |

## Open questions for the maintainer

Each has a recommendation, and the design above already uses it.

1. **Currency label.** Recommend `PLN`: ISO code, language-neutral, shown verbatim in both UI locales. Changing it changes one config key and nothing else.
2. **G13 zone hours and prices.** Recommend the template above (morning peak 07:00-13:00 workdays; afternoon peak 19:00-22:00 April to September and 16:00-21:00 October to March, workdays; off-peak otherwise, all weekends and holidays) with prices copied from the Tauron invoice, all-in variable gross per kWh, and no price shipped as a default. The hours are as remembered (`12-EV-MODULE.md` already says to verify them); if the contract differs only the YAML changes. Holidays are a dated list the maintainer maintains yearly (the add-on warns when it has no date of the current year).
3. **Dynamic prices in 2.0.** Recommend yes, slice 4: the fallback design and the signal roles cost little, the structure is needed anyway and it is the only way an RCE-style price enters without outbound access. If deferred: drop slice 4, reject `kind: dynamic`, nothing else changes.
4. **Savings baseline.** Recommend the device-off counterfactual per settlement period (definition above): exact under netting, computable from stored data, no simulation. The alternative "against a scenario without control" in the mockup copy needs a simulation of the house without Switchboard and is not shipped. A different baseline changes only slice 5's formula and the card's basis line.
5. **Export price source.** Recommend a static `export_price` set monthly to last month's average market price (the maintainer's survey decision), with the `price_export` signal available from slice 4 for automating it later. Note the consequence to confirm: the heaters' value 0.35 is compared against it; if the monthly price rises above 0.35 the heaters get `value_below_cost` (surplus is worth more sold). Raise `value_per_kwh` or set `value_follows: export_price` with `value_factor` above 1 if that is not wanted.

## Design choices the maintainer may want to veto

- Guard shed order stays static while dispatch order is live (above).
- An omitted `sources` keeps the 0.9 behaviour; only a declared one is strict.
- A zone-class device runs whenever its zone is active and the price test passes, until v3.0 goals exist.

## Displacement: stop the cheaper device when a worth-more one fits (0.10.2)

Found live on 0.10.1 (shadow report of the maintainer's run, 10:02:30 UTC): the car (value 2.03) was off, a heater (0.35) ran, and 2.4 kW of free power without the heater would have been enough for the car at 6 A (about 1.47 kW). The car could not start (the start would project to about -700 Wh, below the floor), and nothing stopped the heater (its own projection, 624 Wh, was above the floor). The value order only ever decides who gets headroom first; it never takes energy back from a device already running.

**Rule.** In the allowance gate, after the starts are judged, take the most valuable device `V` that the allocation says could rise now but was refused by the projection (`balance_projection_low`). Look for the running gated device `R`, lowest effective value first, such that all of these hold:

1. `v_eff(V) - v_eff(R) >= economics.min_margin` and `(v_eff(V) - v_eff(R)) / v_eff(V) >= economics.order_min_gap_fraction` (a worth-more device by a real gap, the same gap the value order uses before it swaps two devices; a price tick inside the gap displaces nothing).
2. `R` may be lowered now: its `min_on` has elapsed (`LowerOneStage` already refuses a locked device). A displacement never stops a device before its `min_on`, and never forces a stop the lockout machinery would defer.
3. `R` is not on the zone class (energy the tariff allows is not surplus; the zone class is never stopped by the balance).
4. With `R` one stage lower, the allowance for `V` is Start: `proj(L - shed(R)) > hi` and `proj(L - shed(R) + P_V) - tail_V >= lo` (including the min_on tail of 13-HOURLY-BALANCE), and `V`'s value covers the price of the surplus as in the allowance gate. Nothing is displaced for a start that would not then happen.

Then `R` is lowered one stage with reason `value_rank_below_cutoff` ("a higher-value device took the headroom", its existing meaning, so the list stays at version 6, no new code). One displacement per step; while a displacement is open (a device already lowered this step for it) no second one is chosen. `V` is not started in the same step: the load of `R` leaves the projection once its stop is the target, and `V` then starts by the ordinary rule (persistence, one stage per cycle, settling).

**Why it does not oscillate.** After the swap `R` can only come back through the ordinary start rule, which now counts `V` in the load, so `R` cannot displace `V` (it is worth less), and cannot start on surplus `V` uses. `V` stopping on the balance frees the energy for `R` again, through the ordinary start, after `R`'s `min_off`. Every swap is bounded below by `R`'s `min_on` and `min_off` and by the gap of condition 1.

**Protection.** Displacement only lowers a load or leaves things as they are; it adds nothing. `V` still has to pass the guard's envelope and the allocation before the gate sees it, so no phase limit can be exceeded because of it. It never runs without an `economics:` section (a file without one decides byte for byte as 0.9, ECO-15); there the value order is the static `value_per_kwh` and displacement would be a new behaviour for an old file.

**Not done on purpose (for the maintainer).** (a) A displacement does not stop `R` before its `min_on` (the stop would be deferred anyway and the device is protected by the lockout); the cost is that the car waits for the heater's `min_on` (6 min) when the heater has just started. (b) The step-down of the car in the new hour before the heater, when the heater was still locked by `min_on` (live 10:00 to 10:03), is correct and unchanged: a device under `min_on` cannot be stopped, and the guard-and-balance stop of the car is what keeps the phase and the hour inside their limits; it is the symptom of the start of 09:57 that the min_on tail in `13-HOURLY-BALANCE.md` now prevents. (c) The reason code of the displacement is `value_rank_below_cutoff` and not a new code, because the meaning is the same and a new code would move the list to version 8 (the planner's slice 5 already takes version 7).

Tests: `DisplacementTests`.
