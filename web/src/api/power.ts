import { DECISION_ORIGINS, REASON_CODES, TIER_REACTIONS, parseLevel } from './decisions';
import type { DecisionOrigin, Level, ReasonCode, TierReaction } from './decisions';
import { DISPATCH_INVALID_REASONS } from './dispatch';
import type { DispatchInvalidReason } from './dispatch';
import { FLOW_DIRECTIONS, PHASES } from './signals';
import type { FlowDirection, PhaseName } from './signals';
import { bool, fail, field, instant, list, nullable, number, object, oneOf, text } from './strict';

// Contract of GET api/power (src/Switchboard.Host/Web/PowerEndpoint.cs): the power picture of the
// last control step for the dashboard and the flow view. Every closed list mirrors a backend enum;
// PresentationSpaContractTests compares them, and compares the keys of power.sample.json with what
// the endpoint writes.

/** Why a power figure is not known; not_declared means nothing of the kind exists (counted as 0 in the balance). */
export const POWER_UNKNOWN_REASONS = [
  'configuration_invalid',
  'source_disconnected',
  'power_unmapped',
  'power_not_fresh',
  'generation_unknown',
  'device_power_unknown',
  'not_declared',
] as const;
export const GENERATION_NOTES = ['asleep_counted_as_zero'] as const;
/** base (the house's steady load), controlled (declared devices), foreign (a step above the baseline), export, unattributed. */
export const SEGMENT_KINDS = ['base', 'controlled', 'foreign', 'export', 'unattributed'] as const;
/** measured (its own draw), rated_model (a relay's rating while it reports on), unknown. */
export const POWER_SOURCES = ['measured', 'rated_model', 'unknown'] as const;
/** When the reaction to an over-reserve phase is due; by_boundary is an upper bound. */
export const REACTION_TIMINGS = ['now', 'next_sample', 'next_boundary', 'by_boundary', 'cannot_reduce_exporting'] as const;

export type PowerUnknownReason = (typeof POWER_UNKNOWN_REASONS)[number];
export type GenerationNote = (typeof GENERATION_NOTES)[number];
export type SegmentKind = (typeof SEGMENT_KINDS)[number];
export type PowerSource = (typeof POWER_SOURCES)[number];
export type ReactionTiming = (typeof REACTION_TIMINGS)[number];

/** One figure in W (import and consumption positive), or why it is not known; never both, never neither. */
export interface Quantity {
  w: number | null;
  unknown: PowerUnknownReason | null;
  /** The report time of its oldest measured input; null for a model value. */
  reportedAt: Date | null;
}

/**
 * Generation + net import = load = controlled + storage + uncontrolled. Uncontrolled (the house's
 * own consumption) is the only residual; modelExceedsLoad says it came out negative.
 */
export interface Balance {
  generation: Quantity;
  generationNote: GenerationNote | null;
  netImport: Quantity;
  load: Quantity;
  controlled: Quantity;
  unknownDevices: string[];
  storage: Quantity;
  uncontrolled: Quantity;
  modelExceedsLoad: boolean;
}

export interface BarSegment {
  kind: SegmentKind;
  /** The segment's width in A (scaled to fit the measured current). */
  a: number;
  /** The power it stands for, unscaled; null when unknown. */
  w: number | null;
}

export interface PhasePower {
  phase: PhaseName;
  /** Measured, import positive (cautious mode: the magnitude). */
  currentA: number | null;
  direction: FlowDirection | null;
  currentReportedAt: Date | null;
  reserveThresholdA: number;
  ratingA: number;
  continuousA: number;
  /** The fresh phase voltage, else the nominal one (voltageMeasured says which). */
  voltageV: number;
  voltageMeasured: boolean;
  balance: Balance;
  foreignStepW: number | null;
  foreignBaselineW: number | null;
  foreignDetectedAt: Date | null;
  bar: BarSegment[];
  /** Controlled and foreign did not fit under the measured current (local generation covers part of the load). */
  barScaled: boolean;
  /** The guard's own comparison: the signed estimate above the reserve threshold. */
  overReserve: boolean;
}

export interface DevicePhasePower {
  phase: PhaseName;
  powerW: number | null;
  source: PowerSource;
  reportedAt: Date | null;
}

export interface DevicePower {
  id: string;
  phases: DevicePhasePower[];
  totalW: number | null;
  source: PowerSource;
}

export interface PlannedChange {
  device: string;
  from: Level | null;
  to: Level;
  origin: DecisionOrigin | null;
  reason: ReasonCode | null;
  dueAt: Date | null;
}

