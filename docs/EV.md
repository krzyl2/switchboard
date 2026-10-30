# EV module: execution plan

Status: plan approved for implementation 2026-10-30 (maintainer: "zapisz kompletny plan wykonawczy do pliku EV.md", to be run in a separate implementation session). Nothing of the EV module is built yet. This file is self-contained: read it, then `CLAUDE.md`, `docs/STATUS.md`, `docs/DECISIONS.md` (binding, newest last) and the spec `docs/12-EV-MODULE.md`. Where this file and the spec differ, this file wins (it carries the corrections of the code review of 2026-10-30; section 3 lists them) and the first task of S0 is to record that in `DECISIONS.md`.

Provenance: drafted by a planning agent from the code at release 0.11.8, then reviewed adversarially by a second agent (Opus) against the source with file:line evidence. Line numbers below were true at 0.11.8; re-check them, they move.

## 1. Goal and shape

Charge the maintainer's PHEV (18 kWh battery) from PV surplus first, then from the grid zones in order of price, the dearest only for what the next trip still needs, so the car is charged enough for the trip in the calendar. Spec: `12-EV-MODULE.md`.

Shape: the EV module is **one more `IGoalSource`** (`GoalOrigin.Module`, already in the enum, `GoalTypes.cs:28`). It produces at most three goals for the one EV device and its own assessment for the UI and API. It has no path to the effector, the command port, device overrides or the guard. Everything else (slot grid, EDF, hard/soft goals, escalation, latest-safe-start, `Defer`/`Done`/`Urgent`, allowance gate, reason codes v7, `hourly_netting` surplus gate) is reused unchanged. The plan reaches dispatch only behind `planner.dispatch: off | narrow | full` (default `off`), so a fresh install and a config without `ev:` decide byte for byte as v2.

Protection outranks everything (CLAUDE.md hard rules): the guard's phase limits and the dispatch envelope cap the charger whatever the module proposes. Not a protective device.

## 2. Maintainer answers (assumed defaults, 2026-10-30; override in the first message of the session)

| # | Question | Default taken |
|---|---|---|
| 1 | Entities: car SoC, electric range, calendar, plugged state | to be discovered read-only in S2 (live HA); `calendar` for the Google calendar `Kursy`; plugged from the go-e car-state sensor. The maintainer confirms the IDs found |
| 2 | Event description | `km: 42` in the description (case-insensitive, comma or dot decimals), first hit wins; an all-day event or one without `km:` is ignored and listed as unparsed |
| 3 | Fallback kWh/km | 0.20 (maintainer 2026-10-30: a full charge, 18 kWh, gives 90 km; was 0.30); learning plausibility bounds 0.12 to 0.60; review after a shadow week (usable capacity may be below 18 kWh, which would lower the real figure) |
| 4 | Weekly full charge | Sunday, cap 100 %, target by local midnight of that day, PV first then cheapest zone |
| 5 | Planner may start the charger | first week `planner.dispatch: off` (preview only), then `narrow`, `full` only after a shadow day and with the maintainer's consent |
| 6 | Without a trip | floor 25 % from the cheapest zone, above the floor only PV up to 90 %, once a week 100 %; "PV only" = no grid and no floor; "Off" = the existing `ForceOff` override, set by the user, never by the module |
| 7 | Capacity | 18 kWh gross as in the spec; learning of kWh/km corrects it indirectly |
| 8 | Manual control | MQTT select (mode) + number (target %) + button ("charge now") and the panel |
| 9 | Values without origin | ready margin 30 min, charging losses 10 %, reserve 15 points, floor 25 %, floor_after 25 %, cap 90 %: configuration (`control.ev`), reviewed after a shadow week |
| 10 | Does live `ev_charger` have `start: headroom`? | unknown, S1 checks the live file read-only and the maintainer decides (it changes v2 behaviour of the go-e on its own, see section 8) |

### 2a. Maintainer amendments (2026-10-30, binding; they override rows 4 and 6 above)

- **Weekly full charge on any weekend day** (days configurable, default Saturday and Sunday; local calendar, holidays from `economics.holidays` count as weekend), once per week, 100 %. Plus **manually**: a long trip in the calendar or a user request raises the target to 100 % (mode "full charge" in the manual control of S6).
- **A full charge ends as late as the window allows**, preferably just before departure (battery sits at 100 % as short as possible). For the weekly full charge the deadline is the end of the allowed window; for a trip it is the trip start minus `ready_margin`. The planner's latest-safe-start already works backwards; the module must not pull a full charge earlier than needed.
- **Spreading over a long window**: when the window is long (for example all night), the charge is spread evenly instead of running at the ceiling (for example 14 A instead of 20 A when the energy fits). Per-slot target = energy / usable slots, never below the device minimum and never above the ceiling; PV surplus may still raise it. This is a planner change (a target below `CeilingW` per slot) and is designed in S5; `control.ev.spread` (on/off, default on) and its parameters live in configuration. Open design points for S5: how the target reaches the go-e (amp actuator step) without oscillation, and interaction with `persistence_up_cycles`.

