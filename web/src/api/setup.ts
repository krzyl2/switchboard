import { PHASES, type PhaseName } from './signals';
import { TIER_REACTIONS, type TierReaction } from './decisions';
import { bool, count, fail, field, instant, list, nullable, number, object, oneOf, positive, text, type Json } from './strict';

// Contract of the setup wizard's API (src/Switchboard.Host/Web/SetupEndpoint.cs, Stage 5). Every
// answer is parsed strictly: a key that is missing or an enum value the page does not know makes the
// answer unusable rather than half-read. Closed lists mirror backend enums; SetupSpaContractTests
// (tests/Switchboard.Tests) compares them and the committed *.sample.json files with what the
// endpoints write. The draft's own text fields (a topology, a duration) stay free text: the server
// judges them, so a hand-written file with an odd value still opens in the wizard to be fixed.

/** The closed list of setup issue codes; each has an en and pl sentence. */
export const SETUP_ISSUE_CODES = [
  'entity_not_found',
  'entity_check_unavailable',
  'entity_unavailable',
  'unit_mismatch',
  'device_class_mismatch',
  'value_implausible',
  'phase_sum_mismatch',
  'phase_sum_skipped',
  'max_age_below_refresh',
  'refresh_not_detected',
  'derived_sensor_refused',
  'derived_check_unavailable',
  'actuator_derived',
  'reserve_below_minimum',
  'phase_self_test_mismatch',
  'phase_self_test_missing',
  'acknowledgement_required',
  'comments_acknowledgement_required',
  'replace_unreadable_required',
  'revision_conflict',
  'existing_device_immutable',
  'device_id_taken',
  'max_age_above_refresh',
  'required_field',
  'preserved_path_unknown',
  'document_invalid',
  'source_not_in_registry',
] as const;
export const SETUP_SEVERITIES = ['error', 'warning'] as const;
/** A candidate entity's live readout (UI-04). */
export const READOUT_STATUSES = ['pending', 'found', 'not_found', 'unavailable'] as const;
export const SELF_TEST_STATUSES = ['watching', 'finished', 'expired', 'cancelled'] as const;
/** The passive self-test (CONN-05): inconclusive is never shown as a pass. */
export const PHASE_STEP_VERDICTS = ['confirmed', 'mismatch', 'inconclusive'] as const;
export const INCONCLUSIVE_REASONS = [
  'meter_silent',
  'not_isolated',
  'unsettled_before',
  'step_below_noise',
  'wrong_direction',
  'no_step',
  'foreign_step',
  'rating_differs',
  'timed_out',
  'conflicting',
  'unconfirmed',
] as const;
/** Who observed a switching: the guided watch, or the running add-on (someone else switched it). */
/** watch: the passive self-test; recorded: a switching seen at run time; active: the active self-test switched it (CONN-05). */
export const OBSERVATION_SOURCES = ['watch', 'recorded', 'active'] as const;
/** Why the active phase self-test cannot start now (ActivePhaseTestService). */
export const ACTIVE_TEST_REFUSALS = [
  'control_not_active',
  'kill_switch',
  'safe_state',
  'not_declared',
  'type_not_supported',
  'multi_phase_stage',
  'device_on',
  'state_unknown',
  'lockout_min_off',
  'headroom_insufficient',
  'increases_stopped',
  'override_set',
  'unresponsive',
  'test_running',
  'change_limit',
  'refresh_unknown',
] as const;
export const ACTIVE_TEST_STATES = ['running', 'finished', 'aborted'] as const;
export const ACTIVE_TEST_PHASES = ['pending_on', 'on', 'pending_off', 'judging', 'done'] as const;
export const ACTIVE_TEST_ABORT_REASONS = [
  'guard_acted',
  'safe_state',
  'kill_switch',
  'increases_stopped',
  'not_confirmed',
  'cancelled',
  'control_left_active',
  'configuration_changed',
] as const;
export const FILE_STATES = ['missing', 'valid', 'invalid'] as const;
export const PHASE_SUM_STATES = ['passed', 'failed', 'skipped'] as const;
/** above_refresh: a meter row trusted far longer than it reports (a frozen value would stay trusted). */
export const REFRESH_CHECK_STATES = ['ok', 'below_refresh', 'above_refresh', 'not_detected'] as const;
/** How the running add-on read a saved file. */
export const LOADED_STATUSES = ['valid', 'invalid', 'missing'] as const;

export type SetupIssueCode = (typeof SETUP_ISSUE_CODES)[number];
export type SetupSeverity = (typeof SETUP_SEVERITIES)[number];
export type ReadoutStatus = (typeof READOUT_STATUSES)[number];
export type SelfTestStatus = (typeof SELF_TEST_STATUSES)[number];
export type PhaseStepVerdict = (typeof PHASE_STEP_VERDICTS)[number];
export type InconclusiveReason = (typeof INCONCLUSIVE_REASONS)[number];
export type ObservationSource = (typeof OBSERVATION_SOURCES)[number];
export type ActiveTestRefusalCode = (typeof ACTIVE_TEST_REFUSALS)[number];
export type ActiveTestState = (typeof ACTIVE_TEST_STATES)[number];
export type ActiveTestPhase = (typeof ACTIVE_TEST_PHASES)[number];
export type ActiveTestAbortReason = (typeof ACTIVE_TEST_ABORT_REASONS)[number];
export type FileState = (typeof FILE_STATES)[number];

// ---------------------------------------------------------------------------------------------
// The draft: the parts of switchboard.yaml the wizard owns, in the shape of its five steps.

export interface PhaseValues {
  L1: number | null;
  L2: number | null;
  L3: number | null;
}

/** One mapped entity: its signal's entity, unit and max_age (a duration such as 90s). */
export interface SignalDraft {
  entity: string | null;
  unit: string | null;
  maxAge: string | null;
}

export interface MeterPhaseDraft {
  current: SignalDraft | null;
  power: SignalDraft | null;
  voltage: SignalDraft | null;
}

