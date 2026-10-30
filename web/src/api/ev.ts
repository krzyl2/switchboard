import { EV_CAUSES, type EvCause } from './plan';
import { SIGNAL_DETAILS, SIGNAL_STATES, type SignalDetail, type SignalState } from './signals';
import { bool, fail, field, instant, list, nullable, number, object, oneOf, text } from './strict';

// Contract of GET api/ev (src/Switchboard.Host/Web/EvEndpoint.cs, docs/EV.md EV-01, EV-02, EV-07, EV-10): whether an ev: section is in force, the
// car's inputs and how fresh each one is, the next trip the calendar names and the events it ignored, and what the module offers now and why
// (its own closed list of assessment codes; ReasonCode stays at list version 7). Read-only, except the manual control of S6: POST api/ev/mode,
// POST and DELETE api/ev/charge-now, which store the module's own state in ev.json and write no entity. A JSON body is required (a cross-site
// form cannot send one without a CORS preflight, which the add-on never answers).
//
// The `manual` key belongs to S6 (docs/EV.md section 11): null without an ev: section; an answer without the key is an older add-on, and the controls stay out of the page.

/** The module's own modes (EvMode): auto, pv_only and a one-off full charge to 100 %. "Off" is the device's own ForceOff override: not a mode, never reported by the module. */
export const EV_MODES = ['auto', 'pv_only', 'full_charge'] as const;
export const EV_SOC_GAPS = ['no_reading', 'too_old', 'drift_exceeded', 'charger_stale', 'invalid_capacity'] as const;
export const EV_GOAL_KINDS = ['trip', 'weekly_full', 'cap', 'floor'] as const;
export const EV_UNPARSED_REASONS = ['all_day', 'no_km', 'km_out_of_bounds', 'km_unreadable'] as const;
export const EV_TRIP_WARNINGS = ['next_trip_may_be_hidden'] as const;
export const EV_PLANNER_DISPATCH = ['off', 'narrow', 'full'] as const;
/** Why a manual request was not taken: the closed `code` of the refusal body (docs/EV.md section 11; 400, 404, 409 and 422 answers). */
export const EV_REFUSALS = [
  'bad_request',
  'not_configured',
  'no_charge_now',
  'needs_full_dispatch',
  'not_plugged',
  'soc_unknown',
  'already_at_target',
  'target_out_of_range',
  'duration_out_of_range',
  'deadline_beyond_horizon',
] as const;

export type EvMode = (typeof EV_MODES)[number];
export type EvSocGap = (typeof EV_SOC_GAPS)[number];
export type EvGoalKind = (typeof EV_GOAL_KINDS)[number];
export type EvUnparsedReason = (typeof EV_UNPARSED_REASONS)[number];
export type EvTripWarningKind = (typeof EV_TRIP_WARNINGS)[number];
export type EvRefusal = (typeof EV_REFUSALS)[number] | 'failed';

export interface EvIssue {
  code: string;
  path: string;
  message: string;
}

export interface EvConfig {
  device: string;
  capacityKwh: number;
  socSignal: string;
  cheapZone: string | null;
  /** planner.dispatch as configured; null without a planner: section. */
  plannerDispatch: (typeof EV_PLANNER_DISPATCH)[number] | null;
  rangeSignal: string | null;
  pluggedSignal: string | null;
  pluggedStates: string[] | null;
  calendarSignal: string | null;
}

/** The control.ev values the page shows or offers as defaults. */
export interface EvParameters {
  chargeNowTargetPercent: number;
  chargeNowDurationS: number;
}

export interface EvRead {
  configured: boolean;
  state: SignalState;
  detail: SignalDetail;
  readAt: Date | null;
  ageS: number | null;
  maxAgeS: number | null;
}

export interface EvInputs {
  soc: EvRead;
  socPercent: number | null;
  range: EvRead;
  rangeKm: number | null;
  plugged: EvRead;
  isPlugged: boolean | null;
  pluggedState: string | null;
  calendar: EvRead;
}

export interface EvTrip {
  title: string;
  start: Date;
  km: number;
}

export interface EvUnparsed {
  title: string;
  start: Date;
  reason: EvUnparsedReason;
  km: number | null;
}

export interface EvTripWarning {
  kind: EvTripWarningKind;
  title: string;
  start: Date;
}

export interface EvTrips {
  nextTrip: EvTrip | null;
  unparsed: EvUnparsed[];
  warnings: EvTripWarning[];
  calendarZone: string | null;
}