## 3. Corrections to the spec and to the first draft of this plan (binding for this plan)

Record each as one dated line in `DECISIONS.md` in S0 (Rule 7: contradictions are surfaced, one side chosen).

1. **Zone names live only in configuration.** The core does not know G13 or Tauron (decision 2026-10-26). "Cheapest zone" = the lowest price in the `PriceBook`, optional `ev.cheap_zone: <id>`. Goal `sources` are built from zone ids. The spec's names (`morning_peak`, `offpeak`) are configuration examples. Weekends and holidays are `economics.holidays` (configuration, no calendar in code).
2. **`Auto` has an effect only at `planner.dispatch: narrow` (floor, cap, defer) and `full` (deadline guarantee in a zone outside the device's `sources`: `Urgent`).** At `off` the module computes and shows a preview and raises the warning issue `ev_without_planner_dispatch`. Today (v2) the live car follows `sources: pv_and_zone:offpeak` and the hourly balance with no cap, floor or deadline.
3. **Cap 90 % needs `on_met: stop`**, otherwise the device falls back to the v2 opportunist after the goal is met and keeps charging off-peak. "Off" is not a goal: it is the existing `ForceOff` override (works in every planner mode); the module never sets it.
4. **Surplus logic is not rebuilt.** The spec's "Surplus" section is the existing `hourly_netting` gate (band `floor_wh`/`ceiling_wh`, min_on tail, lockouts), available only to devices with `start: headroom`. The spec sentence becomes "projection >= floor_wh after the start".
5. **No reason code list v8.** Raising `ReasonCode` to v8 changes the list number in every decision line (`DecisionJson.cs:62`), makes v7 lines with a `plan` block stop reading as decisions (`DecisionJson.cs:444`), breaks "byte for byte" (EV-11) and adds codes no decision emits, which breaks "every decision carries exactly one reason code" (`ReasonCode.cs:7`). Instead the EV assessment gets its **own closed list** (like `PlanNote`): `ev_trip_deadline`, `ev_surplus`, `ev_cheap_zone`, `ev_floor_hold`, `ev_weekly_full`, `ev_not_plugged`, `ev_forecast_missing`, `ev_manual`, `ev_soc_stale`, `ev_calendar_unavailable`, `ev_range_unusable`, `ev_pv_only`, `ev_off`, `ev_without_planner_dispatch`. It appears in `GET api/ev` (`reason`) and as an optional `cause` in `api/plan`. Dispatch decisions about the car stay on v7 codes (`plan_slot_target`, `plan_deferred`, `goal_met`, `goal_deadline_urgent`). The line in DECISIONS replaces "closed list gets EV codes" in `12-EV-MODULE.md` and the outdated line in `DECISIONS.md` (~426). Every assessment state, including "no trip", has a code.
6. **EV state does not go into `state.json`.** A schema bump makes the previous version treat the file as untrusted and engages the latched kill switch (`RuntimeStateFile.cs:467-469`, `KillSwitch.cs:81-91`). EV state (mode, charge-now, learned kWh/km with samples, last trip) lives in its own `ev.json` modelled on `load-profile.json` (atomic write, a corrupt file is set aside as `.corrupt`, no kill switch, bounded size).
7. **Calendar:** an HA `calendar.*` entity exposes only the current or next event in its attributes, and the add-on reads entities only (D10, no `calendar/event/subscribe`). Version 1 therefore plans **only the next trip**. Several trips per day, device unavailability windows in the planner and a trip list from an HA template sensor are the deferred slice S9. An all-day event or one without `km:` hides the following trip: show it in the UI as a warning.
8. **Calendar times are naive local.** `start_time` and `end_time` carry no offset. Today a timestamp signal with `attribute:` rejects a zone-less time (`SignalNormalizer.cs:130`); only `series` keys read as the add-on zone (decision 2026-10-29). The new calendar signal kind must read naive times in the add-on's local zone with the same DST rule (nonexistent local time shifts forward one hour, repeated local time = first occurrence, never throws).
9. **PHEV:** a trip longer than the electric range is not an error: target 100 %, a note "trip exceeds electric range". kWh/km is learned only from a SoC and range pair taken from the same report, with SoC >= `ev.learn.min_soc`, range > 0, outside charging, within the plausibility bounds; the electric range is mapped, not the total range with fuel.
10. **Charge now** works only at `planner.dispatch: full` (otherwise `409` with the hint to use the existing `ForceOn` override). It is a manual hard goal; `Urgent` appears only when the margin is at most `escalation_margin` (`Planner.cs:221-222`: `LatestGridStart` = cutoff - R/ceiling). Use the static `CeilingW` of the device, not 20 A (`Planner.cs:34-48`). It ends at the target, on unplug or at its deadline. `ManualGoalStore` needs changes for this (section 6, S6).

## 4. Review findings that change the design (must be built in)

| Id | Finding | Required design |
|---|---|---|
| B1 (critical) | Goal sources run inside the guard step with no exception isolation: `GuardRunner.Step` (`GuardRunner.cs:182`, no try) -> `PlanView.For` -> `DiagnosticsPlanCurrency.Goal` -> `GoalEvaluator` (`GoalTracker.cs:33`, no try). An exception ends the guard loop (`RunAsync` catches only cancellation, `:119-131`) and the add-on (`GuardService.cs:98-102`); `ShutdownAsync` calls `Step` again (`:226-231`) so the safe state may not be applied | The EV source computes its offer **outside the guard thread** and publishes an immutable snapshot; `Goals()` only returns the snapshot. Additionally `try/catch` per source in `GoalEvaluator` (a failing source yields no goals plus one log line, the other sources are unaffected), and a test with a throwing source through a guard step |
| B2 | A device's goals add up: each goal has its own `Need = RemainingKwh` (`Planner.cs:66, 313-337`, additivity pinned by `PlannerTests.cs:292`), while SoC goals are absolute against the same SoC. Floor 25 % + trip 90 % + cap 90 % at SoC 40 % plans about twice the energy (inflated slot budgets, wrong Defer/Done, wrong cost, wrong hours per source in the UI) | The module emits **incremental needs**: each further goal only for the part above the higher goal before it. Property test: sum of needs <= (max target - SoC) x capacity x (1 + loss). The cap is omitted when a trip or weekly goal with a higher target exists |
| B2b | Deadline/margin counted twice if `deadline = start - margin` and `Margin = ready_margin` (GoalId takes `Deadline`, `Planner.cs:52`) | `Deadline` = trip start, `Margin` = `ready_margin` |
| B3 | A revision that depends on target or SoC is unstable: the target drifts with the learned kWh/km, every revision change changes the goal signature, triggers an immediate replan (`PlannerService.cs:295-302, 320-329`) and makes `PlanView` return null until then (`DiagnosticsPlanCurrency.cs:35`), so dispatch jumps to v2 | Revision contains only identity and shape: trip identity, kind, hard, sources, on_met, escalate, capacity, loss, margin. **Not** target, not SoC. The target is frozen per trip and recomputed only when it changes by more than a configured threshold. Test: revision stable while SoC and EWMA move |
| B4 | The calendar entity shows the current event. A trip in progress or a late departure gives a deadline in the past: the goal becomes `Missed` and runs as `Urgent` in every zone from now (`GoalRequest.cs:53-54`, `Planner.cs:203-206, 323`), i.e. full-power charging in the peak | The trip **disappears the moment its `start` passes**. Test on the virtual clock |
| B5 | v8 list: see section 3 point 5 | own closed assessment list, `ReasonCode` stays v7 |
| B6 | schema 6 collision and rollback: see section 3 point 6 | own `ev.json` |
| B7 | `GoalContext.Inputs` carries only four roles (`device_soc`, `goal_target`, `goal_deadline`, `goal_want`, `GoalInputs.cs:19`) and `GoalInputSample` has no read time (`GoalInputs.cs:8`): calendar, range and plugged do not reach a source | A separate EV inputs view (typed, with read times and freshness) plus an architecture test like the one for `GoalInputs`. Do not widen `GoalInputs` |
| B8 | Merge key is (device, deadline) (`GoalMerge.cs:22`): two goals without a deadline fall into the `@horizon` group (`Planner.cs:52`) and displace each other; a manual goal beats a module goal only at the same deadline (different deadlines coexist and double count, `ManualGoalStoreTests.cs:298`) | The module guarantees **unique deadlines** across its goals (test by property): trip = start (hard); cap/weekly = local midnight of the day (soft; target 100 on the weekly day); floor = no deadline, hard, `sources` = cheapest zone only, `escalate: false` (at most one goal without deadline). A manual goal for the device suppresses the module's goals for that device (the module composes with the manual goal's target in mind), it is not left to merge |
| B9 | `ManualGoalStore` requires a configured `goal:` of kind soc (`ManualGoalStore.cs:118`), hard-codes `on_met: Opportunistic`, margin 0 and `escalate = hard` (`:270, :295`) and expires only at its deadline or when the device leaves the configuration (`:180-200`) | `ev:` generates a synthetic soc `GoalSpec` owned by the module (`ConfigGoalSource` skips it; a note in DECISIONS: goal signals normally come from the device block, `DECISIONS.md:583`), and `ManualGoalStore` accepts the EV device and ends a manual EV goal at target, unplug or deadline |
| B10 | SoC estimate between readings: re-reporting the same value must not reset the anchor (anchor only on a changed value); go-e energy for the air conditioning of a plugged car never reaches the SoC; the plan contradicted itself ((g) estimate between readings vs goals inactive after SoC `max_age`) | Freshness of the estimate comes from fresh charger power with its own `ev.soc_max_age` and a drift limit in kWh (both configuration); beyond them the goals are inactive (never the last value). Estimate resets on a fresh changed reading |
| B11 | The EV assessment can use only reasons the closed enums `GoalInactiveReason` and `GoalInputKind` allow (`GoalTypes.cs:62-93`) | Extend them minimally and add the matching SPA, JSON and sentences, or keep EV-specific reasons in the EV assessment list (preferred) |
| B12 | `Planner.cs:752` counts soft goals in the shortfall: a PV-only cap with a midnight deadline raises a constant `goal_shortfall` note on cloudy evenings | Do not let module caps raise the shortfall note (or give them a separate note class); test |

