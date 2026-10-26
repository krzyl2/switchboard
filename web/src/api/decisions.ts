import { FLOW_DIRECTIONS, PHASES, PROTECTION_INVALID_REASONS } from './signals';
import type { FlowDirection, PhaseName, ProtectionInvalidReason } from './signals';
import { bool, count, fail, field, instant, list, nullable, number, object, oneOf, positive, text } from './strict';

// Contract of a decision as GET api/decisions and GET api/guard write it
// (src/Switchboard.Host/Web/DecisionsEndpoint.cs), decision log format 2 (reason codes 2 or 3). Every closed list mirrors a backend enum;
// GuardSpaContractTests (tests/Switchboard.Tests) compares them, so a value added on one side only
// fails the build instead of blanking the page.

/** The closed reason-code list, version 4 (04-ALGORITHMS §6); version 3 appended foreign_load and change_limit, version 4 the three balance codes. */
export const REASON_CODES = [
  'guard_tier_exceeded',
  'guard_budget_low',
  'guard_stale_signal',
  'reserve_threshold',
  'headroom_insufficient',
  'lockout_min_on',
  'lockout_min_off',
  'settling_window_open',
  'awaiting_confirmation',
  'value_rank_below_cutoff',
  'goal_met',
  'guard_expression_false',
  'device_unresponsive',
  'manual_override',
  'mode_shadow',
  'safe_state',
  'plan_slot_target',
  'headroom_available',
  'kill_switch',
  'foreign_load',
  'change_limit',
  'balance_projection_low',
  'balance_surplus',
  'balance_unknown',
] as const;
/** A group decision is about a circuit group (CONN-03): its own safe state, reserve request or budget. */
export const DECISION_SCOPES = ['system', 'phase', 'device', 'group'] as const;
export const DECISION_ACTIONS = [
  'switch_off',
  'reduce_to_minimum',
  'release',
  'request_reduction',
  'reduction_request_cleared',
  'stop_increases',
  'increases_allowed',
  'safe_state_entered',
  'safe_state_exited',
  'kill_switch_engaged',
  'kill_switch_disengaged',
  'mode_resolved',
  'cannot_reduce_exporting',
  'switch_on',
  'set_setpoint',
  'deferred',
  'override_set',
  'marked_unresponsive',
  'unresponsive_cleared',
  'settling_timed_out',
  'foreign_load_detected',
] as const;
/** What a decision is about, for filtering the decision log (backend DecisionKinds.Of: action first, then origin). */
export const DECISION_KINDS = ['protection', 'dispatch', 'override', 'verification', 'foreign_load', 'system'] as const;
export const OPERATING_MODES = ['shadow', 'active'] as const;
/** active_unavailable_before_stage3 is written by 0.3.x only; its lines stay readable for the log's retention. */
export const MODE_NOTES = ['none', 'active_unavailable_before_stage3', 'active_not_enabled'] as const;
/** corrupt_state: engaged at start by an untrusted state.json, held until confirmed; confirmation: released by that confirmation. */
export const KILL_SWITCH_SOURCES = ['api', 'mqtt', 'restored', 'corrupt_state', 'confirmation'] as const;
/** What keeps the effective mode in shadow (MODE-02), in the backend's order. */
export const MODE_CONDITIONS = ['option_shadow', 'control_off', 'configuration_invalid', 'state_unconfirmed'] as const;
/** Who set the global control switch last; default: never set (fresh install or upgraded from 0.6.x). */
export const CONTROL_SWITCH_SOURCES = ['default', 'api', 'mqtt', 'restored'] as const;
/** What put the whole installation in safe state (system-wide records and the holds they made). */
export const SAFE_STATE_CAUSES = [
  'shutdown',
  'starting',
  'configuration_invalid',
  'configuration_missing',
  'source_disconnected',
  'meter_invalid',
] as const;
export const TIER_REACTIONS = [
  'may_add',
  'hold',
  'reduce_ordinary_cycle',
  'reduce_next_cycle',
  'shed_one_immediately',
  'shed_all_on_phase',
] as const;
export const DEVICE_STATE_INVALID_REASONS = [
  'configuration_invalid',
  'source_disconnected',
  'awaiting',
  'missing',
  'unavailable',
  'stale',
  'unrecognized',
] as const;
/** A relay stage's applied state; a modulating device reports a setpoint in A instead. */
export const RELAY_STATES = ['on', 'off'] as const;
/** Who decided a device change: protection, dispatch, a manual override (SAFE-04) or the active phase self-test (CONN-05). */
export const DECISION_ORIGINS = ['guard', 'dispatch', 'override', 'self_test'] as const;
/** A device's settling window at a dispatch decision (DISP-05). */
export const SETTLING_STATES = ['open', 'closed', 'timed_out'] as const;
/** The outcome of an effector verification (DISP-07). */
export const VERDICTS = ['match', 'mismatch', 'inconclusive', 'not_applied', 'unverifiable', 'timed_out'] as const;
export const MEASURE_UNITS = ['amperes', 'watts'] as const;
export const OVERRIDE_MODES = ['auto', 'force_on', 'force_off'] as const;

