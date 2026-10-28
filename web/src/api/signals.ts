import { array, bool, count, fail, field, instant, list, nullable, number, object, oneOf, text } from './strict';

// Contract of GET api/signals (src/Switchboard.Host/Web/SignalsEndpoint.cs). Every closed list below
// mirrors a backend enum or code list; SignalsSpaContractTests (tests/Switchboard.Tests) compares
// them, so a value added on one side only fails the build instead of blanking this page.

export const SOURCE_KINDS = ['home_assistant', 'replay'] as const;
export const SOURCE_STATUSES = ['connected', 'connecting', 'disconnected'] as const;
export const CONFIGURATION_STATES = ['valid', 'invalid', 'missing'] as const;
export const ISSUE_CODES = [
  'file_missing',
  'file_unreadable',
  'file_too_large',
  'yaml_syntax',
  'yaml_alias_not_allowed',
  'yaml_tag_not_allowed',
  'yaml_multiple_documents',
  'duplicate_key',
  'unknown_key',
  'missing_key',
  'key_not_allowed',
  'wrong_type',
  'invalid_value',
  'out_of_range',
  'unsupported_schema_version',
  'invalid_signal_name',
  'invalid_entity_id',
  'duplicate_entity',
  'unit_not_allowed_for_kind',
  'sign_required',
  'sign_not_allowed_for_kind',
  'direction_from_required',
  'direction_from_not_allowed',
  'direction_from_unknown_signal',
  'direction_from_not_signed_power',
  'direction_from_phase_mismatch',
  'role_kind_mismatch',
  'role_not_in_topology',
  'phase_required',
  'phase_not_allowed',
  'phase_not_in_topology',
  'duplicate_role',
  'required_signal_missing',
  'invalid_duration',
  'reserve_out_of_range',
  'tier_bands_invalid',
  'tier_envelope_violated',
  'invalid_device_id',
  'duplicate_device_id',
  'actuator_domain_not_allowed',
  'state_entity_domain_not_allowed',
  'duplicate_actuator',
  'modulating_range_invalid',
  'sensor_domain_required',
  'rotation_group_mismatch',
  'unknown_circuit_group',
  'duplicate_zone_id',
  'dynamic_price_not_supported',
  'zone_gap',
  'price_fallback_missing',
  'series_value_required',
  'reserved_key_ignored',
  'direction_unavailable_cautious',
  'group_rating_above_main',
  'holidays_list_stale',
  'sources_unusable',
  'value_follows_without_price',
  'unit_mismatch',
  'price_signal_unused',
  'goal_without_start',
  'goal_dropped',

  'forecast_signal_unused',
  'curve_without_signal',
  'max_age_short_for_probe',
] as const;
/** Warnings of the runtime derived-sensor check of the configuration in force: never a safe state, never a refusal. */
export const CONFIGURATION_WARNING_CODES = ['derived_measurement_input', 'measurement_not_in_registry', 'derived_check_unavailable', 'own_entity_actuator'] as const;
export type ConfigurationWarningCode = (typeof CONFIGURATION_WARNING_CODES)[number];
export const TOPOLOGIES = ['three_phase', 'three_phase_summed', 'single_phase'] as const;
export const CHARACTERISTICS = ['B', 'C', 'unknown'] as const;
export const PHASES = ['L1', 'L2', 'L3'] as const;
export const PROTECTION_INVALID_REASONS = [
  'configuration_invalid',
  'source_disconnected',
  'current_awaiting',
  'current_missing',
  'current_unavailable',
  'current_stale',
  'current_rejected',
] as const;
export const FLOW_DIRECTIONS = ['import', 'export', 'unknown'] as const;
export const DIRECTION_SOURCES = ['signed_signal', 'deadband', 'pending', 'unavailable', 'not_configured'] as const;
export const SIGNAL_KINDS = ['current', 'power', 'voltage', 'energy_counter', 'state', 'percentage', 'price', 'series', 'energy_forecast', 'timestamp'] as const;
export const SIGNAL_ROLES = [
  'none',
  'phase_current',
  'phase_power',
  'phase_voltage',
  'phase_power_factor',
  'inverter_power',
  'inverter_phase_current',
  'total_current',
  'total_power',
  'device_state',
  'device_current',
  'device_power',
  'group_current',
  'energy_export',
  'energy_import',
  'price_import',
  'price_export',
  'device_soc',
  'goal_target',
  'goal_deadline',
  'goal_want',

  'pv_forecast',
  'pv_forecast_today',
  'pv_forecast_tomorrow',
  'sun_rising',
  'sun_setting',
  'price_curve_import',
  'price_curve_export',
] as const;
export const SIGNAL_STATES = ['fresh', 'stale', 'rejected', 'unavailable', 'unmapped'] as const;
export const SIGNAL_DETAILS = [
  'none',
  'awaiting_first_sample',
  'entity_missing',
  'entity_removed',
  'state_unavailable',
  'state_unknown',
  'not_numeric',
  'unit_mismatch',
  'sign_violation',
  'balance_residual',
  'max_age_exceeded',
  'series_missing',
  'series_malformed',
  'series_too_large',
  'not_timestamp',
] as const;
export const SERIES_VALUE_KINDS = ['power_w', 'energy_wh_period', 'price_per_kwh'] as const;