## 5. Live prerequisites (each step needs the maintainer's explicit consent; do not do them silently)

For the module to do anything on the live system the chain `economics:` (present) + `sources:` on the device (present: `ev_charger sources: pv_and_zone:offpeak`) + `start: headroom` (check) + `planner:` with `dispatch` at least `narrow` is needed. `start: headroom` changes the v2 behaviour of the go-e on its own, independent of the EV module. Order on live: ECO-16 observed -> `start: headroom` -> `planner.dispatch: narrow` (one shadow day first with `off`) -> `full`. Live facts at 2026-10-30: add-on 0.11.8, mode `active`, control allowed, planner section present with `dispatch: off`, 29 signals, PV forecast basis `series`, the wizard saved the economics block and the overrides `persistence_up_cycles: 2` and `start_hysteresis_w: 100` survived it (check after any wizard save). The go-e stop actuator is `select.goe_422752_frc`, the amp actuator `number.goe_422752_amp` (6 to 20 A, L1).

## 6. Slices (each shadow-releasable; tests first, on the virtual clock, in `Europe/Warsaw`, including the days 2026-03-29 (23 h) and 2026-10-25 (25 h))

New requirement ids to add to `docs/02-REQUIREMENTS.md` (unticked; tick only when verified):

- **EV-01** `ev:` section (device, capacity, signals SoC / range / plugged / calendar, thresholds from `control.ev`), parser, issues, wizard keeps, read-only `GET api/ev`
- **EV-02** signals: calendar-event kind (bounded attributes `message`, `description`, `start_time`, `end_time`, `all_day`; description length limit), distance kind (km, mi), roles `ev_range` and `ev_plugged`, `on_change` freshness for `ev_plugged` only; none of these enter `ProtectionInputs` or `DispatchInputs`
- **EV-03** `TripParser` (pure domain): `km:` rules, naive local times, DST, unparsed list, bound `ev.max_trip_km`, trip removed at its start
- **EV-04** kWh/km learning (smoothing, fallback, plausibility, same-report pairs, outside charging, persisted in `ev.json`)
- **EV-05** target SoC rule (floor, floor_after, reserve, losses, ready_margin, cap 90 %, weekly 100 %); table tests 50 % -> 90 %, 60 % -> 100 %
- **EV-06** policy and `EvGoalSource`: modes, floor, cap, weekly, plugged gating, stale expiry, incremental needs, unique deadlines, stable revision, snapshot outside the guard thread; architecture test: no path to effector, command port, overrides or guard
- **EV-07** EV assessment list (section 3 point 5) in `api/ev` and `cause` in `api/plan`, SPA lists and en/pl sentences complete, contract tests; `ReasonCode` list stays v7
- **EV-08** every EV failure-table row (section 9) has a named test
- **EV-09** manual mode and charge now: `ev.json`, `POST api/ev/mode`, `POST api/ev/charge-now`, MQTT select/number/button with `MqttTopicGuard` extension, expiry; never a write to an entity the user has not declared as an actuator
- **EV-10** UI: EV card in `#/plan`, EV row on the device card, unparsed events, controls; en and pl, 375 px, both themes, entity IDs in monospace
- **EV-11** without `ev:`, or at `planner.dispatch: off`, dispatch decisions are byte for byte v2 (digests); at `narrow` a subset of v2
- **EV-12** live shadow week: assessment vs plan vs real charging, one trip checked by hand; then `narrow`, later `full` with consent