export type ReasonCode = (typeof REASON_CODES)[number];
export type DecisionScope = (typeof DECISION_SCOPES)[number];
export type DecisionAction = (typeof DECISION_ACTIONS)[number];
export type DecisionKind = (typeof DECISION_KINDS)[number];
export type OperatingMode = (typeof OPERATING_MODES)[number];
export type ModeNote = (typeof MODE_NOTES)[number];
export type KillSwitchSource = (typeof KILL_SWITCH_SOURCES)[number];
export type ModeCondition = (typeof MODE_CONDITIONS)[number];
export type ControlSwitchSource = (typeof CONTROL_SWITCH_SOURCES)[number];
export type SafeStateCause = (typeof SAFE_STATE_CAUSES)[number];
export type TierReaction = (typeof TIER_REACTIONS)[number];
export type DeviceStateInvalidReason = (typeof DEVICE_STATE_INVALID_REASONS)[number];
export type RelayState = (typeof RELAY_STATES)[number];
export type DecisionOrigin = (typeof DECISION_ORIGINS)[number];
export type SettlingState = (typeof SETTLING_STATES)[number];
export type Verdict = (typeof VERDICTS)[number];
export type MeasureUnit = (typeof MEASURE_UNITS)[number];
export type OverrideMode = (typeof OVERRIDE_MODES)[number];

/** The phase at the decision; current is measured, import positive. */
export interface PhaseInputs {
  phase: PhaseName;
  dataValid: boolean;
  invalidReason: ProtectionInvalidReason | null;
  currentA: number | null;
  effectiveCurrentA: number | null;
  multiple: number | null;
  band: number | null;
  reaction: TierReaction | null;
  budget: number;
  continuousA: number;
  reserveThresholdA: number;
  headroomA: number | null;
  direction: FlowDirection | null;
  reportedAt: Date | null;
}

/** The device stage at the decision. */
export interface DeviceInputs {
  applied: RelayState | null;
  setpointA: number | null;
  stateFresh: boolean;
  stateInvalidReason: DeviceStateInvalidReason | null;
  shedRank: number | null;
  valuePerKwh: number;
  shedPriority: number | null;
  minOnRemainingS: number;
  minOffRemainingS: number;
  /** False for a modulating device without a stop actuator, and for storage: Off cannot happen. */
  canSwitchOff: boolean;
}

/**
 * A device level: relay flags per stage, or a setpoint in A (exactly one of the two); enabled is
 * false only for a modulating device switched off through its stop actuator.
 */
export interface Level {
  relays: boolean[] | null;
  setpointA: number | null;
  enabled: boolean;
}

/** Headroom on one constraint: a main phase (group null) or a circuit group's phase. */
export interface ConstraintHeadroom {
  group: string | null;
  phase: PhaseName;
  /** E from the guard; null without valid data. */
  envelopeA: number | null;
  /** What was left when the device's turn came in the allocation; null when it was not allocated. */
  remainingA: number | null;
}