export interface EvOfferedGoal {
  kind: EvGoalKind;
  targetPercent: number;
  remainingKwh: number;
  deadline: Date | null;
  hard: boolean;
  /** The classes the goal may be scheduled on, in the configuration grammar; null means the device's own. */
  sources: string | null;
  spread: boolean;
}

export interface EvAssessment {
  /** The last computation threw: the module offers nothing and the car follows the v2 rules until it recovers. */
  failed: boolean;
  /** The primary code; null when the computation failed. */
  reason: EvCause | null;
  notes: EvCause[];
  computedAt: Date | null;
  validUntil: Date | null;
  /** The snapshot is past its validity: the guard no longer sees its goals. */
  expired: boolean;
  mode: EvMode | null;
  socPercent: number | null;
  socGap: EvSocGap | null;
  kwhPerKm: number | null;
  nextTrip: EvTrip | null;
  lastFullAt: Date | null;
  goals: EvOfferedGoal[];
}

export interface EvChargeNow {
  active: boolean;
  targetPct: number | null;
  until: Date | null;
}

export interface EvManual {
  mode: EvMode;
  chargeNow: EvChargeNow;
}

export interface Ev {
  at: Date;
  enabled: boolean;
  config: EvConfig | null;
  parameters: EvParameters | null;
  issues: EvIssue[];
  addOnZone: string | null;
  inputs: EvInputs | null;
  trips: EvTrips | null;
  assessment: EvAssessment | null;
  /** Null while the add-on has no manual control (S6). */
  manual: EvManual | null;
}

const SOURCES = /^(pv_only|any|pv_and_zone:[^,\s:]+(,[^,\s:]+)*)$/;

function sources(raw: unknown, path: string): string {
  const value = text(raw, path);
  if (!SOURCES.test(value)) fail(path, 'pv_only, any or pv_and_zone:<id>[,<id>]');
  return value;
}

function config(raw: unknown, path: string): EvConfig {
  const r = object(raw, path);
  return {
    device: text(field(r, 'device', path), `${path}.device`),
    capacityKwh: number(field(r, 'capacityKwh', path), `${path}.capacityKwh`),
    socSignal: text(field(r, 'socSignal', path), `${path}.socSignal`),
    cheapZone: nullable(field(r, 'cheapZone', path), `${path}.cheapZone`, text),
    plannerDispatch: nullable(field(r, 'plannerDispatch', path), `${path}.plannerDispatch`, (v, p) => oneOf(EV_PLANNER_DISPATCH, v, p)),
    rangeSignal: nullable(field(r, 'rangeSignal', path), `${path}.rangeSignal`, text),
    pluggedSignal: nullable(field(r, 'pluggedSignal', path), `${path}.pluggedSignal`, text),
    pluggedStates: nullable(field(r, 'pluggedStates', path), `${path}.pluggedStates`, (v, p) => list(v, p, text)),
    calendarSignal: nullable(field(r, 'calendarSignal', path), `${path}.calendarSignal`, text),
  };
}

function parameters(raw: unknown, path: string): EvParameters {
  const r = object(raw, path);
  return {
    chargeNowTargetPercent: number(field(r, 'chargeNowTargetPercent', path), `${path}.chargeNowTargetPercent`),
    chargeNowDurationS: number(field(r, 'chargeNowDurationS', path), `${path}.chargeNowDurationS`),
  };
}

function issue(raw: unknown, path: string): EvIssue {
  const r = object(raw, path);
  return {
    code: text(field(r, 'code', path), `${path}.code`),
    path: text(field(r, 'path', path), `${path}.path`),
    message: text(field(r, 'message', path), `${path}.message`),
  };
}

function read(raw: unknown, path: string): EvRead {
  const r = object(raw, path);
  return {
    configured: bool(field(r, 'configured', path), `${path}.configured`),
    state: oneOf(SIGNAL_STATES, field(r, 'state', path), `${path}.state`),
    detail: oneOf(SIGNAL_DETAILS, field(r, 'detail', path), `${path}.detail`),
    readAt: nullable(field(r, 'readAt', path), `${path}.readAt`, instant),
    ageS: nullable(field(r, 'ageS', path), `${path}.ageS`, number),
    maxAgeS: nullable(field(r, 'maxAgeS', path), `${path}.maxAgeS`, number),
  };
}

