import {
  DEVICE_STATE_INVALID_REASONS,
  KILL_SWITCH_SOURCES,
  MODE_NOTES,
  OPERATING_MODES,
  REASON_CODES,
  RELAY_STATES,
  TIER_REACTIONS,
  parseDecisions,
} from './decisions';
import type {
  Decision,
  DeviceStateInvalidReason,
  KillSwitchSource,
  ModeNote,
  OperatingMode,
  ReasonCode,
  RelayState,
  TierReaction,
} from './decisions';
import { CHARACTERISTICS, FLOW_DIRECTIONS, PHASES, PROTECTION_INVALID_REASONS } from './signals';
import type { Characteristic, FlowDirection, PhaseName, ProtectionInvalidReason } from './signals';
import { bool, count, fail, field, instant, list, nullable, number, object, oneOf, positive, text } from './strict';

// Contract of GET api/guard (src/Switchboard.Host/Web/GuardEndpoint.cs). Every closed list mirrors a
// backend enum; GuardSpaContractTests (tests/Switchboard.Tests) compares them, and compares the keys
// of guard.sample.json with what the endpoint writes.

export const SAFE_STATE_SCOPES = ['none', 'phases', 'all'] as const;
export const BUDGET_TRENDS = ['full', 'consuming', 'recovering', 'held'] as const;
export const PHASE_GUARD_STATES = ['unknown', 'normal', 'hold', 'reducing', 'shedding', 'safe_state'] as const;
export const DEVICE_TYPES = ['on_off', 'stepped', 'modulating', 'storage'] as const;
export const DEVICE_GUARD_STATES = ['allowed', 'reduced', 'shed', 'unsafe'] as const;
/** What protection holds a stage to. */
export const GUARD_TARGETS = ['off', 'at_minimum', 'released'] as const;
export const STATE_FILE_STATUSES = ['loaded', 'fresh', 'reset_corrupt'] as const;

export type SafeStateScope = (typeof SAFE_STATE_SCOPES)[number];
export type BudgetTrend = (typeof BUDGET_TRENDS)[number];
export type PhaseGuardState = (typeof PHASE_GUARD_STATES)[number];
export type DeviceType = (typeof DEVICE_TYPES)[number];
export type DeviceGuardState = (typeof DEVICE_GUARD_STATES)[number];
export type GuardTarget = (typeof GUARD_TARGETS)[number];
export type StateFileStatus = (typeof STATE_FILE_STATUSES)[number];

export interface Tier {
  /** Upper limit as a multiple of In, inclusive; null for the open top band. */
  upTo: number | null;
  upToA: number | null;
  /** The same limit as power per phase at the nominal voltage (display only); null for the open top band. */
  upToW: number | null;
  /** Allowed duration; null for unlimited. */
  allowedS: number | null;
  reaction: TierReaction;
}

export interface Breaker {
  ratingA: number;
  characteristic: Characteristic;
  continuousMultiple: number;
  continuousA: number;
  /** How long an empty overload budget takes to refill at or below the continuous current. */
  budgetRecoveryTimeS: number;
  tiers: Tier[];
}

export interface GuardPhase {
  phase: PhaseName;
  dataValid: boolean;
  invalidReason: ProtectionInvalidReason | null;
  /** Measured, import positive (cautious mode: the magnitude). */
  currentA: number | null;
  /** Measured minus the relief of limits already decided ("as if applied"). */
  effectiveCurrentA: number | null;
  multiple: number | null;
  /** Index into breaker.tiers of the measured multiple. */
  band: number | null;
  reaction: TierReaction | null;
  continuousA: number;
  reserveThresholdA: number;
  /** To the reserve threshold, from the measured current. */
  headroomA: number | null;
  headroomW: number | null;
  /** To the continuous current, from the measured current. */
  breakerHeadroomA: number | null;
  budget: number;
  budgetTrend: BudgetTrend;
  recoveryRemainingS: number;
  increasesAllowed: boolean;
  reductionRequested: boolean;
  state: PhaseGuardState;
  direction: FlowDirection | null;
  reportedAt: Date | null;
}

export interface GuardStage {
  stage: number;
  applied: RelayState | null;
  setpointA: number | null;
  fresh: boolean;
  invalidReason: DeviceStateInvalidReason | null;
  limit: GuardTarget | null;
  /** The last fresh applied state seen, kept across restarts; for display only. */
  lastKnown: { applied: RelayState | null; setpointA: number | null; at: Date } | null;
}