export interface Verification {
  /** A relay's expected step, or a setpoint's new ceiling. */
  expected: number;
  /** The measured step, or the draw after the window. */
  measured: number;
  unit: MeasureUnit;
  verdict: Verdict;
}

/** What a dispatch decision was made from (MODE-03). */
export interface DispatchInputs {
  headroom: ConstraintHeadroom[];
  startHysteresisA: number;
  stopHysteresisA: number;
  startRank: number | null;
  /** The device's share of the measured current per phase. */
  contribution: { phase: PhaseName; currentA: number }[];
  /** The in-flight change counted for the device per constraint. */
  inFlight: { group: string | null; phase: PhaseName; currentA: number }[];
  settling: SettlingState | null;
  verification: Verification | null;
  minOnRemainingS: number;
  minOffRemainingS: number;
  deferredSince: Date | null;
  override: OverrideMode;
  /** The command re-asserts a level after a reconnect or a state that did not follow (Stage C). */
  reasserted: boolean;
  /** The hourly balance of the step (BAL-07); null on lines written before it and while no energy counters are mapped. */
  balance: BalanceInputs | null;
}

/** The hourly balance a dispatch decision was made beside: Wh and W, positive = export; null = not known at the step. */
export interface BalanceInputs {
  balanceWh: number | null;
  freeW: number | null;
  loadW: number | null;
  projectionWh: number | null;
  loWh: number | null;
  hiWh: number | null;
  /** Seconds to the :00 the rule used; 0 when the balance was not of the running hour. */
  tRemS: number;
  /** Inside the burn window: the upper edge ramps down to the floor. */
  burning: boolean;
}

/**
 * The mode at a mode decision. The control fields are null on lines written before 0.7.0, which
 * knew only the add-on option and the UI choice.
 */
export interface ModeInputs {
  ceiling: OperatingMode;
  note: ModeNote;
  controlAllowed: boolean | null;
  configurationValid: boolean | null;
  stateConfirmed: boolean | null;
  missing: ModeCondition[];
  source: ControlSwitchSource | null;
}

export interface Decision {
  id: string;
  at: Date;
  scope: DecisionScope;
  device: string | null;
  /** Stage number of a stepped device, from 1; null for a device with one stage. */
  stage: number | null;
  phase: PhaseName | null;
  action: DecisionAction;
  kind: DecisionKind;
  reason: ReasonCode;
  /** A rule the decision outranked (a min_on lockout). */
  overridden: ReasonCode | null;
  mode: OperatingMode;
  commanded: boolean;
  refusedBy: ReasonCode | null;
  /** Who decided a device change; null for phase and system events and for 0.3.x decisions. */
  origin: DecisionOrigin | null;
  /** The circuit group the decision is about or was caused by. */
  group: string | null;
  /** The device's target level after the decision (in shadow mode nothing is applied). */
  target: Level | null;
  inputs: {
    phase: PhaseInputs | null;
    device: DeviceInputs | null;
    mode: ModeInputs | null;
    killSwitch: KillSwitchSource | null;
    safeStateCause: SafeStateCause | null;
    dispatch: DispatchInputs | null;
  };
}

export interface DecisionsPage {
  items: Decision[];
  /** Cursor for the next (older) page; null when there is none. */
  next: string | null;
  logWritable: boolean;
}

const reason = (raw: unknown, path: string) => oneOf(REASON_CODES, raw, path);
const phaseName = (raw: unknown, path: string) => oneOf(PHASES, raw, path);
const groupName = (raw: unknown, path: string) => nullable(raw, path, text);

/** A level as the add-on writes it: relays or a setpoint, never both, never neither. */
export function parseLevel(raw: unknown, path: string): Level {
  const r = object(raw, path);
  const relays = nullable(field(r, 'relays', path), `${path}.relays`, (v, p) => list(v, p, bool));
  const setpointA = nullable(field(r, 'setpointA', path), `${path}.setpointA`, number);
  if ((relays === null) === (setpointA === null)) fail(path, 'relays or a setpoint, exactly one of them');
  return { relays, setpointA, enabled: bool(field(r, 'enabled', path), `${path}.enabled`) };
}