export type SourceKind = (typeof SOURCE_KINDS)[number];
export type SourceStatus = (typeof SOURCE_STATUSES)[number];
export type ConfigurationState = (typeof CONFIGURATION_STATES)[number];
export type IssueCode = (typeof ISSUE_CODES)[number];
export type Topology = (typeof TOPOLOGIES)[number];
export type Characteristic = (typeof CHARACTERISTICS)[number];
export type PhaseName = (typeof PHASES)[number];
export type ProtectionInvalidReason = (typeof PROTECTION_INVALID_REASONS)[number];
export type FlowDirection = (typeof FLOW_DIRECTIONS)[number];
export type DirectionSource = (typeof DIRECTION_SOURCES)[number];
export type SignalKind = (typeof SIGNAL_KINDS)[number];
export type SignalRole = (typeof SIGNAL_ROLES)[number];
export type SignalState = (typeof SIGNAL_STATES)[number];
export type SignalDetail = (typeof SIGNAL_DETAILS)[number];
export type SeriesValueKind = (typeof SERIES_VALUE_KINDS)[number];

/**
 * The unit the add-on normalizes each numeric kind to; a state signal has none. A price has no fixed one: it travels as
 * `<currency label>/kWh`, the label being the user's `economics.currency` (see PRICE_UNIT).
 */
export const INTERNAL_UNITS = {
  current: 'A',
  power: 'W',
  voltage: 'V',
  energy_counter: 'kWh',
  percentage: '%',
  energy_forecast: 'kWh',
} as const satisfies Record<Exclude<SignalKind, 'state' | 'price' | 'series' | 'timestamp'>, string>;

/** A price value always travels per kWh (the add-on converted a per-MWh unit); the label before the slash is the user's own. */
export const PRICE_UNIT = /^[^/\s][^/]*\/kWh$/;

export type Unit = (typeof INTERNAL_UNITS)[keyof typeof INTERNAL_UNITS];

export interface Issue {
  code: IssueCode;
  path: string;
  line: number | null;
  column: number | null;
  /** English, as the add-on log writes it; shown next to the translated sentence. */
  message: string;
}

export interface ConfigurationInfo {
  state: ConfigurationState;
  path: string;
  loadedAt: Date | null;
  errors: Issue[];
  warnings: Issue[];
}

export interface Connection {
  topology: Topology;
  breakerRatingA: number;
  characteristic: Characteristic;
  voltageV: number;
}

export interface Reserve {
  configuredW: number;
  minimumSafeW: number;
  belowMinimum: boolean;
  refreshS: number;
  refreshDetected: boolean;
  blindTimeS: number;
  tierMultiple: number;
}

export interface Phase {
  phase: PhaseName;
  protectionDataValid: boolean;
  invalidReason: ProtectionInvalidReason | null;
  /** Signed, import positive. */
  currentA: number | null;
  direction: FlowDirection | null;
  netImportW: number | null;
  generationW: number | null;
  loadW: number | null;
  residualW: number | null;
  /** When the balance was last evaluated; the residual is shown with its age. */
  residualAt: Date | null;
  balanceRejections: number;
  reserve: Reserve | null;
}

