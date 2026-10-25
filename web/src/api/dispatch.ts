import {
  DECISION_ORIGINS,
  MEASURE_UNITS,
  MODE_NOTES,
  OPERATING_MODES,
  OVERRIDE_MODES,
  REASON_CODES,
  VERDICTS,
  parseLevel,
} from './decisions';
import type {
  DecisionOrigin,
  Level,
  MeasureUnit,
  ModeNote,
  OperatingMode,
  OverrideMode,
  ReasonCode,
  Verdict,
} from './decisions';
import { START_POLICIES } from './devices';
import type { StartPolicy } from './devices';
import { DEVICE_TYPES } from './guard';
import type { DeviceType } from './guard';
import { PHASES } from './signals';
import type { PhaseName } from './signals';
import { bool, count, fail, field, instant, list, nullable, number, object, oneOf, positive, text } from './strict';

// Contract of GET api/dispatch and of the answer to POST api/devices/{id}/override
// (src/Switchboard.Host/Web/DispatchEndpoint.cs). Every closed list mirrors a backend enum;
// DispatchSpaContractTests (tests/Switchboard.Tests) compares them, and compares the keys of
// dispatch.sample.json with what the endpoint writes.

/** A device's settling window: none, open, or closed by its hard timeout (DISP-05). */
export const SETTLING_STATUSES = ['none', 'open', 'timed_out'] as const;
/** A history entry is a change of ours (command) or another controller's switching (observed). */
export const HISTORY_KINDS = ['command', 'observed'] as const;
/** Who set an override; restored means it came back from state.json after a restart. */
export const OVERRIDE_SOURCES = ['api', 'mqtt', 'restored'] as const;
/** Why dispatch has no valid power picture of a phase. */
export const DISPATCH_INVALID_REASONS = [
  'configuration_invalid',
  'source_disconnected',
  'power_unmapped',
  'power_not_fresh',
  'generation_unknown',
] as const;

export type SettlingStatus = (typeof SETTLING_STATUSES)[number];
export type HistoryKind = (typeof HISTORY_KINDS)[number];
export type OverrideSource = (typeof OVERRIDE_SOURCES)[number];
export type DispatchInvalidReason = (typeof DISPATCH_INVALID_REASONS)[number];

export interface DispatchGroup {
  group: string;
  envelopeA: number | null;
  inFlightA: number;
  increasesAllowed: boolean;
}

export interface DispatchPhase {
  phase: PhaseName;
  /** E: what dispatch may still add on the phase; null without valid data. */
  envelopeA: number | null;
  /** The in-flight changes counted in the estimate. */
  inFlightA: number;
  dataValid: boolean;
  invalidReason: DispatchInvalidReason | null;
  /** House load not explained by our devices (04 §4); null when unknown. */
  foreignW: number | null;
  foreignBaselineW: number | null;
  /** The adopted phase priority for rotated elements (1 is started first); null before one is adopted. */
  rank: number | null;
  groups: DispatchGroup[];
}

export interface Deferred {
  target: Level;
  reason: ReasonCode;
  refusedBy: ReasonCode;
  since: Date;
  /** When the lockout expires; null for a forced change re-checked every cycle. */
  dueAt: Date | null;
}

export interface DeviceOverride {
  mode: OverrideMode;
  changedAt: Date | null;
  source: OverrideSource | null;
}

export interface HistoryEntry {
  at: Date;
  kind: HistoryKind;
  expected: number;
  measured: number;
  unit: MeasureUnit;
  verdict: Verdict;
  /** When the change was decided (shadow: not applied) or the switching was seen; null for records from before 0.5.0. */
  commandedAt: Date | null;
  /** The level the change started from. */
  from: Level | null;
  /** The level it led to. */
  to: Level | null;
}