function headroom(raw: unknown, path: string): ConstraintHeadroom {
  const r = object(raw, path);
  return {
    group: groupName(field(r, 'group', path), `${path}.group`),
    phase: phaseName(field(r, 'phase', path), `${path}.phase`),
    envelopeA: nullable(field(r, 'envelopeA', path), `${path}.envelopeA`, number),
    remainingA: nullable(field(r, 'remainingA', path), `${path}.remainingA`, number),
  };
}

export function parseVerification(raw: unknown, path: string): Verification {
  const r = object(raw, path);
  return {
    expected: number(field(r, 'expected', path), `${path}.expected`),
    measured: number(field(r, 'measured', path), `${path}.measured`),
    unit: oneOf(MEASURE_UNITS, field(r, 'unit', path), `${path}.unit`),
    verdict: oneOf(VERDICTS, field(r, 'verdict', path), `${path}.verdict`),
  };
}

function balanceInputs(raw: unknown, path: string): BalanceInputs {
  const r = object(raw, path);
  const n = (key: string) => nullable(field(r, key, path), `${path}.${key}`, number);
  return {
    balanceWh: n('balanceWh'),
    freeW: n('freeW'),
    loadW: n('loadW'),
    projectionWh: n('projectionWh'),
    loWh: n('loWh'),
    hiWh: n('hiWh'),
    tRemS: number(field(r, 'tRemS', path), `${path}.tRemS`),
    burning: bool(field(r, 'burning', path), `${path}.burning`),
  };
}

function dispatchInputs(raw: unknown, path: string): DispatchInputs {
  const r = object(raw, path);
  const n = (key: string) => number(field(r, key, path), `${path}.${key}`);
  return {
    headroom: list(field(r, 'headroom', path), `${path}.headroom`, headroom),
    startHysteresisA: n('startHysteresisA'),
    stopHysteresisA: n('stopHysteresisA'),
    startRank: nullable(field(r, 'startRank', path), `${path}.startRank`, positive),
    contribution: list(field(r, 'contribution', path), `${path}.contribution`, (v, p) => {
      const c = object(v, p);
      return { phase: phaseName(field(c, 'phase', p), `${p}.phase`), currentA: number(field(c, 'currentA', p), `${p}.currentA`) };
    }),
    inFlight: list(field(r, 'inFlight', path), `${path}.inFlight`, (v, p) => {
      const c = object(v, p);
      return {
        group: groupName(field(c, 'group', p), `${p}.group`),
        phase: phaseName(field(c, 'phase', p), `${p}.phase`),
        currentA: number(field(c, 'currentA', p), `${p}.currentA`),
      };
    }),
    settling: nullable(field(r, 'settling', path), `${path}.settling`, (v, p) => oneOf(SETTLING_STATES, v, p)),
    verification: nullable(field(r, 'verification', path), `${path}.verification`, parseVerification),
    minOnRemainingS: n('minOnRemainingS'),
    minOffRemainingS: n('minOffRemainingS'),
    deferredSince: nullable(field(r, 'deferredSince', path), `${path}.deferredSince`, instant),
    override: oneOf(OVERRIDE_MODES, field(r, 'override', path), `${path}.override`),
    reasserted: bool(field(r, 'reasserted', path), `${path}.reasserted`),
    balance: nullable(field(r, 'balance', path), `${path}.balance`, balanceInputs),
  };
}

