import { DEVICE_TYPES } from './guard';
import type { DeviceType } from './guard';
import { PHASES } from './signals';
import type { PhaseName } from './signals';
import { bool, fail, field, instant, list, nullable, number, object, oneOf, positive, text } from './strict';

// Contract of GET api/devices (src/Switchboard.Host/Web/DevicesEndpoint.cs): every declared device as
// the configuration declares it, for the device card. Entity IDs come from the configuration layer
// and are rendered in monospace. PresentationSpaContractTests compares the closed lists and the keys
// of devices.sample.json with what the endpoint writes.

/** The configuration document: devices are listed only from a valid one. */
export const CONFIGURATION_STATES = ['valid', 'invalid', 'missing'] as const;
/** age: judged by state_max_age; on_change: by availability, with no age limit. */
export const STATE_REPORTS = ['age', 'on_change'] as const;

export type ConfigurationState = (typeof CONFIGURATION_STATES)[number];
export type StateReports = (typeof STATE_REPORTS)[number];

export interface StageConfig {
  stage: number;
  /** The only entity that may ever be commanded for this stage; nothing commands it in this version. */
  actuator: string;
  stateEntity: string;
  phases: PhaseName[];
  ratedCurrentA: number;
  ratedPowerW: number | null;
}

export interface DeviceConfig {
  id: string;
  /** The optional display name from name:. */
  name: string | null;
  type: DeviceType;
  phases: PhaseName[];
  stages: StageConfig[];
  stopActuator: string | null;
  range: { minA: number; maxA: number; stepA: number; unit: string } | null;
  reactionTimeS: number;
  minOnS: number;
  minOffS: number;
  stateReports: StateReports;
  /** null for on_change. */
  stateMaxAgeS: number | null;
  draw: { current: { phase: PhaseName; entity: string }[]; power: { phase: PhaseName; entity: string }[]; maxAgeS: number } | null;
  valuePerKwh: number;
  shedPriority: number | null;
  noShed: boolean;
  rotationGroup: string | null;
  circuitGroup: string | null;
}

export interface Devices {
  at: Date;
  configuration: ConfigurationState;
  devices: DeviceConfig[];
}

const DEVICES_URL = 'api/devices';

const phaseName = (raw: unknown, path: string) => oneOf(PHASES, raw, path);

function entities(raw: unknown, path: string): { phase: PhaseName; entity: string }[] {
  return list(raw, path, (v, p) => {
    const e = object(v, p);
    return { phase: phaseName(field(e, 'phase', p), `${p}.phase`), entity: text(field(e, 'entity', p), `${p}.entity`) };
  });
}

function device(raw: unknown, path: string): DeviceConfig {
  const r = object(raw, path);
  const n = (key: string) => number(field(r, key, path), `${path}.${key}`);
  const stateReports = oneOf(STATE_REPORTS, field(r, 'stateReports', path), `${path}.stateReports`);
  const stateMaxAgeS = nullable(field(r, 'stateMaxAgeS', path), `${path}.stateMaxAgeS`, number);
  // An on-change state has no age limit; an age-based one always has one.
  if ((stateReports === 'on_change') !== (stateMaxAgeS === null)) fail(`${path}.stateMaxAgeS`, 'null exactly for on_change');
  return {
    id: text(field(r, 'id', path), `${path}.id`),
    name: nullable(field(r, 'name', path), `${path}.name`, text),
    type: oneOf(DEVICE_TYPES, field(r, 'type', path), `${path}.type`),
    phases: list(field(r, 'phases', path), `${path}.phases`, phaseName),
    stages: list(field(r, 'stages', path), `${path}.stages`, (v, p) => {
      const s = object(v, p);
      return {
        stage: positive(field(s, 'stage', p), `${p}.stage`),
        actuator: text(field(s, 'actuator', p), `${p}.actuator`),
        stateEntity: text(field(s, 'stateEntity', p), `${p}.stateEntity`),
        phases: list(field(s, 'phases', p), `${p}.phases`, phaseName),
        ratedCurrentA: number(field(s, 'ratedCurrentA', p), `${p}.ratedCurrentA`),
        ratedPowerW: nullable(field(s, 'ratedPowerW', p), `${p}.ratedPowerW`, number),
      };
    }),
    stopActuator: nullable(field(r, 'stopActuator', path), `${path}.stopActuator`, text),
    range: nullable(field(r, 'range', path), `${path}.range`, (v, p) => {
      const g = object(v, p);
      return {
        minA: number(field(g, 'minA', p), `${p}.minA`),
        maxA: number(field(g, 'maxA', p), `${p}.maxA`),
        stepA: number(field(g, 'stepA', p), `${p}.stepA`),
        unit: text(field(g, 'unit', p), `${p}.unit`),
      };
    }),
    reactionTimeS: n('reactionTimeS'),
    minOnS: n('minOnS'),
    minOffS: n('minOffS'),
    stateReports,
    stateMaxAgeS,
    draw: nullable(field(r, 'draw', path), `${path}.draw`, (v, p) => {
      const d = object(v, p);
      return {
        current: entities(field(d, 'current', p), `${p}.current`),
        power: entities(field(d, 'power', p), `${p}.power`),
        maxAgeS: number(field(d, 'maxAgeS', p), `${p}.maxAgeS`),
      };
    }),
    valuePerKwh: n('valuePerKwh'),
    shedPriority: nullable(field(r, 'shedPriority', path), `${path}.shedPriority`, number),
    noShed: bool(field(r, 'noShed', path), `${path}.noShed`),
    rotationGroup: nullable(field(r, 'rotationGroup', path), `${path}.rotationGroup`, text),
    circuitGroup: nullable(field(r, 'circuitGroup', path), `${path}.circuitGroup`, text),
  };
}

/** Accepts only the exact shape the add-on promises; devices are listed only from a valid configuration. */
export function parseDevices(raw: unknown): Devices {
  const r = object(raw, 'payload');
  const configuration = oneOf(CONFIGURATION_STATES, field(r, 'configuration', 'payload'), 'configuration');
  const devices = list(field(r, 'devices', 'payload'), 'devices', device);
  if (configuration !== 'valid' && devices.length > 0) fail('devices', 'empty without a valid configuration');
  return { at: instant(field(r, 'at', 'payload'), 'at'), configuration, devices };
}

export async function fetchDevices(signal: AbortSignal): Promise<Devices> {
  const response = await fetch(DEVICES_URL, { signal, cache: 'no-store', headers: { Accept: 'application/json' } });
  if (!response.ok) throw new Error(`Devices request failed with HTTP ${response.status}.`);
  return parseDevices(await response.json());
}

/** The name a person reads: the display name when one is declared, else the id. */
export function deviceLabel(devices: readonly DeviceConfig[] | null, id: string): string {
  return devices?.find((d) => d.id === id)?.name ?? id;
}