export interface Direction {
  source: DirectionSource;
  direction: FlowDirection;
  from: string | null;
}

export interface Signal {
  /** Null for an unmapped role slot. */
  name: string | null;
  entity: string | null;
  kind: SignalKind;
  role: SignalRole;
  phase: PhaseName | null;
  state: SignalState;
  detail: SignalDetail;
  /** Text for a state signal, a number in `unit` otherwise. */
  value: number | string | null;
  unit: Unit | string | null;
  raw: { state: string; unit: string | null } | null;
  lastReported: Date | null;
  /** Computed by the add-on when it answered. */
  ageS: number | null;
  maxAgeS: number | null;
  refreshS: number | null;
  refreshIntervals: number;
  direction: Direction | null;
  rejectedCount: number;
  /** Devices held as unsafe while this signal is not fresh (SIG-05). */
  dependentDevices: string[];
  /** What a series signal (a forecast or a price curve) holds, once it has a value; coverage is separate from freshness. */
  series: SeriesInfo | null;
}

export interface SeriesInfo {
  valueKind: SeriesValueKind;
  points: number;
  from: Date | null;
  coversUntil: Date | null;
}

export interface Signals {
  generatedAt: Date;
  snapshotAt: Date;
  sequence: number;
  source: { kind: SourceKind; status: SourceStatus };
  configuration: ConfigurationInfo;
  connection: Connection | null;
  phases: Phase[];
  signals: Signal[];
  rejections: { total: number; balanceResidual: number; invalidSample: number };
  configurationWarnings: ConfigurationWarning[];
}

export interface ConfigurationWarning {
  code: ConfigurationWarningCode;
  /** The logical signal; null for derived_check_unavailable. */
  signal: string | null;
  entity: string | null;
  /** The registry platform of a derived input (template, statistics, …). */
  platform: string | null;
  checkedAt: Date;
}

// Relative on purpose: ingress serves the page under a per-session prefix.
const SIGNALS_URL = 'api/signals';

function issue(raw: unknown, path: string): Issue {
  const r = object(raw, path);
  const position = (key: string) => nullable(field(r, key, path), `${path}.${key}`, count);
  return {
    code: oneOf(ISSUE_CODES, field(r, 'code', path), `${path}.code`),
    path: text(field(r, 'path', path), `${path}.path`),
    line: position('line'),
    column: position('column'),
    message: text(field(r, 'message', path), `${path}.message`),
  };
}

function configuration(raw: unknown, path: string): ConfigurationInfo {
  const r = object(raw, path);
  return {
    state: oneOf(CONFIGURATION_STATES, field(r, 'state', path), `${path}.state`),
    path: text(field(r, 'path', path), `${path}.path`),
    loadedAt: nullable(field(r, 'loadedAt', path), `${path}.loadedAt`, instant),
    errors: array(field(r, 'errors', path), `${path}.errors`).map((e, i) => issue(e, `${path}.errors[${i}]`)),
    warnings: array(field(r, 'warnings', path), `${path}.warnings`).map((w, i) => issue(w, `${path}.warnings[${i}]`)),
  };
}

function connection(raw: unknown, path: string): Connection {
  const r = object(raw, path);
  return {
    topology: oneOf(TOPOLOGIES, field(r, 'topology', path), `${path}.topology`),
    breakerRatingA: number(field(r, 'breakerRatingA', path), `${path}.breakerRatingA`),
    characteristic: oneOf(CHARACTERISTICS, field(r, 'characteristic', path), `${path}.characteristic`),
    voltageV: number(field(r, 'voltageV', path), `${path}.voltageV`),
  };
}

function reserve(raw: unknown, path: string): Reserve {
  const r = object(raw, path);
  const n = (key: string) => number(field(r, key, path), `${path}.${key}`);
  return {
    configuredW: n('configuredW'),
    minimumSafeW: n('minimumSafeW'),
    belowMinimum: bool(field(r, 'belowMinimum', path), `${path}.belowMinimum`),
    refreshS: n('refreshS'),
    refreshDetected: bool(field(r, 'refreshDetected', path), `${path}.refreshDetected`),
    blindTimeS: n('blindTimeS'),
    tierMultiple: n('tierMultiple'),
  };
}