function phaseInputs(raw: unknown, path: string): PhaseInputs {
  const r = object(raw, path);
  const n = (key: string) => nullable(field(r, key, path), `${path}.${key}`, number);
  const dataValid = bool(field(r, 'dataValid', path), `${path}.dataValid`);
  const invalidReason = nullable(field(r, 'invalidReason', path), `${path}.invalidReason`, (v, p) =>
    oneOf(PROTECTION_INVALID_REASONS, v, p),
  );
  // Valid data never carries a reason; a "valid" next to a reason must never be rendered.
  if (dataValid && invalidReason !== null) fail(`${path}.invalidReason`, 'null for valid data');
  return {
    phase: oneOf(PHASES, field(r, 'phase', path), `${path}.phase`),
    dataValid,
    invalidReason,
    currentA: n('currentA'),
    effectiveCurrentA: n('effectiveCurrentA'),
    multiple: n('multiple'),
    band: nullable(field(r, 'band', path), `${path}.band`, count),
    reaction: nullable(field(r, 'reaction', path), `${path}.reaction`, (v, p) => oneOf(TIER_REACTIONS, v, p)),
    budget: number(field(r, 'budget', path), `${path}.budget`),
    continuousA: number(field(r, 'continuousA', path), `${path}.continuousA`),
    reserveThresholdA: number(field(r, 'reserveThresholdA', path), `${path}.reserveThresholdA`),
    headroomA: n('headroomA'),
    direction: nullable(field(r, 'direction', path), `${path}.direction`, (v, p) => oneOf(FLOW_DIRECTIONS, v, p)),
    reportedAt: nullable(field(r, 'reportedAt', path), `${path}.reportedAt`, instant),
  };
}

function deviceInputs(raw: unknown, path: string): DeviceInputs {
  const r = object(raw, path);
  const applied = nullable(field(r, 'applied', path), `${path}.applied`, (v, p) => oneOf(RELAY_STATES, v, p));
  const setpointA = nullable(field(r, 'setpointA', path), `${path}.setpointA`, number);
  // A stage is a relay or a setpoint, never both.
  if (applied !== null && setpointA !== null) fail(`${path}.setpointA`, 'null next to a relay state');
  return {
    applied,
    setpointA,
    stateFresh: bool(field(r, 'stateFresh', path), `${path}.stateFresh`),
    stateInvalidReason: nullable(field(r, 'stateInvalidReason', path), `${path}.stateInvalidReason`, (v, p) =>
      oneOf(DEVICE_STATE_INVALID_REASONS, v, p),
    ),
    shedRank: nullable(field(r, 'shedRank', path), `${path}.shedRank`, positive),
    valuePerKwh: number(field(r, 'valuePerKwh', path), `${path}.valuePerKwh`),
    shedPriority: nullable(field(r, 'shedPriority', path), `${path}.shedPriority`, number),
    minOnRemainingS: number(field(r, 'minOnRemainingS', path), `${path}.minOnRemainingS`),
    minOffRemainingS: number(field(r, 'minOffRemainingS', path), `${path}.minOffRemainingS`),
    canSwitchOff: bool(field(r, 'canSwitchOff', path), `${path}.canSwitchOff`),
  };
}

function modeInputs(raw: unknown, path: string): ModeInputs {
  const r = object(raw, path);
  return {
    ceiling: oneOf(OPERATING_MODES, field(r, 'ceiling', path), `${path}.ceiling`),
    note: oneOf(MODE_NOTES, field(r, 'note', path), `${path}.note`),
    controlAllowed: nullable(field(r, 'controlAllowed', path), `${path}.controlAllowed`, bool),
    configurationValid: nullable(field(r, 'configurationValid', path), `${path}.configurationValid`, bool),
    stateConfirmed: nullable(field(r, 'stateConfirmed', path), `${path}.stateConfirmed`, bool),
    missing: list(field(r, 'missing', path), `${path}.missing`, (v, p) => oneOf(MODE_CONDITIONS, v, p)),
    source: nullable(field(r, 'source', path), `${path}.source`, (v, p) => oneOf(CONTROL_SWITCH_SOURCES, v, p)),
  };
}