### S0 (before any EV code): planner hardening and records

1. Exception isolation per goal source in `GoalEvaluator` plus a guard-step test with a throwing source (B1).
2. Planner + `PlanView` tests: "cap met with `on_met: stop`, trip unmet" (no test exists, only single-goal tests at `PlannerTests.cs:275-288, 327-340`), and "two SoC goals on one device are not double counted" once the incremental rule is in place (B2).
3. `04-ALGORITHMS.md` section 6 to list version 7 (it ends at v6, lines ~182-194).
4. DECISIONS.md lines for section 3 points 1 to 10 and the synthetic `GoalSpec` ownership (B9), each one line, dated.
5. Add EV-01..12 to `02-REQUIREMENTS.md`; mark the EV section of `12-EV-MODULE.md` as superseded by this file where they differ.
Release as a patch version only if a behaviour or test-visible contract changed (use `/deploy`).

### S1: configuration `ev:` (size S)

`EvSectionParser`, `Domain/Ev` (`EvDefinition`), `control.ev` parameters in `control-defaults.yaml` (floor 25, floor_after 25, reserve 15, cap 90, weekly_day, ready_margin 30m, loss 0.10, kwh_per_km fallback, learn.*, max_trip_km, soc_max_age, drift limit, target_change_threshold, charge_now.*) bound but not yet read, add `ev` to `RootKeys` (`SwitchboardDocumentParser.cs:31`; the wizard then keeps the section, `SetupDocumentMapper.cs:57`), `GET api/ev` (enabled, echo, issues), `deploy/examples/ev.yaml` (commented).
Issues: unknown or duplicate device, `ev_device_has_goal` (the device has its own `goal:`), `ev_device_cannot_start` (`start` is `restore_only`), `ev_device_without_sources`, `ev_without_planner_dispatch`, ranges, `duplicate_entity` for the SoC entity.
Acceptance: a 0.11.8 config without `ev:` loads and decides identically (digests); wizard round trip of `ev:`; no behaviour change. Read-only check of the live file for `start:` on `ev_charger` (report to the maintainer, no write).

