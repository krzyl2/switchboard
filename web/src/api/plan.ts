import { SIGNAL_STATES } from './signals';
import type { SignalState } from './signals';
import { bool, count, fail, field, instant, list, nullable, number, object, oneOf, text } from './strict';

// Contract of GET api/plan (src/Switchboard.Host/Web/PlanEndpoint.cs, 15-PLANNER "API"): whether a planner
// is configured and how far its plan may reach, the plan the planner service holds (slots, an entry per
// device and slot, the cost against two references, the notes, the last solve) and every device goal with
// its status. Read-only; the plan is a forecast-based estimate. PlanSpaContractTests compares the closed
// lists below with the backend's enums and the keys of plan.sample.json with what the endpoint writes.

/** The plan's state: fresh (usable), stale (too old or from the future), suspended (the planner stands aside), absent (none held), error (the last solve failed and none is held). */
export const PLAN_STATUSES = ['absent', 'error', 'fresh', 'stale', 'suspended'] as const;
/** planner.dispatch as configured: off means the plan is only shown. */
export const DISPATCH_MODES = ['off', 'narrow', 'full'] as const;
export const GOAL_KINDS = ['energy', 'soc'] as const;
/** Where a goal came from; the numeric precedence in the backend is manual over module over entity over config. */
export const GOAL_SOURCES = ['config', 'entity', 'module', 'manual'] as const;
/** The goal model's statuses, the plan's verdicts (at_risk, infeasible) and the one the endpoint adds for a request the precedence merge set aside. */
export const GOAL_STATUSES = ['on_track', 'at_risk', 'infeasible', 'met', 'expired', 'missed', 'inactive', 'superseded'] as const;
export const GOAL_ON_MET = ['opportunistic', 'stop'] as const;
export const INACTIVE_REASONS = ['want_off', 'input_missing', 'input_stale', 'input_unavailable', 'input_invalid', 'input_out_of_range'] as const;
export const INACTIVE_INPUTS = ['soc', 'target', 'deadline', 'want'] as const;
export const PLAN_NOTES = [
  'pv_forecast_missing',
  'pv_forecast_partial',
  'price_curve_partial',
  'goal_shortfall',
  'goal_infeasible',
  'goal_urgent',
  'goal_missed',
  'goal_input_stale',
  'solver_timeout',
  'plan_invalid',
] as const;
/** What a plan entry says about a slot: run on its class, wait, done, run because the deadline needs it, or nothing for a device that is not planned. */
export const ENTRY_WANTS = ['run', 'defer', 'done', 'urgent', 'none'] as const;
export const PV_BASES = ['none', 'series', 'totals'] as const;
export const LOAD_BASES = ['fallback', 'partial', 'profile'] as const;
export const PRICE_BASES = ['tariff', 'curve', 'partial'] as const;
export const SOLVE_RESULTS = ['computed', 'timeout', 'invalid', 'failed'] as const;
export const VIOLATION_CODES = [
  'invalid_number',
  'outside_horizon',
  'unknown_device',
  'capacity_exceeded',
  'device_power_exceeded',
  'energy_exceeds_need',
  'surplus_exceeded',
  'not_plannable',
  'class_outside_sources',
  'hard_goal_unmet',
  'infeasible_claimed',
] as const;

export type PlanStatus = (typeof PLAN_STATUSES)[number];
export type DispatchMode = (typeof DISPATCH_MODES)[number];
export type GoalKind = (typeof GOAL_KINDS)[number];
export type GoalSource = (typeof GOAL_SOURCES)[number];
export type GoalStatus = (typeof GOAL_STATUSES)[number];
export type GoalOnMet = (typeof GOAL_ON_MET)[number];
export type InactiveReason = (typeof INACTIVE_REASONS)[number];
export type InactiveInput = (typeof INACTIVE_INPUTS)[number];
export type PlanNote = (typeof PLAN_NOTES)[number];
export type EntryWant = (typeof ENTRY_WANTS)[number];
export type PvBasis = (typeof PV_BASES)[number];
export type LoadBasis = (typeof LOAD_BASES)[number];
export type PlanPriceBasis = (typeof PRICE_BASES)[number];
export type SolveResult = (typeof SOLVE_RESULTS)[number];
export type ViolationCode = (typeof VIOLATION_CODES)[number];