export interface SetupDraft {
  connection: {
    topology: string | null;
    breakerRatingA: number | null;
    characteristic: string | null;
    voltageV: number | null;
    reserveW: PhaseValues | null;
  };
  meter: {
    currentSign: string | null;
    currentDirection: string | null;
    powerSign: string | null;
    phases: { L1: MeterPhaseDraft | null; L2: MeterPhaseDraft | null; L3: MeterPhaseDraft | null };
    total: { power: SignalDraft | null; current: SignalDraft | null };
  };
  sources: {
    inverter: {
      power: SignalDraft | null;
      sign: string | null;
      unavailableAsZero: boolean | null;
      phase: string | null;
      phaseCurrents: { L1: SignalDraft | null; L2: SignalDraft | null; L3: SignalDraft | null } | null;
    } | null;
  };
  devices: DeviceDraft[];
  acknowledgements: { notProtective: boolean; commentsDropped: boolean; replaceUnreadable: boolean };
  /** Paths of preserved sections the user confirmed to remove. */
  dropPreserved: string[];
}

export interface DeviceDraft {
  id: string | null;
  /** As the server reported it; on a save the server decides again from the file by id. */
  existing: boolean;
  /** False for a device kept as written (stepped, a per-phase draw): it can only be removed. */
  editable: boolean;
  template: string | null;
  name: string | null;
  type: string | null;
  actuator: string | null;
  stateEntity: string | null;
  stateReports: string | null;
  phase: string | null;
  ratedCurrentA: number | null;
  ratedPowerW: number | null;
  min: number | null;
  max: number | null;
  step: number | null;
  reactionTime: string | null;
  minOn: string | null;
  minOff: string | null;
  valuePerKwh: number | null;
  circuitGroup: string | null;
  rotationGroup: string | null;
  /** A modulating device's stop actuator (switch or input_boolean); null for none or another type. */
  stopActuator: string | null;
  /** start: restore_only or headroom; null leaves the device on the file's default (devices.default_start). */
  start: string | null;
  /** currentUnit / powerUnit: the unit the picked draw entity reports in; null for the default A / W. */
  draw: { current: string | null; power: string | null; maxAge: string | null; currentUnit: string | null; powerUnit: string | null } | null;
}

const optText = (r: Json, key: string, path: string) => nullable(field(r, key, path), `${path}.${key}`, text);
const optNumber = (r: Json, key: string, path: string) => nullable(field(r, key, path), `${path}.${key}`, number);

function signalDraft(raw: unknown, path: string): SignalDraft {
  const r = object(raw, path);
  return { entity: optText(r, 'entity', path), unit: optText(r, 'unit', path), maxAge: optText(r, 'maxAge', path) };
}

const optSignal = (r: Json, key: string, path: string) => nullable(field(r, key, path), `${path}.${key}`, signalDraft);

function phaseValues(raw: unknown, path: string): PhaseValues {
  const r = object(raw, path);
  return { L1: optNumber(r, 'L1', path), L2: optNumber(r, 'L2', path), L3: optNumber(r, 'L3', path) };
}

function meterPhase(raw: unknown, path: string): MeterPhaseDraft {
  const r = object(raw, path);
  return { current: optSignal(r, 'current', path), power: optSignal(r, 'power', path), voltage: optSignal(r, 'voltage', path) };
}

export function parseDeviceDraft(raw: unknown, path: string): DeviceDraft {
  const r = object(raw, path);
  return {
    id: optText(r, 'id', path),
    existing: bool(field(r, 'existing', path), `${path}.existing`),
    editable: bool(field(r, 'editable', path), `${path}.editable`),
    template: optText(r, 'template', path),
    name: optText(r, 'name', path),
    type: optText(r, 'type', path),
    actuator: optText(r, 'actuator', path),
    stateEntity: optText(r, 'stateEntity', path),
    stateReports: optText(r, 'stateReports', path),
    phase: optText(r, 'phase', path),
    ratedCurrentA: optNumber(r, 'ratedCurrentA', path),
    ratedPowerW: optNumber(r, 'ratedPowerW', path),
    min: optNumber(r, 'min', path),
    max: optNumber(r, 'max', path),
    step: optNumber(r, 'step', path),
    reactionTime: optText(r, 'reactionTime', path),
    minOn: optText(r, 'minOn', path),
    minOff: optText(r, 'minOff', path),
    valuePerKwh: optNumber(r, 'valuePerKwh', path),
    circuitGroup: optText(r, 'circuitGroup', path),
    rotationGroup: optText(r, 'rotationGroup', path),
    stopActuator: optText(r, 'stopActuator', path),
    start: optText(r, 'start', path),
    draw: nullable(field(r, 'draw', path), `${path}.draw`, (v, p) => {
      const d = object(v, p);
      return {
        current: optText(d, 'current', p),
        power: optText(d, 'power', p),
        maxAge: optText(d, 'maxAge', p),
        currentUnit: optText(d, 'currentUnit', p),
        powerUnit: optText(d, 'powerUnit', p),
      };
    }),
  };
}

