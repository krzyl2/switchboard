import { describe, expect, it } from 'vitest';
import { emptyDraft, parseDraftView, type SetupIssue } from './api/setup';
import draftSample from './api/setup-draft.sample.json';
import { STEPS, draftEntities, draftReducer, fieldId, getIn, issuesAt, issuesOfStep, newDevice, setIn, stepOfField } from './setupDraft';

function issue(field: string | null, severity: 'error' | 'warning' = 'error'): SetupIssue {
  return { code: 'required_field', severity, field, documentPath: null, message: 'x', args: {} };
}

// UI-05: the add-on names a field by its draft path; the wizard must land on the step and the input
// that path addresses, or "says what to fix" points nowhere.
describe('setup draft', () => {
  it('has the five steps in the mockup order', () => {
    expect(STEPS).toEqual(['meter', 'connection', 'sources', 'loads', 'mode']);
  });

  it.each([
    ['meter.phases.L2.current.entity', 'meter'],
    ['connection.topology', 'meter'],
    ['connection.breakerRatingA', 'meter'],
    ['connection.reserveW.L1', 'connection'],
    ['connection.characteristic', 'connection'],
    ['sources.inverter.power.entity', 'sources'],
    ['devices[1].phase', 'loads'],
    ['acknowledgements.notProtective', 'mode'],
    [null, 'mode'],
  ])('routes %s to the %s step', (field, step) => {
    expect(stepOfField(field)).toBe(step);
  });

  it('lists a step’s issues errors first, and a field’s own issues', () => {
    const issues = [issue('devices[0].phase', 'warning'), issue('devices[0].actuator'), issue('meter.powerSign'), issue('devices[10].id')];

    expect(issuesOfStep(issues, 'loads').map((i) => i.field)).toEqual(['devices[0].actuator', 'devices[10].id', 'devices[0].phase']);
    expect(issuesAt(issues, 'devices[0]').map((i) => i.field)).toEqual(['devices[0].phase', 'devices[0].actuator']);
  });

  it('gives every field a DOM id a refusal can focus', () => {
    expect(fieldId('meter.phases.L1.current.entity')).toBe('setup-meter-phases-L1-current-entity');
    expect(fieldId('devices[2].phase')).toBe('setup-devices-2-phase');
  });

  it('sets nested values on an empty draft, creating the parents', () => {
    const draft = setIn(emptyDraft(), 'meter.phases.L2.power.entity', 'sensor.m_power_b');

    expect(getIn(draft, 'meter.phases.L2.power.entity')).toBe('sensor.m_power_b');
    expect(getIn(emptyDraft(), 'meter.phases.L2.power.entity')).toBeUndefined();
    expect(getIn(setIn(draft, 'connection.reserveW.L1', 2000), 'connection.reserveW.L1')).toBe(2000);
  });

  it('edits one device without touching the others', () => {
    const loaded = parseDraftView(structuredClone(draftSample)).draft;

    const edited = draftReducer(loaded, { kind: 'set', path: 'devices[1].name', value: 'Grzałka łazienka' });

    expect(edited.devices.map((d) => d.name)).toEqual(['Grzałka CWU L1', 'Grzałka łazienka', 'Grzałka CWU L3', 'Ładowarka go-e']);
    expect(loaded.devices[1]?.name).toBe('Grzałka CWU L2');
  });

  it('a template prefills only device fields and declares nothing', () => {
    const device = newDevice({ type: 'modulating', min: 6, max: 16, step: 1, bogus: 3 }, 'ev_charger');

    expect(device).toMatchObject({ type: 'modulating', min: 6, max: 16, step: 1, template: 'ev_charger', actuator: null, existing: false });
    expect('bogus' in device).toBe(false);
  });

  it('adds and removes devices', () => {
    const one = draftReducer(emptyDraft(), { kind: 'addDevice', device: newDevice({ type: 'onoff' }) });
    const two = draftReducer(one, { kind: 'addDevice', device: newDevice({ type: 'onoff' }) });

    expect(draftReducer(two, { kind: 'removeDevice', index: 0 }).devices).toHaveLength(1);
  });

  it('collects the mapped entities, the meter first', () => {
    const loaded = parseDraftView(structuredClone(draftSample)).draft;

    const entities = draftEntities(loaded);
    expect(entities[0]).toBe('sensor.solaredge_m1_ac_current_a');
    expect(entities).toContain('switch.grzalka_1');
    expect(entities).toContain('sensor.goe_422752_nrg_4');
    expect(new Set(entities).size).toBe(entities.length);
  });
});