export interface DispatchDevice {
  id: string;
  type: DeviceType;
  phases: PhaseName[];
  rotationGroup: string | null;
  circuitGroup: string | null;
  /** What Switchboard would have the device at; null while it simply follows the reported state. */
  intended: Level | null;
  /** What Home Assistant says it is at; null while its state is not fresh. */
  reported: Level | null;
  reportedFresh: boolean;
  matches: boolean;
  reason: ReasonCode | null;
  origin: DecisionOrigin | null;
  startRank: number | null;
  /** An increase dispatch wants but has not made yet, and why. */
  pending: ReasonCode | null;
  lockout: { minOnRemainingS: number; minOffRemainingS: number };
  deferred: Deferred | null;
  settling: SettlingStatus;
  unresponsive: { since: Date; retryAt: Date | null } | null;
  mismatches: number;
  override: DeviceOverride;
  draw: { currentA: number | null; powerW: number | null; fresh: boolean } | null;
  /** Verifications and observations, newest first. */
  history: HistoryEntry[];
  start: StartPolicy;
  /**
   * What restore_only brings the device back to, and since when Switchboard holds it lower;
   * commanded is false for a shadow ("would") baseline. Kept while control is off: "left reduced".
   */
  baseline: { level: Level; since: Date; commanded: boolean } | null;
  /** Home Assistant acknowledged a command the reported state has not shown since then. */
  commandWarning: { since: Date } | null;
  /** When Switchboard started the device from off (restore_only); it switches it off again when it leaves the configuration. */
  selfStarted: Date | null;
}

export interface DispatchStatus {
  at: Date;
  mode: { ceiling: OperatingMode; uiChoice: OperatingMode; effective: OperatingMode; note: ModeNote };
  /** True only while commands are sent: effective active mode (option active, control allowed, configuration valid, state confirmed). */
  commandPath: boolean;
  phases: DispatchPhase[];
  devices: DispatchDevice[];
}

// Relative on purpose: ingress serves the page under a per-session prefix.
const DISPATCH_URL = 'api/dispatch';

const phaseName = (raw: unknown, path: string) => oneOf(PHASES, raw, path);
const reasonCode = (raw: unknown, path: string) => oneOf(REASON_CODES, raw, path);

function group(raw: unknown, path: string): DispatchGroup {
  const r = object(raw, path);
  return {
    group: text(field(r, 'group', path), `${path}.group`),
    envelopeA: nullable(field(r, 'envelopeA', path), `${path}.envelopeA`, number),
    inFlightA: number(field(r, 'inFlightA', path), `${path}.inFlightA`),
    increasesAllowed: bool(field(r, 'increasesAllowed', path), `${path}.increasesAllowed`),
  };
}

function phase(raw: unknown, path: string): DispatchPhase {
  const r = object(raw, path);
  const dataValid = bool(field(r, 'dataValid', path), `${path}.dataValid`);
  const invalidReason = nullable(field(r, 'invalidReason', path), `${path}.invalidReason`, (v, p) =>
    oneOf(DISPATCH_INVALID_REASONS, v, p),
  );
  // Valid data never names a reason: a "valid" next to a reason must never be rendered.
  if (dataValid && invalidReason !== null) fail(`${path}.invalidReason`, 'null for valid data');
  return {
    phase: phaseName(field(r, 'phase', path), `${path}.phase`),
    envelopeA: nullable(field(r, 'envelopeA', path), `${path}.envelopeA`, number),
    inFlightA: number(field(r, 'inFlightA', path), `${path}.inFlightA`),
    dataValid,
    invalidReason,
    foreignW: nullable(field(r, 'foreignW', path), `${path}.foreignW`, number),
    foreignBaselineW: nullable(field(r, 'foreignBaselineW', path), `${path}.foreignBaselineW`, number),
    rank: nullable(field(r, 'rank', path), `${path}.rank`, positive),
    groups: list(field(r, 'groups', path), `${path}.groups`, group),
  };
}

function deferred(raw: unknown, path: string): Deferred {
  const r = object(raw, path);
  return {
    target: parseLevel(field(r, 'target', path), `${path}.target`),
    reason: reasonCode(field(r, 'reason', path), `${path}.reason`),
    refusedBy: reasonCode(field(r, 'refusedBy', path), `${path}.refusedBy`),
    since: instant(field(r, 'since', path), `${path}.since`),
    dueAt: nullable(field(r, 'dueAt', path), `${path}.dueAt`, instant),
  };
}

