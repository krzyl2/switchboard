import { STATE_FILE_STATUSES } from './guard';
import type { StateFileStatus } from './guard';
import { bool, count, fail, field, instant, list, nullable, number, object, oneOf, text } from './strict';

// Contract of GET api/statistics (src/Switchboard.Host/Web/StatisticsEndpoint.cs): energy integrated
// per UTC bucket on the add-on's signal clock (never read from the Recorder), the last hour's sums
// and the counters since the statistics began. The page groups buckets into its own local day; it
// may ask for just that range with from/to. PresentationSpaContractTests compares the keys of
// statistics.sample.json with what the endpoint writes.

/** How a presentation file was found at start, and whether it can be written now. */
export interface PresentationFile {
  status: StateFileStatus;
  warning: string | null;
  writeError: string | null;
}

export interface DeviceEnergy {
  id: string;
  measuredKwh: number;
  /** From the rated model (a relay's rating while it reports on): an estimate. */
  modelKwh: number;
  /** Observed time during which its power was not known. */
  unknownS: number;
  pvKwh: number;
  /** Its energy at its value per kWh (no currency: v1 has no tariff). */
  value: number;
}

export interface Pv {
  houseKwh: number;
  storageKwh: number;
  exportKwh: number;
  unattributedKwh: number;
}

export interface Totals {
  /** Time the whole power picture was known; energy is integrated over it only. */
  observedS: number;
  unobservedS: number;
  gridImportKwh: number;
  gridExportKwh: number;
  generationKwh: number;
  loadKwh: number;
  uncontrolledKwh: number;
  storageChargeKwh: number;
  storageDischargeKwh: number;
  pv: Pv;
  devices: DeviceEnergy[];
  sheds: number;
  wouldSheds: number;
  /** Phase-voltage losses on some phases while the others stayed up: possible breaker trips. */
  voltageLosses: number;
  /** Losses on every mapped phase at once: a supply outage, not a trip. */
  supplyOutages: number;
  voltageObservedS: number;
}

export interface Bucket {
  start: Date;
  totals: Totals;
}

export interface Range {
  /** The start of the first bucket summed (the last hour: "since HH:MM"). */
  from: Date;
  to: Date;
  buckets: number;
  totals: Totals;
}

export interface Lifetime {
  since: Date | null;
  sheds: number;
  wouldSheds: number;
  voltageLosses: number;
  supplyOutages: number;
  voltageObservedS: number;
}

export interface Statistics {
  /** The signal clock now (data time in development replay). */
  at: Date;
  bucketMinutes: number;
  stateFile: PresentationFile;
  /** At least one phase voltage is mapped: voltage losses can be seen at all. */
  voltageMeasured: boolean;
  /** Everything before this instant is integrated; null before the first step. */
  integratedTo: Date | null;
  devices: { id: string; name: string | null; valuePerKwh: number }[];
  buckets: Bucket[];
  /** The sums of the requested range; null when none was asked for. */
  range: Range | null;
  lastHour: Range;
  lifetime: Lifetime;
}

const STATISTICS_URL = 'api/statistics';

const nonNegative = (raw: unknown, path: string): number => {
  const value = number(raw, path);
  if (value < 0) fail(path, 'a non-negative number');
  return value;
};

export function parsePresentationFile(raw: unknown, path: string): PresentationFile {
  const r = object(raw, path);
  return {
    status: oneOf(STATE_FILE_STATUSES, field(r, 'status', path), `${path}.status`),
    warning: nullable(field(r, 'warning', path), `${path}.warning`, text),
    writeError: nullable(field(r, 'writeError', path), `${path}.writeError`, text),
  };
}

function deviceEnergy(raw: unknown, path: string): DeviceEnergy {
  const r = object(raw, path);
  const n = (key: string) => nonNegative(field(r, key, path), `${path}.${key}`);
  return {
    id: text(field(r, 'id', path), `${path}.id`),
    measuredKwh: n('measuredKwh'),
    modelKwh: n('modelKwh'),
    unknownS: n('unknownS'),
    pvKwh: n('pvKwh'),
    value: n('value'),
  };
}