### S2 and S3 in parallel after S1

**S2 signals and trips (size L)**: calendar-event kind (new `SignalKind`, parser limits: the attribute text limit is 64 chars at `EntityUpdate.cs:40`, so the description needs its own bounded limit; non-text attributes such as `all_day` read as empty today, `SubscribeEntitiesParser.cs:265`, so booleans need handling), distance kind (unit conversion only in the signal layer), roles, naive local time rule (section 3 point 8), `ev_plugged` `on_change` (allowed today only for `DeviceState`, `SwitchboardConfiguration.cs:47`; the plugged state map is a configurable list of states), the EV inputs view (B7), `TripParser`, `api/ev` shows next trip and unparsed events. **Live read-only discovery** (report, change nothing): the car integration's SoC and electric-range entities, the calendar entity name and a real event sample, the go-e car-state entity, how often each reports and whether they report while the car sleeps or is unplugged, `last_reported` behaviour; this sets `max_age` values. Tests: attribute shapes, `km: 42,5`, no `km`, all-day, naive local time across DST, expired, oversized description (reject whole), architecture (no EV types in guard or dispatch inputs).

**S3 energy model (size M)**: `KwhPerKmLearner`, SoC estimate with the anchor rule (B10), `TargetSoc`, frozen target per trip with the change threshold (B3), `ev.json` (own file, set aside on corruption, bounded). Tests: the table (50 -> 90, 60 -> 100, boundaries, trip longer than the range), smoothing and fallback, sample rejection (range 0, SoC below `learn.min_soc`, pairs from different reports, during charging), restart keeps the learned value, corrupt `ev.json`.

### S4 merged into S5

### S5 `EvGoalSource` (size L, the core)