/** The draft exactly as GET api/setup/draft writes it. */
export function parseDraft(raw: unknown, path = 'draft'): SetupDraft {
  const r = object(raw, path);
  const c = object(field(r, 'connection', path), `${path}.connection`);
  const m = object(field(r, 'meter', path), `${path}.meter`);
  const phases = object(field(m, 'phases', `${path}.meter`), `${path}.meter.phases`);
  const total = object(field(m, 'total', `${path}.meter`), `${path}.meter.total`);
  const s = object(field(r, 'sources', path), `${path}.sources`);
  const a = object(field(r, 'acknowledgements', path), `${path}.acknowledgements`);
  const cp = `${path}.connection`;
  const mp = `${path}.meter`;
  return {
    connection: {
      topology: optText(c, 'topology', cp),
      breakerRatingA: optNumber(c, 'breakerRatingA', cp),
      characteristic: optText(c, 'characteristic', cp),
      voltageV: optNumber(c, 'voltageV', cp),
      reserveW: nullable(field(c, 'reserveW', cp), `${cp}.reserveW`, phaseValues),
    },
    meter: {
      currentSign: optText(m, 'currentSign', mp),
      currentDirection: optText(m, 'currentDirection', mp),
      powerSign: optText(m, 'powerSign', mp),
      phases: {
        L1: nullable(field(phases, 'L1', `${mp}.phases`), `${mp}.phases.L1`, meterPhase),
        L2: nullable(field(phases, 'L2', `${mp}.phases`), `${mp}.phases.L2`, meterPhase),
        L3: nullable(field(phases, 'L3', `${mp}.phases`), `${mp}.phases.L3`, meterPhase),
      },
      total: { power: optSignal(total, 'power', `${mp}.total`), current: optSignal(total, 'current', `${mp}.total`) },
    },
    sources: {
      inverter: nullable(field(s, 'inverter', `${path}.sources`), `${path}.sources.inverter`, (v, p) => {
        const i = object(v, p);
        return {
          power: optSignal(i, 'power', p),
          sign: optText(i, 'sign', p),
          unavailableAsZero: nullable(field(i, 'unavailableAsZero', p), `${p}.unavailableAsZero`, bool),
          phase: optText(i, 'phase', p),
          phaseCurrents: nullable(field(i, 'phaseCurrents', p), `${p}.phaseCurrents`, (pc, pp) => {
            const x = object(pc, pp);
            return { L1: optSignal(x, 'L1', pp), L2: optSignal(x, 'L2', pp), L3: optSignal(x, 'L3', pp) };
          }),
        };
      }),
    },
    devices: list(field(r, 'devices', path), `${path}.devices`, parseDeviceDraft),
    acknowledgements: {
      notProtective: bool(field(a, 'notProtective', `${path}.acknowledgements`), `${path}.acknowledgements.notProtective`),
      commentsDropped: bool(field(a, 'commentsDropped', `${path}.acknowledgements`), `${path}.acknowledgements.commentsDropped`),
      replaceUnreadable: bool(field(a, 'replaceUnreadable', `${path}.acknowledgements`), `${path}.acknowledgements.replaceUnreadable`),
    },
    dropPreserved: list(field(r, 'dropPreserved', path), `${path}.dropPreserved`, text),
  };
}

// ---------------------------------------------------------------------------------------------
// Issues, validation and saving (UI-05, UI-06, CONN-06).

/** One finding at the draft field it concerns; field null means a part of the file the wizard does not edit. */
export interface SetupIssue {
  code: SetupIssueCode;
  severity: SetupSeverity;
  field: string | null;
  documentPath: string | null;
  message: string;
  args: Record<string, string>;
}

function stringMap(raw: unknown, path: string): Record<string, string> {
  const r = object(raw, path);
  return Object.fromEntries(Object.entries(r).map(([key, value]) => [key, text(value, `${path}.${key}`)]));
}

export function parseIssue(raw: unknown, path: string): SetupIssue {
  const r = object(raw, path);
  return {
    code: oneOf(SETUP_ISSUE_CODES, field(r, 'code', path), `${path}.code`),
    severity: oneOf(SETUP_SEVERITIES, field(r, 'severity', path), `${path}.severity`),
    field: optText(r, 'field', path),
    documentPath: optText(r, 'documentPath', path),
    message: text(field(r, 'message', path), `${path}.message`),
    args: stringMap(field(r, 'args', path), `${path}.args`),
  };
}

export interface PreservedSection {
  path: string;
  summary: string;
}

export interface DraftView {
  /** The file's revision (its content fingerprint); null while there is no file. */
  revision: string | null;
  fileState: FileState;
  hasComments: boolean;
  unreadable: boolean;
  draft: SetupDraft;
  preserved: PreservedSection[];
  fileIssues: SetupIssue[];
}

export function parseDraftView(raw: unknown): DraftView {
  const r = object(raw, 'payload');
  return {
    revision: optText(r, 'revision', 'payload'),
    fileState: oneOf(FILE_STATES, field(r, 'fileState', 'payload'), 'fileState'),
    hasComments: bool(field(r, 'hasComments', 'payload'), 'hasComments'),
    unreadable: bool(field(r, 'unreadable', 'payload'), 'unreadable'),
    draft: parseDraft(field(r, 'draft', 'payload')),
    preserved: list(field(r, 'preserved', 'payload'), 'preserved', (v, p) => {
      const x = object(v, p);
      return { path: text(field(x, 'path', p), `${p}.path`), summary: text(field(x, 'summary', p), `${p}.summary`) };
    }),
    fileIssues: list(field(r, 'fileIssues', 'payload'), 'fileIssues', parseIssue),
  };
}

export interface ReservePhase {
  phase: PhaseName;
  reserveW: number;
  minimumSafeW: number;
  refreshS: number;
  refreshDetected: boolean;
  belowMinimum: boolean;
}

/** The kettle arithmetic of 04 §1 with the user's own numbers (CONN-04). */
export interface ReserveExplanation {
  reserveW: number;
  phaseW: number;
  continuousA: number;
  allocatableW: number;
  referenceStepW: number;
  worstCaseA: number;
  multiple: number;
  reaction: TierReaction;
  /** Seconds the tier table tolerates the multiple; null for unlimited. */
  allowedS: number | null;
}

export interface SelfTestVerdict {
  field: string;
  device: string | null;
  stateEntity: string;
  verdict: PhaseStepVerdict;
  observedPhase: PhaseName | null;
  reason: InconclusiveReason | null;
  confirmed: number;
  mismatched: number;
  inconclusive: number;
}