export interface GuardDevice {
  id: string;
  type: DeviceType;
  phases: PhaseName[];
  guardState: DeviceGuardState;
  reason: ReasonCode | null;
  stages: GuardStage[];
  /** Position in the shed order (1 sheds first); null for a no_shed device. */
  shedRank: number | null;
  valuePerKwh: number;
  shedPriority: number | null;
  noShed: boolean;
  canSwitchOff: boolean;
  minOnRemainingS: number;
  minOffRemainingS: number;
  /** Logical names of the signals that make this device unsafe (SIG-05). */
  unsafeSignals: string[];
}

export interface GuardStatus {
  at: Date;
  mode: { ceiling: OperatingMode; uiChoice: OperatingMode; effective: OperatingMode; note: ModeNote };
  safeState: { active: boolean; scope: SafeStateScope; reason: ReasonCode | null; phases: PhaseName[]; since: Date | null };
  /**
   * locked: engaged by an untrusted state.json at start and held until the user confirms (api/control/confirm).
   * switchesDevices: whether an engaged kill switch switches anything now (effective active mode); in
   * shadow nothing is switched and the decision log says what would be.
   */
  killSwitch: { engaged: boolean; changedAt: Date | null; source: KillSwitchSource | null; locked: boolean; switchesDevices: boolean };
  breaker: Breaker | null;
  phases: GuardPhase[];
  devices: GuardDevice[];
  decisionLog: { writable: boolean; lastError: string | null; dropped: number };
  stateFile: { status: StateFileStatus; warning: string | null };
  /** The most recent decisions, newest first. */
  decisions: Decision[];
}

// Relative on purpose: ingress serves the page under a per-session prefix.
const GUARD_URL = 'api/guard';

const phaseName = (raw: unknown, path: string) => oneOf(PHASES, raw, path);
const reasonCode = (raw: unknown, path: string) => oneOf(REASON_CODES, raw, path);
const relayState = (raw: unknown, path: string) => oneOf(RELAY_STATES, raw, path);

function tier(raw: unknown, path: string): Tier {
  const r = object(raw, path);
  const n = (key: string) => nullable(field(r, key, path), `${path}.${key}`, number);
  return {
    upTo: n('upTo'),
    upToA: n('upToA'),
    upToW: n('upToW'),
    allowedS: n('allowedS'),
    reaction: oneOf(TIER_REACTIONS, field(r, 'reaction', path), `${path}.reaction`),
  };
}

function breaker(raw: unknown, path: string): Breaker {
  const r = object(raw, path);
  const tiers = list(field(r, 'tiers', path), `${path}.tiers`, tier);
  // Only the last band is open: a missing limit anywhere else would hide a band's range.
  tiers.forEach((t, i) => {
    if ((t.upTo === null) !== (i === tiers.length - 1)) fail(`${path}.tiers[${i}].upTo`, 'null for the last band only');
  });
  return {
    ratingA: number(field(r, 'ratingA', path), `${path}.ratingA`),
    characteristic: oneOf(CHARACTERISTICS, field(r, 'characteristic', path), `${path}.characteristic`),
    continuousMultiple: number(field(r, 'continuousMultiple', path), `${path}.continuousMultiple`),
    continuousA: number(field(r, 'continuousA', path), `${path}.continuousA`),
    budgetRecoveryTimeS: number(field(r, 'budgetRecoveryTimeS', path), `${path}.budgetRecoveryTimeS`),
    tiers,
  };
}