function override(raw: unknown, path: string): DeviceOverride {
  const r = object(raw, path);
  const mode = oneOf(OVERRIDE_MODES, field(r, 'mode', path), `${path}.mode`);
  const changedAt = nullable(field(r, 'changedAt', path), `${path}.changedAt`, instant);
  const source = nullable(field(r, 'source', path), `${path}.source`, (v, p) => oneOf(OVERRIDE_SOURCES, v, p));
  // Auto is "no override": it has no time and no source; a forced mode always has both.
  const neither = changedAt === null && source === null;
  const both = changedAt !== null && source !== null;
  if (mode === 'auto' ? !neither : !both) fail(path, 'a time and a source exactly when forced');
  return { mode, changedAt, source };
}

function historyEntry(raw: unknown, path: string): HistoryEntry {
  const r = object(raw, path);
  return {
    at: instant(field(r, 'at', path), `${path}.at`),
    kind: oneOf(HISTORY_KINDS, field(r, 'kind', path), `${path}.kind`),
    expected: number(field(r, 'expected', path), `${path}.expected`),
    measured: number(field(r, 'measured', path), `${path}.measured`),
    unit: oneOf(MEASURE_UNITS, field(r, 'unit', path), `${path}.unit`),
    verdict: oneOf(VERDICTS, field(r, 'verdict', path), `${path}.verdict`),
    commandedAt: nullable(field(r, 'commandedAt', path), `${path}.commandedAt`, instant),
    from: nullable(field(r, 'from', path), `${path}.from`, parseLevel),
    to: nullable(field(r, 'to', path), `${path}.to`, parseLevel),
  };
}

function device(raw: unknown, path: string): DispatchDevice {
  const r = object(raw, path);
  const lockout = object(field(r, 'lockout', path), `${path}.lockout`);
  const intended = nullable(field(r, 'intended', path), `${path}.intended`, parseLevel);
  const reported = nullable(field(r, 'reported', path), `${path}.reported`, parseLevel);
  const reportedFresh = bool(field(r, 'reportedFresh', path), `${path}.reportedFresh`);
  const matches = bool(field(r, 'matches', path), `${path}.matches`);
  // A reported state is shown only while it is fresh: a stale "off" must never look current. An
  // unset intention follows the reported state, so it always matches.
  if (reportedFresh !== (reported !== null)) fail(`${path}.reported`, 'present exactly when fresh');
  if (intended === null && !matches) fail(`${path}.matches`, 'true while nothing is intended');
  const unresponsive = nullable(field(r, 'unresponsive', path), `${path}.unresponsive`, (v, p) => {
    const u = object(v, p);
    return { since: instant(field(u, 'since', p), `${p}.since`), retryAt: nullable(field(u, 'retryAt', p), `${p}.retryAt`, instant) };
  });
  const draw = nullable(field(r, 'draw', path), `${path}.draw`, (v, p) => {
    const d = object(v, p);
    return {
      currentA: nullable(field(d, 'currentA', p), `${p}.currentA`, number),
      powerW: nullable(field(d, 'powerW', p), `${p}.powerW`, number),
      fresh: bool(field(d, 'fresh', p), `${p}.fresh`),
    };
  });
  return {
    id: text(field(r, 'id', path), `${path}.id`),
    type: oneOf(DEVICE_TYPES, field(r, 'type', path), `${path}.type`),
    phases: list(field(r, 'phases', path), `${path}.phases`, phaseName),
    rotationGroup: nullable(field(r, 'rotationGroup', path), `${path}.rotationGroup`, text),
    circuitGroup: nullable(field(r, 'circuitGroup', path), `${path}.circuitGroup`, text),
    intended,
    reported,
    reportedFresh,
    matches,
    reason: nullable(field(r, 'reason', path), `${path}.reason`, reasonCode),
    origin: nullable(field(r, 'origin', path), `${path}.origin`, (v, p) => oneOf(DECISION_ORIGINS, v, p)),
    startRank: nullable(field(r, 'startRank', path), `${path}.startRank`, positive),
    pending: nullable(field(r, 'pending', path), `${path}.pending`, reasonCode),
    lockout: {
      minOnRemainingS: number(field(lockout, 'minOnRemainingS', `${path}.lockout`), `${path}.lockout.minOnRemainingS`),
      minOffRemainingS: number(field(lockout, 'minOffRemainingS', `${path}.lockout`), `${path}.lockout.minOffRemainingS`),
    },
    deferred: nullable(field(r, 'deferred', path), `${path}.deferred`, deferred),
    settling: oneOf(SETTLING_STATUSES, field(r, 'settling', path), `${path}.settling`),
    unresponsive,
    mismatches: count(field(r, 'mismatches', path), `${path}.mismatches`),
    override: override(field(r, 'override', path), `${path}.override`),
    draw,
    history: list(field(r, 'history', path), `${path}.history`, historyEntry),
    start: oneOf(START_POLICIES, field(r, 'start', path), `${path}.start`),
    baseline: nullable(field(r, 'baseline', path), `${path}.baseline`, (v, p) => {
      const b = object(v, p);
      return {
        level: parseLevel(field(b, 'level', p), `${p}.level`),
        since: instant(field(b, 'since', p), `${p}.since`),
        commanded: bool(field(b, 'commanded', p), `${p}.commanded`),
      };
    }),
    commandWarning: nullable(field(r, 'commandWarning', path), `${path}.commandWarning`, (v, p) => ({
      since: instant(field(object(v, p), 'since', p), `${p}.since`),
    })),
    selfStarted: nullable(field(r, 'selfStarted', path), `${path}.selfStarted`, instant),
  };
}