Snapshot outside the guard thread (B1), composition rules (B2, B2b, B8), stable revision (B3), trip removed at start (B4), plugged gating, stale expiry, calendar unavailable keeps the last parsed next trip until its start, synthetic soc `GoalSpec`, the assessment list and `api/ev` assessment, `cause` in `api/plan` (B5), B11, B12, all rows of section 9.
Acceptance: each failure row has a named test; digests equal v2 for `off` and for no `ev:`; the plan shows the EV goals in `api/plan`; the property tests (needs bound, unique deadlines, stable revision); throwing source test; architecture test (no effector, command port, override or guard). Shadow only.

### S6 and S7 in parallel after S5

**S6 manual control (size L)**: `POST api/ev/mode`, `POST api/ev/charge-now`, MQTT entities by the `DispatchEntities` pattern, `MqttTopicGuard` allow-list extension (own topics only; the subscription list is closed, `MqttTopicGuard.cs:43-49`, publishing on own `/set` forbidden `:65-67`), mode and charge-now persisted in `ev.json`, expiry, `409` at `narrow`/`off` with the `ForceOn` hint, changes to `ManualGoalStore` (B9). Tests: retained command ignored, mode survives restart, charge-now expires, 404/409, never beyond the L1 limit (the goal still passes the guard and envelope).
**S7 UI (size M)**: EV card in `#/plan`, device-card row, unparsed events and the hidden-next-trip warning, mode and charge-now controls, SPA contract lists, en and pl. Look at it in a browser at 375 px in both themes (as done for PLAN-19).

### S8 live shadow week and rollout

Release with `/deploy`. One week at `planner.dispatch: off` comparing the assessment with the real charging and checking one trip by hand; then, each step with the maintainer's explicit consent and after one shadow day: `start: headroom` on `ev_charger` -> `narrow` -> `full`. Checklist per step: no allocation rejected by the envelope, no `Urgent` in a peak for a non-urgent goal, no oscillation (the live `persistence_up_cycles: 2` also governs the guard's release of a reduction request), decisions for other devices unchanged.

### S9 deferred

Trip list from an HA template sensor and device unavailability windows in the planner (`PlanDevice`), only if one next trip proves insufficient.

Order: S0 -> S1 -> (S2 || S3) -> S5 -> (S6 || S7) -> S8. About 10 to 13 agent-days plus the shadow week.

## 7. Working rules for the implementation session