export interface PvForecastInput {
  /** The signal's state as the signal layer reports it, or `absent` when no PV forecast signal is mapped. */
  state: SignalState | 'absent';
  ageS: number | null;
  coversUntil: Date | null;
  basis: PvBasis;
  derate: number;
}

export interface PlanInputsView {
  /** Null without a planner: section. */
  pvForecast: PvForecastInput | null;
  loadForecast: { basis: LoadBasis; daysFilled: number } | null;
  prices: { basis: PlanPriceBasis; coversUntil: Date | null } | null;
  goalsActive: number;
  goalsInactive: { device: string; reason: InactiveReason; input: InactiveInput }[];
}

export interface PlanSlot {
  start: Date;
  end: Date;
  /** The import tariff's zone when one zone holds the whole slot. */
  zone: string | null;
  importPrice: number | null;
  exportPrice: number | null;
  pvKwh: number;
  baseKwh: number;
  surplusKwh: number;
}

export interface PlanEntry {
  start: Date;
  end: Date;
  want: EntryWant;
  /** `pv` or `zone:<id>` for run and urgent; null otherwise. */
  class: string | null;
  energyKwh: number;
  maxPowerW: number;
}

export interface PlanGoal {
  kind: GoalKind;
  hard: boolean;
  source: GoalSource;
  targetKwh: number | null;
  targetSoc: number | null;
  currentSoc: number | null;
  /** Null means the horizon end. */
  deadline: Date | null;
  marginS: number;
  remainingKwh: number | null;
  deliveredKwh: number | null;
  status: GoalStatus;
  inactiveReason: InactiveReason | null;
  inactiveInput: InactiveInput | null;
  onMet: GoalOnMet | null;
  escalate: boolean;
  /** The classes the goal may be scheduled on, in the configuration grammar; null means the device's own. */
  sources: string | null;
  since: Date | null;
  /** Energy the plan places for the goal; null without a plan or while it is not fresh. */
  plannedKwh: number | null;
  shortfallKwh: number | null;
  latestGridStart: Date | null;
  urgent: boolean | null;
  /** The device a request belongs to; set only on a request the precedence merge set aside, which is listed apart from the devices. */
  device: string | null;
}

export interface PlanDevice {
  device: string;
  /** The goal the planner works on first (the earliest deadline). */
  goal: PlanGoal | null;
  goals: PlanGoal[];
  entries: PlanEntry[];
  shortfallKwh: number | null;
  predictedCompletion: Date | null;
}

export interface PlanCost {
  planned: number;
  referenceV2: number;
  referenceAsap: number;
  currency: string | null;
}

export interface PlanSolve {
  at: Date;
  durationMs: number;
  result: SolveResult;
  /** What made the planner run (first, cadence, goal, progress, config, device_state, safe_state_exit, clock_step, clock_step_back): words in the UI, raw text for any other. */
  reason: string;
  violations: ViolationCode[];
}

export interface PlanIssue {
  code: string;
  path: string;
  message: string;
}

export interface Plan {
  at: Date;
  /** False without a planner: section; the goals are still shown, they simply feed nothing. */
  enabled: boolean;
  dispatchMode: DispatchMode | null;
  status: PlanStatus;
  inputs: PlanInputsView;
  devices: PlanDevice[];
  superseded: PlanGoal[];
  notes: PlanNote[];
  issues: PlanIssue[];
  slots: PlanSlot[];
  id: string | null;
  computedAt: Date | null;
  validUntil: Date | null;
  horizonEnd: Date | null;
  slotMinutes: number | null;
  configRevision: number | null;
  suspended: boolean;
  cost: PlanCost | null;
  lastSolve: PlanSolve | null;
}