function inputs(raw: unknown, path: string): EvInputs {
  const r = object(raw, path);
  return {
    soc: read(field(r, 'soc', path), `${path}.soc`),
    socPercent: nullable(field(r, 'socPercent', path), `${path}.socPercent`, number),
    range: read(field(r, 'range', path), `${path}.range`),
    rangeKm: nullable(field(r, 'rangeKm', path), `${path}.rangeKm`, number),
    plugged: read(field(r, 'plugged', path), `${path}.plugged`),
    isPlugged: nullable(field(r, 'isPlugged', path), `${path}.isPlugged`, bool),
    pluggedState: nullable(field(r, 'pluggedState', path), `${path}.pluggedState`, text),
    calendar: read(field(r, 'calendar', path), `${path}.calendar`),
  };
}

function trip(raw: unknown, path: string): EvTrip {
  const r = object(raw, path);
  return {
    title: text(field(r, 'title', path), `${path}.title`),
    start: instant(field(r, 'start', path), `${path}.start`),
    km: number(field(r, 'km', path), `${path}.km`),
  };
}

function trips(raw: unknown, path: string): EvTrips {
  const r = object(raw, path);
  return {
    nextTrip: nullable(field(r, 'nextTrip', path), `${path}.nextTrip`, trip),
    unparsed: list(field(r, 'unparsed', path), `${path}.unparsed`, (v, p) => {
      const u = object(v, p);
      return {
        title: text(field(u, 'title', p), `${p}.title`),
        start: instant(field(u, 'start', p), `${p}.start`),
        reason: oneOf(EV_UNPARSED_REASONS, field(u, 'reason', p), `${p}.reason`),
        km: nullable(field(u, 'km', p), `${p}.km`, number),
      };
    }),
    warnings: list(field(r, 'warnings', path), `${path}.warnings`, (v, p) => {
      const w = object(v, p);
      return {
        kind: oneOf(EV_TRIP_WARNINGS, field(w, 'kind', p), `${p}.kind`),
        title: text(field(w, 'title', p), `${p}.title`),
        start: instant(field(w, 'start', p), `${p}.start`),
      };
    }),
    calendarZone: nullable(field(r, 'calendarZone', path), `${path}.calendarZone`, text),
  };
}

function offeredGoal(raw: unknown, path: string): EvOfferedGoal {
  const r = object(raw, path);
  return {
    kind: oneOf(EV_GOAL_KINDS, field(r, 'kind', path), `${path}.kind`),
    targetPercent: number(field(r, 'targetPercent', path), `${path}.targetPercent`),
    remainingKwh: number(field(r, 'remainingKwh', path), `${path}.remainingKwh`),
    deadline: nullable(field(r, 'deadline', path), `${path}.deadline`, instant),
    hard: bool(field(r, 'hard', path), `${path}.hard`),
    sources: nullable(field(r, 'sources', path), `${path}.sources`, sources),
    spread: bool(field(r, 'spread', path), `${path}.spread`),
  };
}

function assessment(raw: unknown, path: string): EvAssessment {
  const r = object(raw, path);
  const result: EvAssessment = {
    failed: bool(field(r, 'failed', path), `${path}.failed`),
    reason: nullable(field(r, 'reason', path), `${path}.reason`, (v, p) => oneOf(EV_CAUSES, v, p)),
    notes: list(field(r, 'notes', path), `${path}.notes`, (v, p) => oneOf(EV_CAUSES, v, p)),
    computedAt: nullable(field(r, 'computedAt', path), `${path}.computedAt`, instant),
    validUntil: nullable(field(r, 'validUntil', path), `${path}.validUntil`, instant),
    expired: bool(field(r, 'expired', path), `${path}.expired`),
    mode: nullable(field(r, 'mode', path), `${path}.mode`, (v, p) => oneOf(EV_MODES, v, p)),
    socPercent: nullable(field(r, 'socPercent', path), `${path}.socPercent`, number),
    socGap: nullable(field(r, 'socGap', path), `${path}.socGap`, (v, p) => oneOf(EV_SOC_GAPS, v, p)),
    kwhPerKm: nullable(field(r, 'kwhPerKm', path), `${path}.kwhPerKm`, number),
    nextTrip: nullable(field(r, 'nextTrip', path), `${path}.nextTrip`, trip),
    lastFullAt: nullable(field(r, 'lastFullAt', path), `${path}.lastFullAt`, instant),
    goals: list(field(r, 'goals', path), `${path}.goals`, offeredGoal),
  };
  // A computation that threw offers nothing and has no reason; one that ran always has a reason.
  if (result.failed !== (result.reason === null)) fail(`${path}.reason`, 'present exactly when the computation did not fail');
  if (result.failed && result.goals.length > 0) fail(`${path}.goals`, 'empty for a failed computation');
  return result;
}