- Work as an orchestrator of subagents (maintainer's workflow): one planning/implementing agent per slice, an independent review agent per slice before it is called done; the orchestrator re-verifies with `dotnet build Switchboard.slnx -c Release --no-incremental` (0 warnings), `dotnet test`, `cd web && npm test && npm run build` when `web/` changed.
- Release only through `/deploy` (`deploy/release.ps1`, `docs/RELEASE.md`). Never by hand, never without the maintainer's fresh explicit approval for commit + push + image + deploy. The release preflight refuses stray files; keep new files in the allowed roots.
- Never touch the live `switchboard.yaml`, add-on options, `mode`, the control switch or any `switch.grzalka_*`, `number.goe_*`, `select.goe_*` without explicit consent for that exact action. The live HA is the test bed; never start a new HA. Only GitHub account `krzyl2`. Do not change the maintainer's HA settings; HA-side YAML is the maintainer's to edit.
- Hard rules of `CLAUDE.md` stand: protection first, no actuator the user has not declared, no entity ID in the core, shadow default, a reason (or assessment code) for every decision, no literals for control parameters, injected clock, `last_reported` freshness, no exposed port beyond ingress, no outbound telemetry, permissive licences only, "not a protective device".
- Replies in Polish with diacritics, terse. One dated line per decision into `DECISIONS.md`. Update `docs/STATUS.md` (counts, slice states) after each slice with targeted edits.
- The private token budget rule is waived for this project (mention a breach once in the final report).
- Tests encode why (Rule 9): a test that cannot fail when the business logic changes is wrong. Do not search the repo root recursively; name `src/ web/ switchboard/ deploy/ tests/ docs/`.

## 8. Risks (ranked)

1. B1 exception in a goal source stopping the guard (critical, designed out in S0 and S5).
2. B2 double counting of SoC goals.
3. B4 `Missed` trip becoming `Urgent` in a peak.
4. B3 revision flapping between plan and v2.
5. Missing live prerequisites: without `start: headroom`, `sources` and `planner` at `narrow` or more the module does nothing; and `start: headroom` changes v2 behaviour of the go-e by itself.
6. A stale or sleeping car integration: long windows with inactive goals, during which the car is v2 again (may charge off-peak to full): the UI must say so.
7. kWh/km poisoning (PHEV: range 0 with a depleted buffer, SoC/range from different moments, sample taken while charging, total range mapped instead of electric range).
8. SoC estimate error in the taper above about 90 %; drift limit mitigates.
9. "Off" and "charge now" are dead at `off`/`narrow` (documented, 409 with the hint).
10. A calendar all-day or no-`km:` event hides the next trip; overlapping events.
11. Documentation drift (04 section 6, SPA contract lists, DOCS.md, CHANGELOG, the requirement list).

## 9. EV failure-table rows (each needs a named test; index them in a test file header like `PlannerFailureTableTests.cs`)

| Condition | Reaction | Visible as |
|---|---|---|
| SoC stale or unavailable (`max_age` from `last_reported`, estimate beyond `ev.soc_max_age` or the drift limit) | goals inactive (never the last value), no new plan, warning "trip goal at risk", entries expire and the car returns to v2 | `ev_soc_stale`, `goalsInactive input_stale` |
| Range stale, 0 or unknown | last smoothed kWh/km or the fallback, sample rejected | `ev_range_unusable` |
| Calendar unavailable | keep the last parsed next trip until its start, no new trips, floor holds | `ev_calendar_unavailable` |
| Calendar entity not updated for long | `max_age` large (for example 24 h) and based on availability; verify live | note |
| Event without `km:`, all-day, km out of bounds, overlapping events, an event that hides the next trip | ignored, listed as unparsed, warning when it hides a trip | UI |
| Trip in progress or departure already passed (B4) | trip removed at its start | none |
| Trip added just before departure | `infeasible` with the shortfall shown | plan note |
| No time at home before the departure | hard goal shows `infeasible` | plan note |
| Not plugged | no goal, no control, nothing reserved | `ev_not_plugged` |
| Plugged but the go-e is unavailable or its link is lost (plugged state `unavailable`) | goals inactive | `ev_not_plugged` or input reason |
| PV forecast missing or stale | existing `pv_forecast_missing`, zero PV, zones in `sources`, hard goals by escalation | `ev_forecast_missing` |
| Clock step, DST (23 and 25 hour days, ambiguous or nonexistent local calendar times), weekly day in local time | absolute deadlines, naive calendar times in the add-on zone | tests on 2026-03-29 and 2026-10-25 |
| Add-on zone differs from the HA zone | calendar times misread: show the zone in `api/ev`, issue if the entity's own zone attribute disagrees | issue |
| Restart | mode, charge-now, kWh/km, last trip from `ev.json`; plan not persisted; first plan after `start_delay`; v2 until then | `status absent` |
| `ev.json` corrupt | set aside as `.corrupt`, defaults, no kill switch | log |
| SoC jump (charging outside the add-on) or the same value re-reported | estimate anchored only on a changed value; a jump resets the estimate | log |
| Air conditioning on a plugged car | go-e energy not in the SoC: drift limit | note |
| Configuration change | `ConfigRevision`: foreign plan, v2 for the device | `plan_fallback` |
| Goal source throws (B1) | no goals from that source, one log line, guard loop unaffected | log |
| Protection / L1 | guard and dispatch always win; the module only proposes | existing |

## 10. Definition of done for the module

All of EV-01..11 ticked and verified; EV-12 (live shadow week, then `narrow`, then `full`) done with the maintainer's consent; digests prove no change without `ev:` and at `off`; the architecture tests prove the module has no path to the effector; docs updated (`12-EV-MODULE.md` pointer, `04-ALGORITHMS.md`, `DECISIONS.md`, `STATUS.md`, `02-REQUIREMENTS.md`, `DOCS.md`, `CHANGELOG`); released through `/deploy`.

## 11. As built: S6 manual control (2026-10-30)

The user's controls of the module (EV-09). They change only the add-on's own state (`ev.json`, a manual goal in `state.json`) and write no entity. This section is the contract the UI (S7) builds on.

### Modes

`auto` (the module's own rules), `pv_only` (surplus only: the cap goal, no floor, no trip) and `full_charge` (one-off: the weekly goal at 100 % on any day, ending as late as the window allows; the module returns to `auto` by itself once the estimate reaches 100 % and records the full charge). "Off" is not a mode: it is the device's own `force_off` override, set by the user and never by the module. The mode is kept in `ev.json` and survives a restart. Changes are atomic in memory at once (`EvManualState`); the engine persists them on its next computation (the only writer of `ev.json`).

### Web API (ingress only, JSON body required, body at most 512 bytes, strict keys)

| Request | Body | Answer |
|---|---|---|
| `POST api/ev/mode` | `{"mode":"auto"\|"pv_only"\|"full_charge"}` | `200 {"mode":"…"}` |
| `POST api/ev/charge-now` | `{}` or `{"targetPct":number?,"durationMinutes":number?}` (only these two keys; absent = `control.ev.charge_now.target` / `.duration`) | `200 {"active":true,"targetPct":80,"until":"<ISO instant>"}` |
| `DELETE api/ev/charge-now` | none | `200 {"ended":true}` |

Refusals are `{"code","message"}` with a closed code: `400 bad_request`; `404 not_configured` (no `ev:` section; also mode and DELETE), `404 no_charge_now` (DELETE without a running charge-now); `409 needs_full_dispatch` (charge now only works at `planner.dispatch: full`; the message points to the device's `force_on` override), `409 not_plugged`, `409 soc_unknown` (no SoC estimate), `409 already_at_target`; `422 target_out_of_range` (not in (0, 100]), `422 duration_out_of_range` (not above zero), `422 deadline_beyond_horizon` (`control.planner.horizon`).

### `GET api/ev`: new field `manual`

`manual: { "mode": "auto"|"pv_only"|"full_charge", "chargeNow": { "active": bool, "targetPct": number|null, "until": ISO|null } }`; null without an `ev:` section. It reads from memory (what the user set, at once); `assessment` follows on the module's next computation (at most one dispatch cycle later). An ended charge-now (past `until`) is not active. The assessment code is `ev_manual` while a charge-now or a full charge is in force and `ev_pv_only` in `pv_only`.

### Charge now

A manual hard SoC goal for the EV device in the existing manual goal store (`ManualGoalStore`), deadline now + duration, SoC from the module's estimate (not the raw signal), the device's own `sources`, escalating like any hard goal (the plan makes it `Urgent` only when the margin is within `escalation_margin`, using the device's static `CeilingW`). It suppresses the module's own goals for the device (B8, no double counting). It ends at its target (the estimate reaches it), when the car is unplugged (an unknown plugged state ends nothing), at its deadline, or by `DELETE`. `ModuleOwned` synthetic `GoalSpec` (B9): built from `ev:` and `control.ev` for the EV device only; any other device without a `goal:` still refuses a SoC goal. The guard and the dispatch envelope bound the charger as for any goal.

### MQTT (only while a valid configuration holds an `ev:` section)

`select.switchboard_ev_mode` (auto, pv_only, full_charge), `number.switchboard_ev_charge_now_target` (1 to 100 %, box, default `control.ev.charge_now.target`, kept in memory) and `button.switchboard_ev_charge_now` (payload `PRESS`; starts a charge-now with the number's value and the configured duration). Command topics `switchboard/ev_mode/set`, `…/ev_charge_now_target/set`, `…/ev_charge_now/set` are subscribed only with an `ev:` section (`MqttTopicGuard`), never from a retained message; the add-on never publishes to them. Without `ev:` the entities are retracted; without a valid configuration nothing is retracted.

### Notes

- `EvMode.Off` is removed (the module never sets "off"); the assessment code `ev_off` stays in the closed list but nothing emits it until a later slice may read the device's override on the read side.
- Mode and charge-now are not restored if `ev.json` is lost; a charge-now restored from `ev.json` without its manual goal (state.json lost) is cleared by the engine on its next computation.
- Target rule (one for the API, MQTT and the UI): above 0 and at most 100 percent. The MQTT number's slider runs 1 to 100 in steps of 1 only as a grid; a value of 0.5 sent by hand or typed in the UI is accepted.
- `DELETE api/ev/charge-now` ends the EV device's manual goal whenever one exists, even if the state holds no charge-now (a request racing the engine, `ev.json` lost); `404 no_charge_now` only when there is neither. A duration too long to represent is still a positive one and answers `422 deadline_beyond_horizon`.
- Seeding from `ev.json` is per field: a mode or a charge-now the user set before the engine's first computation wins for that field, the other field comes from the file.
- The engine clears an expired or orphaned charge-now by reading the manual goal inside the atomic state update, so a charge-now that begins during a computation (goal first, then state) is never cleared on a goal that did not exist yet.
- An unknown plugged state (signal configured but stale) does not block the start of a charge-now; only an explicit "not plugged" does. The goal then ends on a later explicit unplug, never on an unknown state.
- A requested `full_charge` stays in force until the SoC estimate reaches 100 %: a car that tops out at 99 % or is unplugged keeps the mode until it is changed by hand (known, to be reviewed after the shadow week).