export interface ReserveBanner {
  phase: PhaseName;
  /** Measured, import positive. */
  currentA: number;
  /**
   * The guard's estimate with the changes already decided; null without valid data. The add-on
   * raises a banner when either this or the measured import is over the threshold.
   */
  effectiveCurrentA: number | null;
  thresholdA: number;
  foreignStepW: number | null;
  foreignDetectedAt: Date | null;
  reaction: TierReaction | null;
  reductionRequested: boolean;
  timing: ReactionTiming;
  dueAt: Date | null;
  planned: PlannedChange[];
}

export interface Power {
  /** The signal clock now (data time in development replay): ages and the local hour are computed from it. */
  at: Date;
  /** When the last control step was evaluated; null before the first. */
  evaluatedAt: Date | null;
  cycleS: number;
  dataValid: boolean;
  invalidReason: DispatchInvalidReason | null;
  total: Balance;
  phases: PhasePower[];
  devices: DevicePower[];
  banners: ReserveBanner[];
}

// Relative on purpose: ingress serves the page under a per-session prefix.
const POWER_URL = 'api/power';

const phaseName = (raw: unknown, path: string) => oneOf(PHASES, raw, path);
const maybeNumber = (r: Record<string, unknown>, key: string, path: string) => nullable(field(r, key, path), `${path}.${key}`, number);
const maybeInstant = (r: Record<string, unknown>, key: string, path: string) => nullable(field(r, key, path), `${path}.${key}`, instant);

export function parseQuantity(raw: unknown, path: string): Quantity {
  const r = object(raw, path);
  const w = maybeNumber(r, 'w', path);
  const unknown = nullable(field(r, 'unknown', path), `${path}.unknown`, (v, p) => oneOf(POWER_UNKNOWN_REASONS, v, p));
  // A figure is a number or a reason, never both and never neither: an unknown must never render as 0.
  if ((w === null) === (unknown === null)) fail(path, 'a value or a reason, exactly one of them');
  return { w, unknown, reportedAt: maybeInstant(r, 'reportedAt', path) };
}

export function parseBalance(raw: unknown, path: string): Balance {
  const r = object(raw, path);
  const q = (key: string) => parseQuantity(field(r, key, path), `${path}.${key}`);
  return {
    generation: q('generation'),
    generationNote: nullable(field(r, 'generationNote', path), `${path}.generationNote`, (v, p) => oneOf(GENERATION_NOTES, v, p)),
    netImport: q('netImport'),
    load: q('load'),
    controlled: q('controlled'),
    unknownDevices: list(field(r, 'unknownDevices', path), `${path}.unknownDevices`, text),
    storage: q('storage'),
    uncontrolled: q('uncontrolled'),
    modelExceedsLoad: bool(field(r, 'modelExceedsLoad', path), `${path}.modelExceedsLoad`),
  };
}

function segment(raw: unknown, path: string): BarSegment {
  const r = object(raw, path);
  const a = number(field(r, 'a', path), `${path}.a`);
  if (a < 0) fail(`${path}.a`, 'a non-negative current');
  return { kind: oneOf(SEGMENT_KINDS, field(r, 'kind', path), `${path}.kind`), a, w: maybeNumber(r, 'w', path) };
}

function phase(raw: unknown, path: string): PhasePower {
  const r = object(raw, path);
  const n = (key: string) => number(field(r, key, path), `${path}.${key}`);
  return {
    phase: phaseName(field(r, 'phase', path), `${path}.phase`),
    currentA: maybeNumber(r, 'currentA', path),
    direction: nullable(field(r, 'direction', path), `${path}.direction`, (v, p) => oneOf(FLOW_DIRECTIONS, v, p)),
    currentReportedAt: maybeInstant(r, 'currentReportedAt', path),
    reserveThresholdA: n('reserveThresholdA'),
    ratingA: n('ratingA'),
    continuousA: n('continuousA'),
    voltageV: n('voltageV'),
    voltageMeasured: bool(field(r, 'voltageMeasured', path), `${path}.voltageMeasured`),
    balance: parseBalance(field(r, 'balance', path), `${path}.balance`),
    foreignStepW: maybeNumber(r, 'foreignStepW', path),
    foreignBaselineW: maybeNumber(r, 'foreignBaselineW', path),
    foreignDetectedAt: maybeInstant(r, 'foreignDetectedAt', path),
    bar: list(field(r, 'bar', path), `${path}.bar`, segment),
    barScaled: bool(field(r, 'barScaled', path), `${path}.barScaled`),
    overReserve: bool(field(r, 'overReserve', path), `${path}.overReserve`),
  };
}