function manual(raw: unknown, path: string): EvManual {
  const r = object(raw, path);
  return {
    mode: oneOf(EV_MODES, field(r, 'mode', path), `${path}.mode`),
    chargeNow:
      nullable(field(r, 'chargeNow', path), `${path}.chargeNow`, (v, p) => {
        const c = object(v, p);
        return {
          active: bool(field(c, 'active', p), `${p}.active`),
          targetPct: nullable(field(c, 'targetPct', p), `${p}.targetPct`, number),
          until: nullable(field(c, 'until', p), `${p}.until`, instant),
        };
      }) ?? { active: false, targetPct: null, until: null },
  };
}

export function parseEv(raw: unknown): Ev {
  const path = 'ev';
  const r = object(raw, path);
  const ev: Ev = {
    at: instant(field(r, 'at', path), `${path}.at`),
    enabled: bool(field(r, 'enabled', path), `${path}.enabled`),
    config: nullable(field(r, 'config', path), `${path}.config`, config),
    parameters: nullable(field(r, 'parameters', path), `${path}.parameters`, parameters),
    issues: list(field(r, 'issues', path), `${path}.issues`, issue),
    addOnZone: nullable(field(r, 'addOnZone', path), `${path}.addOnZone`, text),
    inputs: nullable(field(r, 'inputs', path), `${path}.inputs`, inputs),
    trips: nullable(field(r, 'trips', path), `${path}.trips`, trips),
    assessment: nullable(field(r, 'assessment', path), `${path}.assessment`, assessment),
    manual: 'manual' in r ? nullable(r.manual, `${path}.manual`, manual) : null,
  };
  // The module is on exactly when its section is there; without it there is nothing to show beyond the issues.
  if (ev.enabled !== (ev.config !== null)) fail(`${path}.config`, 'present exactly when the module is enabled');
  if (!ev.enabled && (ev.inputs !== null || ev.trips !== null || ev.assessment !== null)) fail(`${path}.inputs`, 'null without an ev: section');
  return ev;
}

const EV_URL = 'api/ev';

export async function fetchEv(signal: AbortSignal): Promise<Ev> {
  const response = await fetch(EV_URL, { signal, cache: 'no-store', headers: { Accept: 'application/json' } });
  if (!response.ok) throw new Error(`EV request failed with HTTP ${response.status}.`);
  return parseEv(await response.json());
}

export class EvError extends Error {
  readonly refusal: EvRefusal;

  constructor(refusal: EvRefusal) {
    super(`EV request refused: ${refusal}.`);
    this.refusal = refusal;
  }
}

/** The refusal body's `code` when it is one the page words; without one (a proxy's answer, a body that is not JSON) the status is the best that can be said. */
async function refusalOf(response: Response): Promise<EvRefusal> {
  try {
    const body: unknown = await response.json();
    const code = typeof body === 'object' && body !== null ? (body as Record<string, unknown>).code : undefined;
    if ((EV_REFUSALS as readonly unknown[]).includes(code)) return code as EvRefusal;
  } catch {
    // not JSON: fall back to the status below
  }
  if (response.status === 400) return 'bad_request';
  if (response.status === 404) return 'not_configured';
  if (response.status === 409) return 'needs_full_dispatch';
  return 'failed';
}

async function send(url: string, method: 'POST' | 'DELETE', body: unknown, signal?: AbortSignal): Promise<void> {
  const response = await fetch(url, {
    method,
    signal,
    cache: 'no-store',
    headers: body === undefined ? { Accept: 'application/json' } : { Accept: 'application/json', 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) throw new EvError(await refusalOf(response));
}

/** POST api/ev/mode. Stored in ev.json and kept across a restart; never an entity write. */
export function postEvMode(mode: EvMode, signal?: AbortSignal): Promise<void> {
  return send(`${EV_URL}/mode`, 'POST', { mode }, signal);
}

export interface ChargeNowRequest {
  /** Percent; the module's own default (control.ev.charge_now) when absent. */
  targetPct?: number;
  durationMinutes?: number;
}

/** POST api/ev/charge-now: a manual hard goal. The add-on answers 409 unless planner.dispatch is full. */
export function postChargeNow(request: ChargeNowRequest, signal?: AbortSignal): Promise<void> {
  const body: Record<string, number> = {};
  if (request.targetPct !== undefined) body.targetPct = request.targetPct;
  if (request.durationMinutes !== undefined) body.durationMinutes = request.durationMinutes;
  return send(`${EV_URL}/charge-now`, 'POST', body, signal);
}

/** DELETE api/ev/charge-now. */
export function deleteChargeNow(signal?: AbortSignal): Promise<void> {
  return send(`${EV_URL}/charge-now`, 'DELETE', undefined, signal);
}
