import type { DeviceDraft, SetupDraft, SetupIssue } from './api/setup';

// The wizard's shared draft (UI-01, UI-06): the five steps edit one draft, the add-on validates and
// saves it. Fields are addressed by the same dotted paths the add-on reports issues at
// (connection.breakerRatingA, meter.phases.L1.current.entity, devices[1].phase), so a refusal can
// focus exactly the field it names (UI-05).

/** The five steps in the mockup's order (docs/mockups screen 03). */
export const STEPS = ['meter', 'connection', 'sources', 'loads', 'mode'] as const;
export type StepName = (typeof STEPS)[number];

/** The step whose screen shows a field; topology and breaker rating are asked first in step 1. */
export function stepOfField(field: string | null): StepName {
  if (field === null) return 'mode';
  if (field === 'connection.topology' || field === 'connection.breakerRatingA' || field.startsWith('meter')) return 'meter';
  if (field.startsWith('connection')) return 'connection';
  if (field.startsWith('sources')) return 'sources';
  if (field.startsWith('devices')) return 'loads';
  return 'mode';
}

/** The issues a step shows: those at its fields, errors first. */
export function issuesOfStep(issues: readonly SetupIssue[], step: StepName): SetupIssue[] {
  return issues.filter((issue) => stepOfField(issue.field) === step).sort((a, b) => (a.severity === b.severity ? 0 : a.severity === 'error' ? -1 : 1));
}

/** The issues at one field (or inside it: devices[1] includes devices[1].phase). */
export function issuesAt(issues: readonly SetupIssue[], field: string): SetupIssue[] {
  return issues.filter((issue) => issue.field !== null && (issue.field === field || issue.field.startsWith(`${field}.`)));
}

/** The DOM id of a field's input: a refusal focuses it. */
export function fieldId(field: string): string {
  return `setup-${field.replace(/[^A-Za-z0-9]+/g, '-')}`;
}

type Path = (string | number)[];

/** connection.reserveW.L1 → ['connection', 'reserveW', 'L1']; devices[1].phase → ['devices', 1, 'phase']. */
export function parsePath(path: string): Path {
  const parts: Path = [];
  for (const segment of path.split('.')) {
    const match = /^([A-Za-z0-9_]+)((?:\[\d+\])*)$/.exec(segment);
    if (match === null) throw new Error(`Not a draft path: ${path}`);
    parts.push(match[1]!);
    for (const index of match[2]!.matchAll(/\[(\d+)\]/g)) parts.push(Number(index[1]));
  }
  return parts;
}

/** The value at a path; undefined when a parent is missing. */
export function getIn(draft: SetupDraft, path: string): unknown {
  let current: unknown = draft;
  for (const key of parsePath(path)) {
    if (current === null || typeof current !== 'object') return undefined;
    current = (current as Record<string | number, unknown>)[key];
  }
  return current;
}

/**
 * A copy of the draft with one value set; missing parents along the way are created as objects
 * (a meter row, a signal), so a step can set meter.phases.L2.power.entity on an empty draft.
 */
export function setIn(draft: SetupDraft, path: string, value: unknown): SetupDraft {
  const keys = parsePath(path);
  const write = (node: unknown, index: number): unknown => {
    const key = keys[index]!;
    if (typeof key === 'number') {
      const array = Array.isArray(node) ? [...node] : [];
      array[key] = index === keys.length - 1 ? value : write(array[key], index + 1);
      return array;
    }
    const object = node !== null && typeof node === 'object' && !Array.isArray(node) ? { ...(node as Record<string, unknown>) } : {};
    object[key] = index === keys.length - 1 ? value : write(object[key], index + 1);
    return object;
  };
  return write(draft, 0) as SetupDraft;
}

/** A new device as a template card or the generic form starts it: nothing declared until the user picks. */
export function newDevice(prefill: Record<string, string | number> = {}, template: string | null = null): DeviceDraft {
  const device: DeviceDraft = {
    id: null,
    existing: false,
    editable: true,
    template,
    name: null,
    type: null,
    actuator: null,
    stateEntity: null,
    stateReports: null,
    phase: null,
    ratedCurrentA: null,
    ratedPowerW: null,
    min: null,
    max: null,
    step: null,
    reactionTime: null,
    minOn: null,
    minOff: null,
    valuePerKwh: null,
    circuitGroup: null,
    rotationGroup: null,
    draw: null,
  };
  for (const [key, value] of Object.entries(prefill)) {
    if (key in device) (device as unknown as Record<string, unknown>)[key] = value;
  }
  return device;
}

export type DraftAction =
  | { kind: 'replace'; draft: SetupDraft }
  | { kind: 'set'; path: string; value: unknown }
  | { kind: 'addDevice'; device: DeviceDraft }
  | { kind: 'removeDevice'; index: number };

/** The draft's only writer; every step dispatches to it. */
export function draftReducer(draft: SetupDraft, action: DraftAction): SetupDraft {
  switch (action.kind) {
    case 'replace':
      return action.draft;
    case 'set':
      return setIn(draft, action.path, action.value);
    case 'addDevice':
      return { ...draft, devices: [...draft.devices, action.device] };
    case 'removeDevice':
      return { ...draft, devices: draft.devices.filter((_, i) => i !== action.index) };
  }
}

/** The entities a draft maps, the meter first: what the readouts follow. */
export function draftEntities(draft: SetupDraft): string[] {
  const entities: (string | null | undefined)[] = [];
  for (const phase of ['L1', 'L2', 'L3'] as const) {
    const row = draft.meter.phases[phase];
    entities.push(row?.current?.entity, row?.power?.entity, row?.voltage?.entity);
  }
  entities.push(draft.meter.total.power?.entity, draft.meter.total.current?.entity);
  const inverter = draft.sources.inverter;
  entities.push(inverter?.power?.entity, inverter?.phaseCurrents?.L1?.entity, inverter?.phaseCurrents?.L2?.entity, inverter?.phaseCurrents?.L3?.entity);
  for (const device of draft.devices) entities.push(device.stateEntity ?? device.actuator, device.draw?.current, device.draw?.power);
  return [...new Set(entities.filter((e): e is string => typeof e === 'string' && e !== ''))];
}