const PLAN_URL = 'api/plan';

const SOURCES = /^(pv_only|any|pv_and_zone:[^,\s:]+(,[^,\s:]+)*)$/;

function sources(raw: unknown, path: string): string {
  const value = text(raw, path);
  if (!SOURCES.test(value)) fail(path, 'pv_only, any or pv_and_zone:<id>[,<id>]');
  return value;
}

function pvForecast(raw: unknown, path: string): PvForecastInput {
  const r = object(raw, path);
  return {
    state: oneOf([...SIGNAL_STATES, 'absent'] as const, field(r, 'state', path), `${path}.state`),
    ageS: nullable(field(r, 'ageS', path), `${path}.ageS`, number),
    coversUntil: nullable(field(r, 'coversUntil', path), `${path}.coversUntil`, instant),
    basis: oneOf(PV_BASES, field(r, 'basis', path), `${path}.basis`),
    derate: number(field(r, 'derate', path), `${path}.derate`),
  };
}

function inputs(raw: unknown, path: string): PlanInputsView {
  const r = object(raw, path);
  return {
    pvForecast: nullable(field(r, 'pvForecast', path), `${path}.pvForecast`, pvForecast),
    loadForecast: nullable(field(r, 'loadForecast', path), `${path}.loadForecast`, (v, p) => {
      const l = object(v, p);
      return { basis: oneOf(LOAD_BASES, field(l, 'basis', p), `${p}.basis`), daysFilled: count(field(l, 'daysFilled', p), `${p}.daysFilled`) };
    }),
    prices: nullable(field(r, 'prices', path), `${path}.prices`, (v, p) => {
      const x = object(v, p);
      return { basis: oneOf(PRICE_BASES, field(x, 'basis', p), `${p}.basis`), coversUntil: nullable(field(x, 'coversUntil', p), `${p}.coversUntil`, instant) };
    }),
    goalsActive: count(field(r, 'goalsActive', path), `${path}.goalsActive`),
    goalsInactive: list(field(r, 'goalsInactive', path), `${path}.goalsInactive`, (v, p) => {
      const g = object(v, p);
      return {
        device: text(field(g, 'device', p), `${p}.device`),
        reason: oneOf(INACTIVE_REASONS, field(g, 'reason', p), `${p}.reason`),
        input: oneOf(INACTIVE_INPUTS, field(g, 'input', p), `${p}.input`),
      };
    }),
  };
}

function slot(raw: unknown, path: string): PlanSlot {
  const r = object(raw, path);
  return {
    start: instant(field(r, 'start', path), `${path}.start`),
    end: instant(field(r, 'end', path), `${path}.end`),
    zone: nullable(field(r, 'zone', path), `${path}.zone`, text),
    importPrice: nullable(field(r, 'importPrice', path), `${path}.importPrice`, number),
    exportPrice: nullable(field(r, 'exportPrice', path), `${path}.exportPrice`, number),
    pvKwh: number(field(r, 'pvKwh', path), `${path}.pvKwh`),
    baseKwh: number(field(r, 'baseKwh', path), `${path}.baseKwh`),
    surplusKwh: number(field(r, 'surplusKwh', path), `${path}.surplusKwh`),
  };
}

const CLASS = /^(pv|zone:[^\s:]+)$/;

function entry(raw: unknown, path: string): PlanEntry {
  const r = object(raw, path);
  const want = oneOf(ENTRY_WANTS, field(r, 'want', path), `${path}.want`);
  const cls = nullable(field(r, 'class', path), `${path}.class`, (v, p) => {
    const value = text(v, p);
    if (!CLASS.test(value)) fail(p, 'pv or zone:<id>');
    return value;
  });
  // Only a slot where the device runs names the energy class it runs on.
  if ((want === 'run' || want === 'urgent') !== (cls !== null)) fail(`${path}.class`, 'present exactly for run and urgent');
  return {
    start: instant(field(r, 'start', path), `${path}.start`),
    end: instant(field(r, 'end', path), `${path}.end`),
    want,
    class: cls,
    energyKwh: number(field(r, 'energyKwh', path), `${path}.energyKwh`),
    maxPowerW: number(field(r, 'maxPowerW', path), `${path}.maxPowerW`),
  };
}