export interface Validation {
  valid: boolean;
  issues: SetupIssue[];
  checks: {
    phaseSum: { state: (typeof PHASE_SUM_STATES)[number]; residual: number | null; tolerance: number | null; samples: number; unit: string | null; reason: string | null };
    refresh: { field: string; detectedS: number | null; maxAgeS: number | null; state: (typeof REFRESH_CHECK_STATES)[number]; suggestedMaxAgeS: number | null }[];
  };
  reserve: { perPhase: ReservePhase[]; explanation: ReserveExplanation } | null;
  selfTest: SelfTestVerdict[];
}

const phaseName = (raw: unknown, path: string) => oneOf(PHASES, raw, path);

export function parseValidation(raw: unknown): Validation {
  const r = object(raw, 'payload');
  const checks = object(field(r, 'checks', 'payload'), 'checks');
  const sum = object(field(checks, 'phaseSum', 'checks'), 'checks.phaseSum');
  return {
    valid: bool(field(r, 'valid', 'payload'), 'valid'),
    issues: list(field(r, 'issues', 'payload'), 'issues', parseIssue),
    checks: {
      phaseSum: {
        state: oneOf(PHASE_SUM_STATES, field(sum, 'state', 'checks.phaseSum'), 'checks.phaseSum.state'),
        residual: optNumber(sum, 'residual', 'checks.phaseSum'),
        tolerance: optNumber(sum, 'tolerance', 'checks.phaseSum'),
        samples: count(field(sum, 'samples', 'checks.phaseSum'), 'checks.phaseSum.samples'),
        unit: optText(sum, 'unit', 'checks.phaseSum'),
        reason: optText(sum, 'reason', 'checks.phaseSum'),
      },
      refresh: list(field(checks, 'refresh', 'checks'), 'checks.refresh', (v, p) => {
        const x = object(v, p);
        return {
          field: text(field(x, 'field', p), `${p}.field`),
          detectedS: optNumber(x, 'detectedS', p),
          maxAgeS: optNumber(x, 'maxAgeS', p),
          state: oneOf(REFRESH_CHECK_STATES, field(x, 'state', p), `${p}.state`),
          suggestedMaxAgeS: optNumber(x, 'suggestedMaxAgeS', p),
        };
      }),
    },
    reserve: nullable(field(r, 'reserve', 'payload'), 'reserve', (v, p) => {
      const x = object(v, p);
      const e = object(field(x, 'explanation', p), `${p}.explanation`);
      const ep = `${p}.explanation`;
      const n = (key: string) => number(field(e, key, ep), `${ep}.${key}`);
      return {
        perPhase: list(field(x, 'perPhase', p), `${p}.perPhase`, (pv, pp) => {
          const y = object(pv, pp);
          return {
            phase: phaseName(field(y, 'phase', pp), `${pp}.phase`),
            reserveW: number(field(y, 'reserveW', pp), `${pp}.reserveW`),
            minimumSafeW: number(field(y, 'minimumSafeW', pp), `${pp}.minimumSafeW`),
            refreshS: number(field(y, 'refreshS', pp), `${pp}.refreshS`),
            refreshDetected: bool(field(y, 'refreshDetected', pp), `${pp}.refreshDetected`),
            belowMinimum: bool(field(y, 'belowMinimum', pp), `${pp}.belowMinimum`),
          };
        }),
        explanation: {
          reserveW: n('reserveW'),
          phaseW: n('phaseW'),
          continuousA: n('continuousA'),
          allocatableW: n('allocatableW'),
          referenceStepW: n('referenceStepW'),
          worstCaseA: n('worstCaseA'),
          multiple: n('multiple'),
          reaction: oneOf(TIER_REACTIONS, field(e, 'reaction', ep), `${ep}.reaction`),
          allowedS: optNumber(e, 'allowedS', ep),
        },
      };
    }),
    selfTest: list(field(r, 'selfTest', 'payload'), 'selfTest', (v, p) => {
      const x = object(v, p);
      return {
        field: text(field(x, 'field', p), `${p}.field`),
        device: optText(x, 'device', p),
        stateEntity: text(field(x, 'stateEntity', p), `${p}.stateEntity`),
        verdict: oneOf(PHASE_STEP_VERDICTS, field(x, 'verdict', p), `${p}.verdict`),
        observedPhase: nullable(field(x, 'observedPhase', p), `${p}.observedPhase`, phaseName),
        reason: nullable(field(x, 'reason', p), `${p}.reason`, (rv, rp) => oneOf(INCONCLUSIVE_REASONS, rv, rp)),
        confirmed: count(field(x, 'confirmed', p), `${p}.confirmed`),
        mismatched: count(field(x, 'mismatched', p), `${p}.mismatched`),
        inconclusive: count(field(x, 'inconclusive', p), `${p}.inconclusive`),
      };
    }),
  };
}

export type SaveOutcome =
  | { kind: 'saved'; revision: string; backup: string | null; reloaded: boolean; loadedStatus: (typeof LOADED_STATUSES)[number] | null }
  | { kind: 'refused'; issues: SetupIssue[] }
  | { kind: 'conflict'; currentRevision: string | null; issues: SetupIssue[] };

export function parseSaved(raw: unknown): SaveOutcome {
  const r = object(raw, 'payload');
  return {
    kind: 'saved',
    revision: text(field(r, 'revision', 'payload'), 'revision'),
    backup: optText(r, 'backup', 'payload'),
    reloaded: bool(field(r, 'reloaded', 'payload'), 'reloaded'),
    loadedStatus: nullable(field(r, 'loadedStatus', 'payload'), 'loadedStatus', (v, p) => oneOf(LOADED_STATUSES, v, p)),
  };
}

/** A 422 (refused) or 409 (conflict) answer of POST api/setup/save. */
export function parseRefusal(raw: unknown, status: number): SaveOutcome {
  const r = object(raw, 'payload');
  const issues = list(field(r, 'issues', 'payload'), 'issues', parseIssue);
  const code = text(field(r, 'code', 'payload'), 'code');
  const currentRevision = optText(r, 'currentRevision', 'payload');
  if (status === 409) {
    if (code !== 'revision_conflict') fail('code', 'revision_conflict');
    return { kind: 'conflict', currentRevision, issues };
  }
  return { kind: 'refused', issues };
}