function totals(raw: unknown, path: string): Totals {
  const r = object(raw, path);
  const n = (key: string) => nonNegative(field(r, key, path), `${path}.${key}`);
  const c = (key: string) => count(field(r, key, path), `${path}.${key}`);
  const pv = object(field(r, 'pv', path), `${path}.pv`);
  const p = (key: string) => nonNegative(field(pv, key, `${path}.pv`), `${path}.pv.${key}`);
  return {
    observedS: n('observedS'),
    unobservedS: n('unobservedS'),
    gridImportKwh: n('gridImportKwh'),
    gridExportKwh: n('gridExportKwh'),
    generationKwh: n('generationKwh'),
    loadKwh: n('loadKwh'),
    uncontrolledKwh: n('uncontrolledKwh'),
    storageChargeKwh: n('storageChargeKwh'),
    storageDischargeKwh: n('storageDischargeKwh'),
    pv: { houseKwh: p('houseKwh'), storageKwh: p('storageKwh'), exportKwh: p('exportKwh'), unattributedKwh: p('unattributedKwh') },
    devices: list(field(r, 'devices', path), `${path}.devices`, deviceEnergy),
    sheds: c('sheds'),
    wouldSheds: c('wouldSheds'),
    voltageLosses: c('voltageLosses'),
    supplyOutages: c('supplyOutages'),
    voltageObservedS: n('voltageObservedS'),
  };
}

function range(raw: unknown, path: string): Range {
  const r = object(raw, path);
  const from = instant(field(r, 'from', path), `${path}.from`);
  const to = instant(field(r, 'to', path), `${path}.to`);
  if (from > to) fail(path, 'a range whose start is not after its end');
  return { from, to, buckets: count(field(r, 'buckets', path), `${path}.buckets`), totals: totals(field(r, 'totals', path), `${path}.totals`) };
}

function bucket(raw: unknown, path: string): Bucket {
  const r = object(raw, path);
  return { start: instant(field(r, 'start', path), `${path}.start`), totals: totals(field(r, 'totals', path), `${path}.totals`) };
}

/** Accepts only the exact shape the add-on promises; energies and times are never negative, buckets ascend. */
export function parseStatistics(raw: unknown): Statistics {
  const r = object(raw, 'payload');
  const lifetime = object(field(r, 'lifetime', 'payload'), 'lifetime');
  const buckets = list(field(r, 'buckets', 'payload'), 'buckets', bucket);
  buckets.forEach((b, i) => {
    if (i > 0 && b.start <= buckets[i - 1]!.start) fail(`buckets[${i}].start`, 'after the previous bucket');
  });
  const bucketMinutes = number(field(r, 'bucketMinutes', 'payload'), 'bucketMinutes');
  if (bucketMinutes <= 0) fail('bucketMinutes', 'a positive number');
  return {
    at: instant(field(r, 'at', 'payload'), 'at'),
    bucketMinutes,
    stateFile: parsePresentationFile(field(r, 'stateFile', 'payload'), 'stateFile'),
    voltageMeasured: bool(field(r, 'voltageMeasured', 'payload'), 'voltageMeasured'),
    integratedTo: nullable(field(r, 'integratedTo', 'payload'), 'integratedTo', instant),
    devices: list(field(r, 'devices', 'payload'), 'devices', (v, p) => {
      const d = object(v, p);
      return {
        id: text(field(d, 'id', p), `${p}.id`),
        name: nullable(field(d, 'name', p), `${p}.name`, text),
        valuePerKwh: nonNegative(field(d, 'valuePerKwh', p), `${p}.valuePerKwh`),
      };
    }),
    buckets,
    range: nullable(field(r, 'range', 'payload'), 'range', range),
    lastHour: range(field(r, 'lastHour', 'payload'), 'lastHour'),
    lifetime: {
      since: nullable(field(lifetime, 'since', 'lifetime'), 'lifetime.since', instant),
      sheds: count(field(lifetime, 'sheds', 'lifetime'), 'lifetime.sheds'),
      wouldSheds: count(field(lifetime, 'wouldSheds', 'lifetime'), 'lifetime.wouldSheds'),
      voltageLosses: count(field(lifetime, 'voltageLosses', 'lifetime'), 'lifetime.voltageLosses'),
      supplyOutages: count(field(lifetime, 'supplyOutages', 'lifetime'), 'lifetime.supplyOutages'),
      voltageObservedS: nonNegative(field(lifetime, 'voltageObservedS', 'lifetime'), 'lifetime.voltageObservedS'),
    },
  };
}

/** GET api/statistics, optionally only the buckets of [from, to) (UTC instants the page computes from its local day). */
export async function fetchStatistics(signal: AbortSignal, range?: { from: Date; to: Date }): Promise<Statistics> {
  const query = range === undefined ? '' : `?from=${encodeURIComponent(range.from.toISOString())}&to=${encodeURIComponent(range.to.toISOString())}`;
  const response = await fetch(STATISTICS_URL + query, { signal, cache: 'no-store', headers: { Accept: 'application/json' } });
  if (!response.ok) throw new Error(`Statistics request failed with HTTP ${response.status}.`);
  return parseStatistics(await response.json());
}