function devicePhase(raw: unknown, path: string): DevicePhasePower {
  const r = object(raw, path);
  const powerW = maybeNumber(r, 'powerW', path);
  const source = oneOf(POWER_SOURCES, field(r, 'source', path), `${path}.source`);
  // A power is unknown exactly when its source is: a stale draw must never show a number.
  if ((powerW === null) !== (source === 'unknown')) fail(path, 'a power exactly when its source is known');
  return { phase: phaseName(field(r, 'phase', path), `${path}.phase`), powerW, source, reportedAt: maybeInstant(r, 'reportedAt', path) };
}

function device(raw: unknown, path: string): DevicePower {
  const r = object(raw, path);
  const totalW = maybeNumber(r, 'totalW', path);
  const source = oneOf(POWER_SOURCES, field(r, 'source', path), `${path}.source`);
  if ((totalW === null) !== (source === 'unknown')) fail(path, 'a total exactly when its source is known');
  return {
    id: text(field(r, 'id', path), `${path}.id`),
    phases: list(field(r, 'phases', path), `${path}.phases`, devicePhase),
    totalW,
    source,
  };
}

function planned(raw: unknown, path: string): PlannedChange {
  const r = object(raw, path);
  return {
    device: text(field(r, 'device', path), `${path}.device`),
    from: nullable(field(r, 'from', path), `${path}.from`, parseLevel),
    to: parseLevel(field(r, 'to', path), `${path}.to`),
    origin: nullable(field(r, 'origin', path), `${path}.origin`, (v, p) => oneOf(DECISION_ORIGINS, v, p)),
    reason: nullable(field(r, 'reason', path), `${path}.reason`, (v, p) => oneOf(REASON_CODES, v, p)),
    dueAt: maybeInstant(r, 'dueAt', path),
  };
}

function banner(raw: unknown, path: string): ReserveBanner {
  const r = object(raw, path);
  return {
    phase: phaseName(field(r, 'phase', path), `${path}.phase`),
    currentA: number(field(r, 'currentA', path), `${path}.currentA`),
    effectiveCurrentA: maybeNumber(r, 'effectiveCurrentA', path),
    thresholdA: number(field(r, 'thresholdA', path), `${path}.thresholdA`),
    foreignStepW: maybeNumber(r, 'foreignStepW', path),
    foreignDetectedAt: maybeInstant(r, 'foreignDetectedAt', path),
    reaction: nullable(field(r, 'reaction', path), `${path}.reaction`, (v, p) => oneOf(TIER_REACTIONS, v, p)),
    reductionRequested: bool(field(r, 'reductionRequested', path), `${path}.reductionRequested`),
    timing: oneOf(REACTION_TIMINGS, field(r, 'timing', path), `${path}.timing`),
    dueAt: maybeInstant(r, 'dueAt', path),
    planned: list(field(r, 'planned', path), `${path}.planned`, planned),
  };
}

/**
 * Accepts only the exact shape the add-on promises: every key present, every enum value from its
 * closed list, a figure either a number or a reason. Anything else is an error, so the dashboard
 * never shows a power the add-on did not report.
 */
export function parsePower(raw: unknown): Power {
  const r = object(raw, 'payload');
  const dataValid = bool(field(r, 'dataValid', 'payload'), 'dataValid');
  const invalidReason = nullable(field(r, 'invalidReason', 'payload'), 'invalidReason', (v, p) => oneOf(DISPATCH_INVALID_REASONS, v, p));
  if (dataValid !== (invalidReason === null)) fail('invalidReason', 'null exactly when the data is valid');
  return {
    at: instant(field(r, 'at', 'payload'), 'at'),
    evaluatedAt: nullable(field(r, 'evaluatedAt', 'payload'), 'evaluatedAt', instant),
    cycleS: number(field(r, 'cycleS', 'payload'), 'cycleS'),
    dataValid,
    invalidReason,
    total: parseBalance(field(r, 'total', 'payload'), 'total'),
    phases: list(field(r, 'phases', 'payload'), 'phases', phase),
    devices: list(field(r, 'devices', 'payload'), 'devices', device),
    banners: list(field(r, 'banners', 'payload'), 'banners', banner),
  };
}

export async function fetchPower(signal: AbortSignal): Promise<Power> {
  const response = await fetch(POWER_URL, { signal, cache: 'no-store', headers: { Accept: 'application/json' } });
  if (!response.ok) throw new Error(`Power request failed with HTTP ${response.status}.`);
  return parsePower(await response.json());
}