// ---------------------------------------------------------------------------------------------
// Discovery and readouts (UI-03, UI-04).

export interface DiscoveredEntity {
  entityId: string;
  domain: string;
  name: string | null;
  deviceClass: string | null;
  stateClass: string | null;
  unit: string | null;
  platform: string | null;
  deviceId: string | null;
  disabled: boolean;
  hidden: boolean;
  /** False for an entity defined in YAML without a unique_id: its source cannot be verified. */
  inRegistry: boolean;
  state: string | null;
  lastReported: Date | null;
  /** Set when the entity is computed from other sensors: never a control input. */
  derived: { platform: string } | null;
}

export interface Discovery {
  takenAt: Date;
  registryAvailable: boolean;
  truncated: boolean;
  entities: DiscoveredEntity[];
  devices: { id: string; name: string | null; manufacturer: string | null; model: string | null }[];
}

export function parseDiscovery(raw: unknown): Discovery {
  const r = object(raw, 'payload');
  return {
    takenAt: instant(field(r, 'takenAt', 'payload'), 'takenAt'),
    registryAvailable: bool(field(r, 'registryAvailable', 'payload'), 'registryAvailable'),
    truncated: bool(field(r, 'truncated', 'payload'), 'truncated'),
    entities: list(field(r, 'entities', 'payload'), 'entities', (v, p) => {
      const x = object(v, p);
      return {
        entityId: text(field(x, 'entityId', p), `${p}.entityId`),
        domain: text(field(x, 'domain', p), `${p}.domain`),
        name: optText(x, 'name', p),
        deviceClass: optText(x, 'deviceClass', p),
        stateClass: optText(x, 'stateClass', p),
        unit: optText(x, 'unit', p),
        platform: optText(x, 'platform', p),
        deviceId: optText(x, 'deviceId', p),
        disabled: bool(field(x, 'disabled', p), `${p}.disabled`),
        hidden: bool(field(x, 'hidden', p), `${p}.hidden`),
        inRegistry: bool(field(x, 'inRegistry', p), `${p}.inRegistry`),
        state: optText(x, 'state', p),
        lastReported: nullable(field(x, 'lastReported', p), `${p}.lastReported`, instant),
        derived: nullable(field(x, 'derived', p), `${p}.derived`, (dv, dp) => ({ platform: text(field(object(dv, dp), 'platform', dp), `${dp}.platform`) })),
      };
    }),
    devices: list(field(r, 'devices', 'payload'), 'devices', (v, p) => {
      const x = object(v, p);
      return { id: text(field(x, 'id', p), `${p}.id`), name: optText(x, 'name', p), manufacturer: optText(x, 'manufacturer', p), model: optText(x, 'model', p) };
    }),
  };
}

export interface Readout {
  entityId: string;
  status: ReadoutStatus;
  state: string | null;
  unit: string | null;
  lastReported: Date | null;
  /** Age at the answer's `at`, on the add-on's clock. */
  ageS: number | null;
  refresh: { intervalS: number | null; samples: number; needed: number };
}

export interface Readouts {
  at: Date;
  items: Readout[];
}

export function parseReadouts(raw: unknown): Readouts {
  const r = object(raw, 'payload');
  return {
    at: instant(field(r, 'at', 'payload'), 'at'),
    items: list(field(r, 'items', 'payload'), 'items', (v, p) => {
      const x = object(v, p);
      const refresh = object(field(x, 'refresh', p), `${p}.refresh`);
      return {
        entityId: text(field(x, 'entityId', p), `${p}.entityId`),
        status: oneOf(READOUT_STATUSES, field(x, 'status', p), `${p}.status`),
        state: optText(x, 'state', p),
        unit: optText(x, 'unit', p),
        lastReported: nullable(field(x, 'lastReported', p), `${p}.lastReported`, instant),
        ageS: optNumber(x, 'ageS', p),
        refresh: {
          intervalS: optNumber(refresh, 'intervalS', `${p}.refresh`),
          samples: count(field(refresh, 'samples', `${p}.refresh`), `${p}.refresh.samples`),
          needed: count(field(refresh, 'needed', `${p}.refresh`), `${p}.refresh.needed`),
        },
      };
    }),
  };
}

// ---------------------------------------------------------------------------------------------
// Templates (UI-02).

export interface Template {
  id: string;
  type: string;
  /** Draft fields the card sets (written to the file). */
  prefill: Record<string, string | number>;
  /** Inherited from the control defaults (shown, not written). */
  inherited: { reactionTime: string; minOn: string; minOff: string };
  actuatorDomains: string[];
  stateDomains: string[];
  drawRecommended: boolean;
}

export function parseTemplates(raw: unknown): { templates: Template[]; prefillVoltageV: number } {
  const r = object(raw, 'payload');
  return {
    templates: list(field(r, 'templates', 'payload'), 'templates', (v, p) => {
      const x = object(v, p);
      const prefill = object(field(x, 'prefill', p), `${p}.prefill`);
      const inherited = object(field(x, 'inherited', p), `${p}.inherited`);
      return {
        id: text(field(x, 'id', p), `${p}.id`),
        type: text(field(x, 'type', p), `${p}.type`),
        prefill: Object.fromEntries(
          Object.entries(prefill).map(([key, value]) => [key, typeof value === 'number' ? number(value, `${p}.prefill.${key}`) : text(value, `${p}.prefill.${key}`)]),
        ),
        inherited: {
          reactionTime: text(field(inherited, 'reactionTime', `${p}.inherited`), `${p}.inherited.reactionTime`),
          minOn: text(field(inherited, 'minOn', `${p}.inherited`), `${p}.inherited.minOn`),
          minOff: text(field(inherited, 'minOff', `${p}.inherited`), `${p}.inherited.minOff`),
        },
        actuatorDomains: list(field(x, 'actuatorDomains', p), `${p}.actuatorDomains`, text),
        stateDomains: list(field(x, 'stateDomains', p), `${p}.stateDomains`, text),
        drawRecommended: bool(field(x, 'drawRecommended', p), `${p}.drawRecommended`),
      };
    }),
    prefillVoltageV: number(field(r, 'prefillVoltageV', 'payload'), 'prefillVoltageV'),
  };
}