function goal(raw: unknown, path: string): PlanGoal {
  const r = object(raw, path);
  const status = oneOf(GOAL_STATUSES, field(r, 'status', path), `${path}.status`);
  const inactiveReason = nullable(field(r, 'inactiveReason', path), `${path}.inactiveReason`, (v, p) => oneOf(INACTIVE_REASONS, v, p));
  const inactiveInput = nullable(field(r, 'inactiveInput', path), `${path}.inactiveInput`, (v, p) => oneOf(INACTIVE_INPUTS, v, p));
  // A goal that cannot be evaluated says why, and no other goal does.
  if ((status === 'inactive') !== (inactiveReason !== null) || (inactiveReason === null) !== (inactiveInput === null)) {
    fail(`${path}.inactiveReason`, 'present exactly for an inactive goal, with its input');
  }
  return {
    kind: oneOf(GOAL_KINDS, field(r, 'kind', path), `${path}.kind`),
    hard: bool(field(r, 'hard', path), `${path}.hard`),
    source: oneOf(GOAL_SOURCES, field(r, 'source', path), `${path}.source`),
    targetKwh: nullable(field(r, 'targetKwh', path), `${path}.targetKwh`, number),
    targetSoc: nullable(field(r, 'targetSoc', path), `${path}.targetSoc`, number),
    currentSoc: nullable(field(r, 'currentSoc', path), `${path}.currentSoc`, number),
    deadline: nullable(field(r, 'deadline', path), `${path}.deadline`, instant),
    marginS: number(field(r, 'marginS', path), `${path}.marginS`),
    remainingKwh: nullable(field(r, 'remainingKwh', path), `${path}.remainingKwh`, number),
    deliveredKwh: nullable(field(r, 'deliveredKwh', path), `${path}.deliveredKwh`, number),
    status,
    inactiveReason,
    inactiveInput,
    onMet: nullable(field(r, 'onMet', path), `${path}.onMet`, (v, p) => oneOf(GOAL_ON_MET, v, p)),
    escalate: bool(field(r, 'escalate', path), `${path}.escalate`),
    sources: nullable(field(r, 'sources', path), `${path}.sources`, sources),
    since: nullable(field(r, 'since', path), `${path}.since`, instant),
    plannedKwh: nullable(field(r, 'plannedKwh', path), `${path}.plannedKwh`, number),
    shortfallKwh: nullable(field(r, 'shortfallKwh', path), `${path}.shortfallKwh`, number),
    latestGridStart: nullable(field(r, 'latestGridStart', path), `${path}.latestGridStart`, instant),
    urgent: nullable(field(r, 'urgent', path), `${path}.urgent`, bool),
    device: nullable(field(r, 'device', path), `${path}.device`, text),
  };
}

function device(raw: unknown, path: string): PlanDevice {
  const r = object(raw, path);
  return {
    device: text(field(r, 'device', path), `${path}.device`),
    goal: nullable(field(r, 'goal', path), `${path}.goal`, goal),
    goals: list(field(r, 'goals', path), `${path}.goals`, goal),
    entries: list(field(r, 'entries', path), `${path}.entries`, entry),
    shortfallKwh: nullable(field(r, 'shortfallKwh', path), `${path}.shortfallKwh`, number),
    predictedCompletion: nullable(field(r, 'predictedCompletion', path), `${path}.predictedCompletion`, instant),
  };
}