/** One decision, exactly as the add-on writes it; anything else throws. */
export function parseDecision(raw: unknown, path: string): Decision {
  const r = object(raw, path);
  const inputs = object(field(r, 'inputs', path), `${path}.inputs`);
  const scope = oneOf(DECISION_SCOPES, field(r, 'scope', path), `${path}.scope`);
  const device = nullable(field(r, 'device', path), `${path}.device`, text);
  const phase = nullable(field(r, 'phase', path), `${path}.phase`, phaseName);
  const group = groupName(field(r, 'group', path), `${path}.group`);
  // The add-on cannot build a device decision without its device, a phase decision without its
  // phase or a group decision without its group.
  if (scope === 'device' && device === null) fail(`${path}.device`, 'present for a device decision');
  if (scope === 'phase' && phase === null) fail(`${path}.phase`, 'present for a phase decision');
  if (scope === 'group' && group === null) fail(`${path}.group`, 'present for a group decision');
  return {
    id: text(field(r, 'id', path), `${path}.id`),
    at: instant(field(r, 'at', path), `${path}.at`),
    scope,
    device,
    stage: nullable(field(r, 'stage', path), `${path}.stage`, positive),
    phase,
    action: oneOf(DECISION_ACTIONS, field(r, 'action', path), `${path}.action`),
    kind: oneOf(DECISION_KINDS, field(r, 'kind', path), `${path}.kind`),
    reason: reason(field(r, 'reason', path), `${path}.reason`),
    overridden: nullable(field(r, 'overridden', path), `${path}.overridden`, reason),
    mode: oneOf(OPERATING_MODES, field(r, 'mode', path), `${path}.mode`),
    commanded: bool(field(r, 'commanded', path), `${path}.commanded`),
    refusedBy: nullable(field(r, 'refusedBy', path), `${path}.refusedBy`, reason),
    origin: nullable(field(r, 'origin', path), `${path}.origin`, (v, p) => oneOf(DECISION_ORIGINS, v, p)),
    group,
    target: nullable(field(r, 'target', path), `${path}.target`, parseLevel),
    inputs: {
      phase: nullable(field(inputs, 'phase', `${path}.inputs`), `${path}.inputs.phase`, phaseInputs),
      device: nullable(field(inputs, 'device', `${path}.inputs`), `${path}.inputs.device`, deviceInputs),
      mode: nullable(field(inputs, 'mode', `${path}.inputs`), `${path}.inputs.mode`, modeInputs),
      killSwitch: nullable(field(inputs, 'killSwitch', `${path}.inputs`), `${path}.inputs.killSwitch`, (v, p) =>
        oneOf(KILL_SWITCH_SOURCES, v, p),
      ),
      safeStateCause: nullable(field(inputs, 'safeStateCause', `${path}.inputs`), `${path}.inputs.safeStateCause`, (v, p) =>
        oneOf(SAFE_STATE_CAUSES, v, p),
      ),
      dispatch: nullable(field(inputs, 'dispatch', `${path}.inputs`), `${path}.inputs.dispatch`, dispatchInputs),
    },
  };
}

export function parseDecisions(raw: unknown, path: string): Decision[] {
  return list(raw, path, parseDecision);
}

/** Filters of GET api/decisions; a missing one does not filter. */
export interface DecisionQuery {
  device?: string;
  kind?: DecisionKind;
  /** The cursor of an older page (a previous page's next). */
  before?: string;
  limit?: number;
}

/** GET api/decisions: one page of recent decisions, newest first. */
export function parseDecisionsPage(raw: unknown): DecisionsPage {
  const r = object(raw, 'payload');
  return {
    items: parseDecisions(field(r, 'items', 'payload'), 'items'),
    next: nullable(field(r, 'next', 'payload'), 'next', text),
    logWritable: bool(field(r, 'logWritable', 'payload'), 'logWritable'),
  };
}

/** GET api/decisions with the given filters (the add-on refuses an unknown one with 400). */
export async function fetchDecisions(signal: AbortSignal, query: DecisionQuery = {}): Promise<DecisionsPage> {
  const parameters = new URLSearchParams();
  if (query.device !== undefined) parameters.set('device', query.device);
  if (query.kind !== undefined) parameters.set('kind', query.kind);
  if (query.before !== undefined) parameters.set('before', query.before);
  if (query.limit !== undefined) parameters.set('limit', String(query.limit));
  const search = parameters.toString();
  const response = await fetch(search === '' ? 'api/decisions' : `api/decisions?${search}`, {
    signal,
    cache: 'no-store',
    headers: { Accept: 'application/json' },
  });
  if (!response.ok) throw new Error(`Decisions request failed with HTTP ${response.status}.`);
  return parseDecisionsPage(await response.json());
}