// ---------------------------------------------------------------------------------------------
// The passive phase self-test (CONN-05): Switchboard switches nothing, it watches.

export interface Observation {
  at: Date;
  transition: 'on' | 'off';
  /** The step of each phase that reported, import positive. */
  deltas: Partial<Record<PhaseName, number>>;
  unit: string;
  verdict: PhaseStepVerdict;
  observedPhase: PhaseName | null;
  reason: InconclusiveReason | null;
  ratingRatio: number | null;
  source: ObservationSource;
  houseLoad: boolean;
  note: string | null;
}

export interface StepSummary {
  verdict: PhaseStepVerdict;
  observedPhase: PhaseName | null;
  reason: InconclusiveReason | null;
  confirmed: number;
  mismatched: number;
  inconclusive: number;
}

export interface SelfTest {
  id: string;
  status: SelfTestStatus;
  startedAt: Date;
  expiresAt: Date;
  expected: { phase: PhaseName; rating: number; unit: string; stateEntity: string };
  observations: Observation[];
  verdict: StepSummary;
}

export function parseObservation(raw: unknown, path: string): Observation {
  const r = object(raw, path);
  const deltas = object(field(r, 'deltas', path), `${path}.deltas`);
  return {
    at: instant(field(r, 'at', path), `${path}.at`),
    transition: oneOf(['on', 'off'] as const, field(r, 'transition', path), `${path}.transition`),
    deltas: Object.fromEntries(Object.entries(deltas).map(([key, value]) => [phaseName(key, `${path}.deltas`), number(value, `${path}.deltas.${key}`)])),
    unit: text(field(r, 'unit', path), `${path}.unit`),
    verdict: oneOf(PHASE_STEP_VERDICTS, field(r, 'verdict', path), `${path}.verdict`),
    observedPhase: nullable(field(r, 'observedPhase', path), `${path}.observedPhase`, phaseName),
    reason: nullable(field(r, 'reason', path), `${path}.reason`, (v, p) => oneOf(INCONCLUSIVE_REASONS, v, p)),
    ratingRatio: optNumber(r, 'ratingRatio', path),
    source: oneOf(OBSERVATION_SOURCES, field(r, 'source', path), `${path}.source`),
    houseLoad: bool(field(r, 'houseLoad', path), `${path}.houseLoad`),
    note: optText(r, 'note', path),
  };
}

export function parseSummary(raw: unknown, path: string): StepSummary {
  const r = object(raw, path);
  return {
    verdict: oneOf(PHASE_STEP_VERDICTS, field(r, 'verdict', path), `${path}.verdict`),
    observedPhase: nullable(field(r, 'observedPhase', path), `${path}.observedPhase`, phaseName),
    reason: nullable(field(r, 'reason', path), `${path}.reason`, (v, p) => oneOf(INCONCLUSIVE_REASONS, v, p)),
    confirmed: count(field(r, 'confirmed', path), `${path}.confirmed`),
    mismatched: count(field(r, 'mismatched', path), `${path}.mismatched`),
    inconclusive: count(field(r, 'inconclusive', path), `${path}.inconclusive`),
  };
}

export function parseSelfTestStarted(raw: unknown): { id: string; status: SelfTestStatus; expiresAt: Date } {
  const r = object(raw, 'payload');
  return {
    id: text(field(r, 'id', 'payload'), 'id'),
    status: oneOf(SELF_TEST_STATUSES, field(r, 'status', 'payload'), 'status'),
    expiresAt: instant(field(r, 'expiresAt', 'payload'), 'expiresAt'),
  };
}

export function parseSelfTest(raw: unknown): SelfTest {
  const r = object(raw, 'payload');
  const e = object(field(r, 'expected', 'payload'), 'expected');
  return {
    id: text(field(r, 'id', 'payload'), 'id'),
    status: oneOf(SELF_TEST_STATUSES, field(r, 'status', 'payload'), 'status'),
    startedAt: instant(field(r, 'startedAt', 'payload'), 'startedAt'),
    expiresAt: instant(field(r, 'expiresAt', 'payload'), 'expiresAt'),
    expected: {
      phase: phaseName(field(e, 'phase', 'expected'), 'expected.phase'),
      rating: number(field(e, 'rating', 'expected'), 'expected.rating'),
      unit: text(field(e, 'unit', 'expected'), 'expected.unit'),
      stateEntity: text(field(e, 'stateEntity', 'expected'), 'expected.stateEntity'),
    },
    observations: list(field(r, 'observations', 'payload'), 'observations', parseObservation),
    verdict: parseSummary(field(r, 'verdict', 'payload'), 'verdict'),
  };
}

export interface DeviceEvidence {
  id: string;
  stateEntity: string;
  phase: PhaseName;
  observations: Observation[];
  verdict: StepSummary;
}

export function parseEvidence(raw: unknown): DeviceEvidence[] {
  const r = object(raw, 'payload');
  return list(field(r, 'devices', 'payload'), 'devices', (v, p) => {
    const x = object(v, p);
    return {
      id: text(field(x, 'id', p), `${p}.id`),
      stateEntity: text(field(x, 'stateEntity', p), `${p}.stateEntity`),
      phase: phaseName(field(x, 'phase', p), `${p}.phase`),
      observations: list(field(x, 'observations', p), `${p}.observations`, parseObservation),
      verdict: parseSummary(field(x, 'verdict', p), `${p}.verdict`),
    };
  });
}

// ---------------------------------------------------------------------------------------------
// Requests. Every POST is application/json: a cross-site form cannot send one without a preflight.