function phase(raw: unknown, path: string): Phase {
  const r = object(raw, path);
  const measured = (key: string) => nullable(field(r, key, path), `${path}.${key}`, number);
  const valid = bool(field(r, 'protectionDataValid', path), `${path}.protectionDataValid`);
  const invalidReason = nullable(field(r, 'invalidReason', path), `${path}.invalidReason`, (v, p) =>
    oneOf(PROTECTION_INVALID_REASONS, v, p),
  );
  // Valid data has no reason and invalid data always has one; anything else is a contract break,
  // and a "valid" next to a reason must never be rendered.
  if (valid !== (invalidReason === null)) fail(`${path}.invalidReason`, 'consistent with protectionDataValid');
  return {
    phase: oneOf(PHASES, field(r, 'phase', path), `${path}.phase`),
    protectionDataValid: valid,
    invalidReason,
    currentA: measured('currentA'),
    direction: nullable(field(r, 'direction', path), `${path}.direction`, (v, p) => oneOf(FLOW_DIRECTIONS, v, p)),
    netImportW: measured('netImportW'),
    generationW: measured('generationW'),
    loadW: measured('loadW'),
    residualW: measured('residualW'),
    residualAt: nullable(field(r, 'residualAt', path), `${path}.residualAt`, instant),
    balanceRejections: count(field(r, 'balanceRejections', path), `${path}.balanceRejections`),
    reserve: nullable(field(r, 'reserve', path), `${path}.reserve`, reserve),
  };
}

function signal(raw: unknown, path: string): Signal {
  const r = object(raw, path);
  const kind = oneOf(SIGNAL_KINDS, field(r, 'kind', path), `${path}.kind`);
  const rawValue = field(r, 'value', path);
  const unit = field(r, 'unit', path);
  let value: number | string | null;
  if (kind === 'state' || kind === 'timestamp') {
    // A timestamp travels as the instant's text (ISO 8601 with its offset), like a state.
    value = nullable(rawValue, `${path}.value`, text);
    if (unit !== null) fail(`${path}.unit`, `null for a ${kind} signal`);
  } else if (kind === 'series') {
    // A series has no single value and no unit; its content is summarized in `series`.
    if (rawValue !== null) fail(`${path}.value`, 'null for a series signal');
    if (unit !== null) fail(`${path}.unit`, 'null for a series signal');
    value = null;
  } else {
    value = nullable(rawValue, `${path}.value`, number);
    if (kind === 'price') {
      // A price travels per kWh in the user's currency label; no number means no unit.
      if (value === null ? unit !== null : typeof unit !== 'string' || !PRICE_UNIT.test(unit)) {
        fail(`${path}.unit`, value === null ? 'null for this price value' : '<currency>/kWh for this price value');
      }
    } else {
      // A number always travels in the kind's internal unit, and no number means no unit.
      const expected = value === null ? null : INTERNAL_UNITS[kind];
      if (unit !== expected) fail(`${path}.unit`, `${expected ?? 'null'} for this ${kind} value`);
    }
  }
  return {
    name: nullable(field(r, 'name', path), `${path}.name`, text),
    entity: nullable(field(r, 'entity', path), `${path}.entity`, text),
    kind,
    role: oneOf(SIGNAL_ROLES, field(r, 'role', path), `${path}.role`),
    phase: nullable(field(r, 'phase', path), `${path}.phase`, (v, p) => oneOf(PHASES, v, p)),
    state: oneOf(SIGNAL_STATES, field(r, 'state', path), `${path}.state`),
    detail: oneOf(SIGNAL_DETAILS, field(r, 'detail', path), `${path}.detail`),
    value,
    unit: unit as Unit | string | null,
    raw: nullable(field(r, 'raw', path), `${path}.raw`, (v, p) => {
      const o = object(v, p);
      return {
        state: text(field(o, 'state', p), `${p}.state`),
        unit: nullable(field(o, 'unit', p), `${p}.unit`, text),
      };
    }),
    lastReported: nullable(field(r, 'lastReported', path), `${path}.lastReported`, instant),
    ageS: nullable(field(r, 'ageS', path), `${path}.ageS`, number),
    maxAgeS: nullable(field(r, 'maxAgeS', path), `${path}.maxAgeS`, number),
    refreshS: nullable(field(r, 'refreshS', path), `${path}.refreshS`, number),
    refreshIntervals: count(field(r, 'refreshIntervals', path), `${path}.refreshIntervals`),
    direction: nullable(field(r, 'direction', path), `${path}.direction`, (v, p) => {
      const o = object(v, p);
      return {
        source: oneOf(DIRECTION_SOURCES, field(o, 'source', p), `${p}.source`),
        direction: oneOf(FLOW_DIRECTIONS, field(o, 'direction', p), `${p}.direction`),
        from: nullable(field(o, 'from', p), `${p}.from`, text),
      };
    }),
    rejectedCount: count(field(r, 'rejectedCount', path), `${path}.rejectedCount`),
    dependentDevices: array(field(r, 'dependentDevices', path), `${path}.dependentDevices`).map((d, i) =>
      text(d, `${path}.dependentDevices[${i}]`),
    ),
    series: nullable(field(r, 'series', path), `${path}.series`, (v, p) => {
      const o = object(v, p);
      return {
        valueKind: oneOf(SERIES_VALUE_KINDS, field(o, 'valueKind', p), `${p}.valueKind`),
        points: count(field(o, 'points', p), `${p}.points`),
        from: nullable(field(o, 'from', p), `${p}.from`, instant),
        coversUntil: nullable(field(o, 'coversUntil', p), `${p}.coversUntil`, instant),
      };
    }),
  };
}