function cost(raw: unknown, path: string): PlanCost {
  const r = object(raw, path);
  return {
    planned: number(field(r, 'planned', path), `${path}.planned`),
    referenceV2: number(field(r, 'referenceV2', path), `${path}.referenceV2`),
    referenceAsap: number(field(r, 'referenceAsap', path), `${path}.referenceAsap`),
    currency: nullable(field(r, 'currency', path), `${path}.currency`, text),
  };
}

function solve(raw: unknown, path: string): PlanSolve {
  const r = object(raw, path);
  return {
    at: instant(field(r, 'at', path), `${path}.at`),
    durationMs: number(field(r, 'durationMs', path), `${path}.durationMs`),
    result: oneOf(SOLVE_RESULTS, field(r, 'result', path), `${path}.result`),
    reason: text(field(r, 'reason', path), `${path}.reason`),
    violations: list(field(r, 'violations', path), `${path}.violations`, (v, p) => oneOf(VIOLATION_CODES, v, p)),
  };
}

function issue(raw: unknown, path: string): PlanIssue {
  const r = object(raw, path);
  return {
    code: text(field(r, 'code', path), `${path}.code`),
    path: text(field(r, 'path', path), `${path}.path`),
    message: text(field(r, 'message', path), `${path}.message`),
  };
}

export function parsePlan(raw: unknown): Plan {
  const path = 'plan';
  const r = object(raw, path);
  const plan: Plan = {
    at: instant(field(r, 'at', path), `${path}.at`),
    enabled: bool(field(r, 'enabled', path), `${path}.enabled`),
    dispatchMode: nullable(field(r, 'dispatchMode', path), `${path}.dispatchMode`, (v, p) => oneOf(DISPATCH_MODES, v, p)),
    status: oneOf(PLAN_STATUSES, field(r, 'status', path), `${path}.status`),
    inputs: inputs(field(r, 'inputs', path), `${path}.inputs`),
    devices: list(field(r, 'devices', path), `${path}.devices`, device),
    superseded: list(field(r, 'superseded', path), `${path}.superseded`, goal),
    notes: list(field(r, 'notes', path), `${path}.notes`, (v, p) => oneOf(PLAN_NOTES, v, p)),
    issues: list(field(r, 'issues', path), `${path}.issues`, issue),
    slots: list(field(r, 'slots', path), `${path}.slots`, slot),
    id: nullable(field(r, 'id', path), `${path}.id`, text),
    computedAt: nullable(field(r, 'computedAt', path), `${path}.computedAt`, instant),
    validUntil: nullable(field(r, 'validUntil', path), `${path}.validUntil`, instant),
    horizonEnd: nullable(field(r, 'horizonEnd', path), `${path}.horizonEnd`, instant),
    slotMinutes: nullable(field(r, 'slotMinutes', path), `${path}.slotMinutes`, number),
    configRevision: nullable(field(r, 'configRevision', path), `${path}.configRevision`, number),
    suspended: bool(field(r, 'suspended', path), `${path}.suspended`),
    cost: nullable(field(r, 'cost', path), `${path}.cost`, cost),
    lastSolve: nullable(field(r, 'lastSolve', path), `${path}.lastSolve`, solve),
  };
  // The add-on cannot name a dispatch mode without a planner, nor hold a plan without one.
  if (plan.enabled !== (plan.dispatchMode !== null)) fail(`${path}.dispatchMode`, 'present exactly when a planner is configured');
  if (!plan.enabled && (plan.id !== null || plan.cost !== null)) fail(`${path}.id`, 'null without a planner');
  // A plan, when held, brings its whole picture; none brings none of it.
  if ((plan.id === null) !== (plan.cost === null) || (plan.id === null) !== (plan.computedAt === null)) fail(`${path}.id`, 'present together with computedAt and cost');
  return plan;
}

export async function fetchPlan(signal: AbortSignal): Promise<Plan> {
  const response = await fetch(PLAN_URL, { signal, cache: 'no-store', headers: { Accept: 'application/json' } });
  if (!response.ok) throw new Error(`Plan request failed with HTTP ${response.status}.`);
  return parsePlan(await response.json());
}