async function getJson(url: string, signal: AbortSignal): Promise<unknown> {
  const response = await fetch(url, { signal, cache: 'no-store', headers: { Accept: 'application/json' } });
  if (!response.ok) throw new Error(`${url} failed with HTTP ${response.status}.`);
  return response.json();
}

async function postJson(url: string, body: unknown, signal?: AbortSignal): Promise<Response> {
  return fetch(url, {
    method: 'POST',
    signal,
    cache: 'no-store',
    headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

/** Thrown when Home Assistant cannot be reached (HTTP 503 with its code). */
export class DiscoveryUnavailable extends Error {
  constructor(readonly code: string) {
    super(`Discovery unavailable: ${code}.`);
  }
}

export async function fetchDiscovery(signal: AbortSignal, refresh = false): Promise<Discovery> {
  const response = await fetch(refresh ? 'api/setup/discovery?refresh=true' : 'api/setup/discovery', { signal, cache: 'no-store', headers: { Accept: 'application/json' } });
  if (response.status === 503) {
    const body = object(await response.json(), 'payload');
    throw new DiscoveryUnavailable(text(field(body, 'code', 'payload'), 'code'));
  }
  if (!response.ok) throw new Error(`Discovery failed with HTTP ${response.status}.`);
  return parseDiscovery(await response.json());
}

export async function fetchReadouts(signal: AbortSignal, entities: readonly string[]): Promise<Readouts> {
  const query = new URLSearchParams();
  for (const entity of entities) query.append('entity', entity);
  return parseReadouts(await getJson(`api/setup/readouts?${query.toString()}`, signal));
}

export async function fetchDraft(signal: AbortSignal): Promise<DraftView> {
  return parseDraftView(await getJson('api/setup/draft', signal));
}

export async function fetchTemplates(signal: AbortSignal): Promise<{ templates: Template[]; prefillVoltageV: number }> {
  return parseTemplates(await getJson('api/setup/templates', signal));
}

export async function validateDraft(draft: SetupDraft, baseRevision: string | null, signal?: AbortSignal): Promise<Validation> {
  const response = await postJson('api/setup/validate', { draft, baseRevision }, signal);
  if (!response.ok) throw new Error(`Validation failed with HTTP ${response.status}.`);
  return parseValidation(await response.json());
}

export async function saveDraft(draft: SetupDraft, baseRevision: string | null, signal?: AbortSignal): Promise<SaveOutcome> {
  const response = await postJson('api/setup/save', { draft, baseRevision }, signal);
  if (response.ok) return parseSaved(await response.json());
  if (response.status === 409 || response.status === 422) return parseRefusal(await response.json(), response.status);
  throw new Error(`Save failed with HTTP ${response.status}.`);
}

export type SelfTestTarget = { device: { index: number } } | { testLoad: { stateEntity: string; phase: PhaseName; ratedPowerW: number } };

export type SelfTestStart =
  | { kind: 'started'; id: string; status: SelfTestStatus; expiresAt: Date }
  | { kind: 'refused'; issues: SetupIssue[] }
  | { kind: 'busy'; id: string };

export async function startSelfTest(draft: SetupDraft, target: SelfTestTarget, signal?: AbortSignal): Promise<SelfTestStart> {
  const response = await postJson('api/setup/self-test', { draft, ...target }, signal);
  if (response.status === 202) return { kind: 'started', ...parseSelfTestStarted(await response.json()) };
  if (response.status === 422) return { kind: 'refused', issues: list(field(object(await response.json(), 'payload'), 'issues', 'payload'), 'issues', parseIssue) };
  if (response.status === 409) return { kind: 'busy', id: text(field(object(await response.json(), 'payload'), 'id', 'payload'), 'id') };
  throw new Error(`Self-test failed with HTTP ${response.status}.`);
}

export async function fetchSelfTest(signal: AbortSignal, id: string): Promise<SelfTest> {
  return parseSelfTest(await getJson(`api/setup/self-test/${encodeURIComponent(id)}`, signal));
}

export async function cancelSelfTest(id: string): Promise<void> {
  const response = await fetch(`api/setup/self-test/${encodeURIComponent(id)}`, { method: 'DELETE', cache: 'no-store' });
  if (!response.ok && response.status !== 404) throw new Error(`Cancelling the self-test failed with HTTP ${response.status}.`);
}

export async function fetchEvidence(signal: AbortSignal): Promise<DeviceEvidence[]> {
  return parseEvidence(await getJson('api/setup/evidence', signal));
}

/** The draft as an empty clean install starts it (nothing mapped, nothing acknowledged). */
export function emptyDraft(): SetupDraft {
  return {
    connection: { topology: null, breakerRatingA: null, characteristic: null, voltageV: null, reserveW: null },
    meter: { currentSign: null, currentDirection: null, powerSign: null, phases: { L1: null, L2: null, L3: null }, total: { power: null, current: null } },
    sources: { inverter: null },
    devices: [],
    acknowledgements: { notProtective: false, commentsDropped: false, replaceUnreadable: false },
    dropPreserved: [],
  };
}

// ---------------------------------------------------------------------------------------------
// The active phase self-test (CONN-05): Switchboard switches one declared, idle relay stage on and
// off through the effector gate, below protection, only in effective active mode. Refused with the
// reasons otherwise; aborted the moment protection acts anywhere.

export interface ActiveTestRefusal {
  code: ActiveTestRefusalCode;
  /** The phase concerned (headroom_insufficient). */
  phase: PhaseName | null;
  /** Seconds until it ends (lockout_min_off). */
  remainingS: number | null;
}

export interface ActiveTestPreconditions {
  ok: boolean;
  refusals: ActiveTestRefusal[];
  expectedDurationS: number | null;
}

export interface ActiveTest {
  id: string;
  device: string;
  stage: number;
  stateEntity: string;
  expectedPhase: PhaseName;
  state: ActiveTestState;
  phase: ActiveTestPhase;
  startedAt: Date;
  onAt: Date | null;
  offAt: Date | null;
  expectedDurationS: number;
  /** The classifier's verdict over the test's own on and off; null until judged. */
  verdict: StepSummary | null;
  observations: Observation[];
  abortReason: ActiveTestAbortReason | null;
}

export type ActiveTestStart = { kind: 'started'; id: string; expectedDurationS: number } | { kind: 'refused'; refusals: ActiveTestRefusal[] };

function activeTestRefusal(raw: unknown, path: string): ActiveTestRefusal {
  const r = object(raw, path);
  return {
    code: oneOf(ACTIVE_TEST_REFUSALS, field(r, 'code', path), `${path}.code`),
    phase: nullable(field(r, 'phase', path), `${path}.phase`, phaseName),
    remainingS: optNumber(r, 'remainingS', path),
  };
}

export function parseActiveTestPreconditions(raw: unknown): ActiveTestPreconditions {
  const r = object(raw, 'payload');
  const preconditions = {
    ok: bool(field(r, 'ok', 'payload'), 'ok'),
    refusals: list(field(r, 'refusals', 'payload'), 'refusals', activeTestRefusal),
    expectedDurationS: optNumber(r, 'expectedDurationS', 'payload'),
  };
  // A test that may start has no refusal, and one that may not names at least one.
  if (preconditions.ok !== (preconditions.refusals.length === 0)) fail('refusals', 'empty exactly when ok');
  return preconditions;
}

export function parseActiveTestStarted(raw: unknown): { id: string; expectedDurationS: number } {
  const r = object(raw, 'payload');
  return { id: text(field(r, 'id', 'payload'), 'id'), expectedDurationS: number(field(r, 'expectedDurationS', 'payload'), 'expectedDurationS') };
}

export function parseActiveTestRefused(raw: unknown): ActiveTestRefusal[] {
  const r = object(raw, 'payload');
  oneOf(['refused'] as const, field(r, 'code', 'payload'), 'code');
  const refusals = list(field(r, 'refusals', 'payload'), 'refusals', activeTestRefusal);
  if (refusals.length === 0) fail('refusals', 'at least one refusal');
  return refusals;
}

export function parseActiveTest(raw: unknown): ActiveTest {
  const r = object(raw, 'payload');
  const test: ActiveTest = {
    id: text(field(r, 'id', 'payload'), 'id'),
    device: text(field(r, 'device', 'payload'), 'device'),
    stage: positive(field(r, 'stage', 'payload'), 'stage'),
    stateEntity: text(field(r, 'stateEntity', 'payload'), 'stateEntity'),
    expectedPhase: phaseName(field(r, 'expectedPhase', 'payload'), 'expectedPhase'),
    state: oneOf(ACTIVE_TEST_STATES, field(r, 'state', 'payload'), 'state'),
    phase: oneOf(ACTIVE_TEST_PHASES, field(r, 'phase', 'payload'), 'phase'),
    startedAt: instant(field(r, 'startedAt', 'payload'), 'startedAt'),
    onAt: nullable(field(r, 'onAt', 'payload'), 'onAt', instant),
    offAt: nullable(field(r, 'offAt', 'payload'), 'offAt', instant),
    expectedDurationS: number(field(r, 'expectedDurationS', 'payload'), 'expectedDurationS'),
    verdict: nullable(field(r, 'verdict', 'payload'), 'verdict', parseSummary),
    observations: list(field(r, 'observations', 'payload'), 'observations', parseObservation),
    abortReason: nullable(field(r, 'abortReason', 'payload'), 'abortReason', (v, p) => oneOf(ACTIVE_TEST_ABORT_REASONS, v, p)),
  };
  // An aborted test always says why, and nothing else carries a reason.
  if ((test.state === 'aborted') !== (test.abortReason !== null)) fail('abortReason', 'present exactly for an aborted test');
  return test;
}

export async function fetchActiveTestPreconditions(device: string, stage: number, signal?: AbortSignal): Promise<ActiveTestPreconditions> {
  const query = new URLSearchParams({ device, stage: String(stage) });
  const response = await fetch(`api/setup/active-test/preconditions?${query.toString()}`, { signal, cache: 'no-store', headers: { Accept: 'application/json' } });
  if (!response.ok) throw new Error(`Active self-test preconditions failed with HTTP ${response.status}.`);
  return parseActiveTestPreconditions(await response.json());
}

/** POST api/setup/active-test as JSON (a cross-site form cannot send it without a preflight). */
export async function startActiveTest(device: string, stage: number, signal?: AbortSignal): Promise<ActiveTestStart> {
  const response = await fetch('api/setup/active-test', {
    method: 'POST',
    signal,
    cache: 'no-store',
    headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
    body: JSON.stringify({ device, stage }),
  });
  if (response.status === 202) return { kind: 'started', ...parseActiveTestStarted(await response.json()) };
  if (response.status === 409) return { kind: 'refused', refusals: parseActiveTestRefused(await response.json()) };
  throw new Error(`Starting the active self-test failed with HTTP ${response.status}.`);
}

export async function fetchActiveTest(id: string, signal?: AbortSignal): Promise<ActiveTest> {
  const response = await fetch(`api/setup/active-test/${encodeURIComponent(id)}`, { signal, cache: 'no-store', headers: { Accept: 'application/json' } });
  if (!response.ok) throw new Error(`Active self-test request failed with HTTP ${response.status}.`);
  return parseActiveTest(await response.json());
}

/** DELETE api/setup/active-test/{id}: the test switches its stage off again and ends as cancelled. */
export async function cancelActiveTest(id: string, signal?: AbortSignal): Promise<boolean> {
  const response = await fetch(`api/setup/active-test/${encodeURIComponent(id)}`, { method: 'DELETE', signal, cache: 'no-store' });
  if (response.status === 204) return true;
  if (response.status === 404) return false;
  throw new Error(`Cancelling the active self-test failed with HTTP ${response.status}.`);
}