/**
 * Accepts only the exact shape the add-on promises: every key present, every enum value from its
 * closed list, units consistent with the kind. Anything else is an error rather than a guess, so the
 * page never shows a signal state the add-on did not report.
 */
export function parseSignals(raw: unknown): Signals {
  const r = object(raw, 'payload');
  const source = object(field(r, 'source', 'payload'), 'source');
  const rejections = object(field(r, 'rejections', 'payload'), 'rejections');
  return {
    generatedAt: instant(field(r, 'generatedAt', 'payload'), 'generatedAt'),
    snapshotAt: instant(field(r, 'snapshotAt', 'payload'), 'snapshotAt'),
    sequence: count(field(r, 'sequence', 'payload'), 'sequence'),
    source: {
      kind: oneOf(SOURCE_KINDS, field(source, 'kind', 'source'), 'source.kind'),
      status: oneOf(SOURCE_STATUSES, field(source, 'status', 'source'), 'source.status'),
    },
    configuration: configuration(field(r, 'configuration', 'payload'), 'configuration'),
    connection: nullable(field(r, 'connection', 'payload'), 'connection', connection),
    phases: array(field(r, 'phases', 'payload'), 'phases').map((p, i) => phase(p, `phases[${i}]`)),
    signals: array(field(r, 'signals', 'payload'), 'signals').map((s, i) => signal(s, `signals[${i}]`)),
    rejections: {
      total: count(field(rejections, 'total', 'rejections'), 'rejections.total'),
      balanceResidual: count(field(rejections, 'balanceResidual', 'rejections'), 'rejections.balanceResidual'),
      invalidSample: count(field(rejections, 'invalidSample', 'rejections'), 'rejections.invalidSample'),
    },
    configurationWarnings: list(field(r, 'configurationWarnings', 'payload'), 'configurationWarnings', (v, p) => {
      const w = object(v, p);
      return {
        code: oneOf(CONFIGURATION_WARNING_CODES, field(w, 'code', p), `${p}.code`),
        signal: nullable(field(w, 'signal', p), `${p}.signal`, text),
        entity: nullable(field(w, 'entity', p), `${p}.entity`, text),
        platform: nullable(field(w, 'platform', p), `${p}.platform`, text),
        checkedAt: instant(field(w, 'checkedAt', p), `${p}.checkedAt`),
      };
    }),
  };
}

export async function fetchSignals(signal: AbortSignal): Promise<Signals> {
  const response = await fetch(SIGNALS_URL, {
    signal,
    cache: 'no-store',
    headers: { Accept: 'application/json' },
  });
  if (!response.ok) {
    throw new Error(`Signals request failed with HTTP ${response.status}.`);
  }
  return parseSignals(await response.json());
}