/**
 * Accepts only the exact shape the add-on promises: every key present, every enum value from its
 * closed list, the invariants the add-on guarantees. Anything else is an error, so the page never
 * shows an intention or an override the add-on did not report.
 */
export function parseDispatch(raw: unknown): DispatchStatus {
  const r = object(raw, 'payload');
  const mode = object(field(r, 'mode', 'payload'), 'mode');
  return {
    at: instant(field(r, 'at', 'payload'), 'at'),
    mode: {
      ceiling: oneOf(OPERATING_MODES, field(mode, 'ceiling', 'mode'), 'mode.ceiling'),
      uiChoice: oneOf(OPERATING_MODES, field(mode, 'uiChoice', 'mode'), 'mode.uiChoice'),
      effective: oneOf(OPERATING_MODES, field(mode, 'effective', 'mode'), 'mode.effective'),
      note: oneOf(MODE_NOTES, field(mode, 'note', 'mode'), 'mode.note'),
    },
    commandPath: bool(field(r, 'commandPath', 'payload'), 'commandPath'),
    phases: list(field(r, 'phases', 'payload'), 'phases', phase),
    devices: list(field(r, 'devices', 'payload'), 'devices', device),
  };
}

export async function fetchDispatch(signal: AbortSignal): Promise<DispatchStatus> {
  const response = await fetch(DISPATCH_URL, {
    signal,
    cache: 'no-store',
    headers: { Accept: 'application/json' },
  });
  if (!response.ok) {
    throw new Error(`Dispatch request failed with HTTP ${response.status}.`);
  }
  return parseDispatch(await response.json());
}

/** Why an override was not accepted, from the HTTP status the add-on answers with. */
export type OverrideRefusal = 'unknown_device' | 'no_configuration' | 'not_evaluated' | 'failed';

export class OverrideError extends Error {
  constructor(readonly refusal: OverrideRefusal) {
    super(`Override refused: ${refusal}.`);
  }
}

/**
 * SAFE-04 from the page. A JSON body, as the add-on requires (a cross-site form cannot send one
 * without a CORS preflight). The answer is the dispatch status after the guard runner evaluated
 * the change; 404 is an undeclared device, 409 no valid configuration, 503 an evaluation that
 * came late (the override holds in memory either way).
 */
export async function postOverride(device: string, mode: OverrideMode, signal: AbortSignal): Promise<DispatchStatus> {
  const response = await fetch(`api/devices/${encodeURIComponent(device)}/override`, {
    method: 'POST',
    signal,
    cache: 'no-store',
    headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
    body: JSON.stringify({ mode }),
  });
  if (!response.ok) {
    const refusal: OverrideRefusal =
      response.status === 404
        ? 'unknown_device'
        : response.status === 409
          ? 'no_configuration'
          : response.status === 503
            ? 'not_evaluated'
            : 'failed';
    throw new OverrideError(refusal);
  }
  return parseDispatch(await response.json());
}