function phase(raw: unknown, path: string, bands: number | null): GuardPhase {
  const r = object(raw, path);
  const n = (key: string) => nullable(field(r, key, path), `${path}.${key}`, number);
  const dataValid = bool(field(r, 'dataValid', path), `${path}.dataValid`);
  const invalidReason = nullable(field(r, 'invalidReason', path), `${path}.invalidReason`, (v, p) =>
    oneOf(PROTECTION_INVALID_REASONS, v, p),
  );
  const currentA = n('currentA');
  const band = nullable(field(r, 'band', path), `${path}.band`, count);
  // Valid data always has a current and never a reason: a "valid" next to a reason, or a valid
  // phase without a number, must never be rendered.
  if (dataValid && (invalidReason !== null || currentA === null)) fail(path, 'valid data with a current and no reason');
  if (band !== null && (bands === null || band >= bands)) fail(`${path}.band`, 'a band of the breaker profile');
  return {
    phase: phaseName(field(r, 'phase', path), `${path}.phase`),
    dataValid,
    invalidReason,
    currentA,
    effectiveCurrentA: n('effectiveCurrentA'),
    multiple: n('multiple'),
    band,
    reaction: nullable(field(r, 'reaction', path), `${path}.reaction`, (v, p) => oneOf(TIER_REACTIONS, v, p)),
    continuousA: number(field(r, 'continuousA', path), `${path}.continuousA`),
    reserveThresholdA: number(field(r, 'reserveThresholdA', path), `${path}.reserveThresholdA`),
    headroomA: n('headroomA'),
    headroomW: n('headroomW'),
    breakerHeadroomA: n('breakerHeadroomA'),
    budget: number(field(r, 'budget', path), `${path}.budget`),
    budgetTrend: oneOf(BUDGET_TRENDS, field(r, 'budgetTrend', path), `${path}.budgetTrend`),
    recoveryRemainingS: number(field(r, 'recoveryRemainingS', path), `${path}.recoveryRemainingS`),
    increasesAllowed: bool(field(r, 'increasesAllowed', path), `${path}.increasesAllowed`),
    reductionRequested: bool(field(r, 'reductionRequested', path), `${path}.reductionRequested`),
    state: oneOf(PHASE_GUARD_STATES, field(r, 'state', path), `${path}.state`),
    direction: nullable(field(r, 'direction', path), `${path}.direction`, (v, p) => oneOf(FLOW_DIRECTIONS, v, p)),
    reportedAt: nullable(field(r, 'reportedAt', path), `${path}.reportedAt`, instant),
  };
}

function lastKnown(raw: unknown, path: string): GuardStage['lastKnown'] {
  const r = object(raw, path);
  return {
    applied: nullable(field(r, 'applied', path), `${path}.applied`, relayState),
    setpointA: nullable(field(r, 'setpointA', path), `${path}.setpointA`, number),
    at: instant(field(r, 'at', path), `${path}.at`),
  };
}

function stage(raw: unknown, path: string): GuardStage {
  const r = object(raw, path);
  const fresh = bool(field(r, 'fresh', path), `${path}.fresh`);
  const applied = nullable(field(r, 'applied', path), `${path}.applied`, relayState);
  const setpointA = nullable(field(r, 'setpointA', path), `${path}.setpointA`, number);
  const invalidReason = nullable(field(r, 'invalidReason', path), `${path}.invalidReason`, (v, p) =>
    oneOf(DEVICE_STATE_INVALID_REASONS, v, p),
  );
  // A fresh stage reports exactly one applied state and no reason; a stage that is not fresh has a
  // reason and no applied state (a stale "off" must never look current).
  if (fresh !== (invalidReason === null)) fail(`${path}.invalidReason`, 'consistent with fresh');
  const reported = (applied === null ? 0 : 1) + (setpointA === null ? 0 : 1);
  if (reported !== (fresh ? 1 : 0)) fail(path, 'one applied state when fresh, none otherwise');
  return {
    stage: positive(field(r, 'stage', path), `${path}.stage`),
    applied,
    setpointA,
    fresh,
    invalidReason,
    limit: nullable(field(r, 'limit', path), `${path}.limit`, (v, p) => oneOf(GUARD_TARGETS, v, p)),
    lastKnown: nullable(field(r, 'lastKnown', path), `${path}.lastKnown`, lastKnown),
  };
}

function device(raw: unknown, path: string): GuardDevice {
  const r = object(raw, path);
  const n = (key: string) => number(field(r, key, path), `${path}.${key}`);
  return {
    id: text(field(r, 'id', path), `${path}.id`),
    type: oneOf(DEVICE_TYPES, field(r, 'type', path), `${path}.type`),
    phases: list(field(r, 'phases', path), `${path}.phases`, phaseName),
    guardState: oneOf(DEVICE_GUARD_STATES, field(r, 'guardState', path), `${path}.guardState`),
    reason: nullable(field(r, 'reason', path), `${path}.reason`, reasonCode),
    stages: list(field(r, 'stages', path), `${path}.stages`, stage),
    shedRank: nullable(field(r, 'shedRank', path), `${path}.shedRank`, positive),
    valuePerKwh: n('valuePerKwh'),
    shedPriority: nullable(field(r, 'shedPriority', path), `${path}.shedPriority`, number),
    noShed: bool(field(r, 'noShed', path), `${path}.noShed`),
    canSwitchOff: bool(field(r, 'canSwitchOff', path), `${path}.canSwitchOff`),
    minOnRemainingS: n('minOnRemainingS'),
    minOffRemainingS: n('minOffRemainingS'),
    unsafeSignals: list(field(r, 'unsafeSignals', path), `${path}.unsafeSignals`, text),
  };
}

