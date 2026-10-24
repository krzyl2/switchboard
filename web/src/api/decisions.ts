import { FLOW_DIRECTIONS, PHASES, PROTECTION_INVALID_REASONS } from './signals';
import type { FlowDirection, PhaseName, ProtectionInvalidReason } from './signals';
import { bool, count, fail, field, instant, list, nullable, number, object, oneOf, positive, text } from './strict';

// Contract of a decision as GET api/decisions and GET api/guard write it
// (src/Switchboard.Host/Web/DecisionsEndpoint.cs). Every closed list mirrors a backend enum;
// GuardSpaContractTests (tests/Switchboard.Tests) compares them, so a value added on one side only
// fails the build instead of blanking the page.

/** The closed reason-code list, version 2 (04-ALGORITHMS §6). */
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
] as const;
export const DECISION_SCOPES = ['system', 'phase', 'device'] as const;
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
] as const;
export const OPERATING_MODES = ['shadow', 'active'] as const;
export const MODE_NOTES = ['none', 'active_unavailable_before_stage3'] as const;
export const KILL_SWITCH_SOURCES = ['api', 'mqtt', 'restored'] as const;
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

export type ReasonCode = (typeof REASON_CODES)[number];
export type DecisionScope = (typeof DECISION_SCOPES)[number];
export type DecisionAction = (typeof DECISION_ACTIONS)[number];
export type OperatingMode = (typeof OPERATING_MODES)[number];
export type ModeNote = (typeof MODE_NOTES)[number];
export type KillSwitchSource = (typeof KILL_SWITCH_SOURCES)[number];
export type SafeStateCause = (typeof SAFE_STATE_CAUSES)[number];
export type TierReaction = (typeof TIER_REACTIONS)[number];
export type DeviceStateInvalidReason = (typeof DEVICE_STATE_INVALID_REASONS)[number];
export type RelayState = (typeof RELAY_STATES)[number];

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
  /** False for a modulating device before a stop actuator exists: Off cannot happen. */
  canSwitchOff: boolean;
}

export interface ModeInputs {
  ceiling: OperatingMode;
  uiChoice: OperatingMode;
  note: ModeNote;
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
  reason: ReasonCode;
  /** A rule the decision outranked (a min_on lockout). */
  overridden: ReasonCode | null;
  mode: OperatingMode;
  commanded: boolean;
  refusedBy: ReasonCode | null;
  inputs: {
    phase: PhaseInputs | null;
    device: DeviceInputs | null;
    mode: ModeInputs | null;
    killSwitch: KillSwitchSource | null;
    safeStateCause: SafeStateCause | null;
  };
}

export interface DecisionsPage {
  items: Decision[];
  /** Cursor for the next (older) page; null when there is none. */
  next: string | null;
  logWritable: boolean;
}

const reason = (raw: unknown, path: string) => oneOf(REASON_CODES, raw, path);

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
    uiChoice: oneOf(OPERATING_MODES, field(r, 'uiChoice', path), `${path}.uiChoice`),
    note: oneOf(MODE_NOTES, field(r, 'note', path), `${path}.note`),
  };
}

/** One decision, exactly as the add-on writes it; anything else throws. */
export function parseDecision(raw: unknown, path: string): Decision {
  const r = object(raw, path);
  const inputs = object(field(r, 'inputs', path), `${path}.inputs`);
  const scope = oneOf(DECISION_SCOPES, field(r, 'scope', path), `${path}.scope`);
  const device = nullable(field(r, 'device', path), `${path}.device`, text);
  const phase = nullable(field(r, 'phase', path), `${path}.phase`, (v, p) => oneOf(PHASES, v, p));
  // The add-on cannot build a device decision without its device or a phase decision without its phase.
  if (scope === 'device' && device === null) fail(`${path}.device`, 'present for a device decision');
  if (scope === 'phase' && phase === null) fail(`${path}.phase`, 'present for a phase decision');
  return {
    id: text(field(r, 'id', path), `${path}.id`),
    at: instant(field(r, 'at', path), `${path}.at`),
    scope,
    device,
    stage: nullable(field(r, 'stage', path), `${path}.stage`, positive),
    phase,
    action: oneOf(DECISION_ACTIONS, field(r, 'action', path), `${path}.action`),
    reason: reason(field(r, 'reason', path), `${path}.reason`),
    overridden: nullable(field(r, 'overridden', path), `${path}.overridden`, reason),
    mode: oneOf(OPERATING_MODES, field(r, 'mode', path), `${path}.mode`),
    commanded: bool(field(r, 'commanded', path), `${path}.commanded`),
    refusedBy: nullable(field(r, 'refusedBy', path), `${path}.refusedBy`, reason),
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
    },
  };
}

export function parseDecisions(raw: unknown, path: string): Decision[] {
  return list(raw, path, parseDecision);
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