/**
 * Accepts only the exact shape the add-on promises: every key present, every enum value from its
 * closed list, the invariants the add-on guarantees. Anything else is an error rather than a guess,
 * so the page never shows a limit, a budget or a safe state the add-on did not report.
 */
export function parseGuard(raw: unknown): GuardStatus {
  const r = object(raw, 'payload');
  const mode = object(field(r, 'mode', 'payload'), 'mode');
  const safe = object(field(r, 'safeState', 'payload'), 'safeState');
  const kill = object(field(r, 'killSwitch', 'payload'), 'killSwitch');
  const log = object(field(r, 'decisionLog', 'payload'), 'decisionLog');
  const stateFile = object(field(r, 'stateFile', 'payload'), 'stateFile');
  const profile = nullable(field(r, 'breaker', 'payload'), 'breaker', breaker);
  const bands = profile === null ? null : profile.tiers.length;
  const safeActive = bool(field(safe, 'active', 'safeState'), 'safeState.active');
  const safeScope = oneOf(SAFE_STATE_SCOPES, field(safe, 'scope', 'safeState'), 'safeState.scope');
  // Safe state is active exactly when it has a scope; an "inactive" banner with a scope is a contract break.
  if (safeActive !== (safeScope !== 'none')) fail('safeState.scope', 'consistent with safeState.active');
  return {
    at: instant(field(r, 'at', 'payload'), 'at'),
    mode: {
      ceiling: oneOf(OPERATING_MODES, field(mode, 'ceiling', 'mode'), 'mode.ceiling'),
      uiChoice: oneOf(OPERATING_MODES, field(mode, 'uiChoice', 'mode'), 'mode.uiChoice'),
      effective: oneOf(OPERATING_MODES, field(mode, 'effective', 'mode'), 'mode.effective'),
      note: oneOf(MODE_NOTES, field(mode, 'note', 'mode'), 'mode.note'),
    },
    safeState: {
      active: safeActive,
      scope: safeScope,
      reason: nullable(field(safe, 'reason', 'safeState'), 'safeState.reason', reasonCode),
      phases: list(field(safe, 'phases', 'safeState'), 'safeState.phases', phaseName),
      since: nullable(field(safe, 'since', 'safeState'), 'safeState.since', instant),
    },
    killSwitch: {
      engaged: bool(field(kill, 'engaged', 'killSwitch'), 'killSwitch.engaged'),
      changedAt: nullable(field(kill, 'changedAt', 'killSwitch'), 'killSwitch.changedAt', instant),
      source: nullable(field(kill, 'source', 'killSwitch'), 'killSwitch.source', (v, p) =>
        oneOf(KILL_SWITCH_SOURCES, v, p),
      ),
      locked: bool(field(kill, 'locked', 'killSwitch'), 'killSwitch.locked'),
      switchesDevices: bool(field(kill, 'switchesDevices', 'killSwitch'), 'killSwitch.switchesDevices'),
    },
    breaker: profile,
    phases: list(field(r, 'phases', 'payload'), 'phases', (p, path) => phase(p, path, bands)),
    devices: list(field(r, 'devices', 'payload'), 'devices', device),
    decisionLog: {
      writable: bool(field(log, 'writable', 'decisionLog'), 'decisionLog.writable'),
      lastError: nullable(field(log, 'lastError', 'decisionLog'), 'decisionLog.lastError', text),
      dropped: count(field(log, 'dropped', 'decisionLog'), 'decisionLog.dropped'),
    },
    stateFile: {
      status: oneOf(STATE_FILE_STATUSES, field(stateFile, 'status', 'stateFile'), 'stateFile.status'),
      warning: nullable(field(stateFile, 'warning', 'stateFile'), 'stateFile.warning', text),
    },
    decisions: parseDecisions(field(r, 'decisions', 'payload'), 'decisions'),
  };
}

export async function fetchGuard(signal: AbortSignal): Promise<GuardStatus> {
  const response = await fetch(GUARD_URL, {
    signal,
    cache: 'no-store',
    headers: { Accept: 'application/json' },
  });
  if (!response.ok) {
    throw new Error(`Guard request failed with HTTP ${response.status}.`);
  }
  return parseGuard(await response.json());
}
