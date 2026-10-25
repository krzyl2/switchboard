import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/preact';
import { SetupSources } from './SetupSources';
import { cleanDraft, renderStep, samples, serve, type Payload } from './SetupLoads.test-support';
import { emptyDraft, type SetupIssue } from './api/setup';
import { messages } from './i18n';
import { advance } from './screens.test-support';
import { fieldId, issuesAt, issuesOfStep } from './setupDraft';
import type { SetupState } from './useSetup';

let fetchMock: Mock<typeof fetch>;

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
  // Unsaved drafts and watches are kept per browser tab; every test starts in a fresh one.
  window.sessionStorage.clear();
  fetchMock = vi.fn<typeof fetch>();
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

const byField = <T extends HTMLElement = HTMLInputElement>(field: string) => document.getElementById(fieldId(field)) as T;
const optionValues = (select: HTMLSelectElement) => [...select.options].filter((o) => !o.disabled).map((o) => o.value).filter((v) => v !== '');

/** The shared state a step receives, with the given issues from the add-on (no network). */
function state(issues: SetupIssue[]): SetupState {
  return {
    load: { kind: 'ready' },
    view: null,
    draft: emptyDraft(),
    templates: [],
    prefillVoltageV: 230,
    validation: null,
    issues,
    busy: 'idle',
    outcome: null,
    unsaved: 'none',
    discardUnsaved: () => undefined,
    set: () => {},
    addDevice: () => {},
    removeDevice: () => {},
    validate: () => Promise.resolve(null),
    save: () => Promise.resolve(null),
    reload: () => {},
    issuesOf: (s) => issuesOfStep(issues, s),
    issuesAt: (f) => issuesAt(issues, f),
  };
}

const refusal: SetupIssue = { code: 'required_field', severity: 'error', field: 'sources.inverter.power.entity', documentPath: null, message: 'required', args: {} };
const elsewhere: SetupIssue = { ...refusal, field: 'connection.voltageV' };

// UI-01 / UI-05: the step has its own heading in both languages and shows the add-on's refusals
// at its own fields, never another step's.
describe('SetupSources', () => {
  it('has its heading in English and Polish', () => {
    serve(fetchMock);
    const { unmount } = render(<SetupSources setup={state([])} t={messages.en} lang="en" goTo={() => {}} />);
    expect(screen.getByRole('heading', { name: 'Sources and storage' })).toBeTruthy();
    unmount();

    render(<SetupSources setup={state([])} t={messages.pl} lang="pl" goTo={() => {}} />);
    expect(screen.getByRole('heading', { name: 'Źródła i magazyny' })).toBeTruthy();
  });

  it('shows a refusal at one of its fields and not another step’s', () => {
    serve(fetchMock);
    render(<SetupSources setup={state([refusal, elsewhere])} t={messages.en} lang="en" goTo={() => {}} />);

    const alert = screen.getByRole('alert');
    expect(alert.querySelectorAll('li')).toHaveLength(1);
    expect(alert.querySelector('li')?.getAttribute('data-field')).toBe('sources.inverter.power.entity');
    expect(alert.textContent).toContain('This field is required.');
  });
});

// The inverter: protection never reads generation, dispatch needs it. Nothing is declared until the
// user adds an inverter and picks its entity (UI-03: filtered by device class or unit; 07-PITFALLS:
// a derived sensor is never an input), and its live value and age show beside it (UI-04).
describe('PV inverter', () => {
  it('starts with no PV and offers only power sensors once the user adds an inverter', async () => {
    serve(fetchMock, { draft: cleanDraft() });
    const setup = await renderStep(SetupSources, vi.fn());

    expect((screen.getByRole('radio', { name: 'No PV' }) as HTMLInputElement).checked).toBe(true);
    expect(byField('sources.inverter.power.entity')).toBeNull();

    fireEvent.click(screen.getByRole('radio', { name: 'A PV inverter' }));
    expect(setup().draft.sources.inverter).toEqual({ power: null, sign: null, unavailableAsZero: null, phase: null, phaseCurrents: null });

    const picker = byField<HTMLSelectElement>('sources.inverter.power.entity');
    expect(picker.value).toBe('');
    expect(optionValues(picker)).toContain('sensor.solaredge_m1_ac_power');
    expect(optionValues(picker)).not.toContain('sensor.solaredge_m1_ac_current_a');
    const usage = [...picker.options].find((o) => o.value === 'sensor.usage')!;
    expect(usage.disabled).toBe(true);
    expect(usage.textContent).toBe('sensor.usage — computed by template, not an input');
  });

  it('takes the unit from Home Assistant when an entity is picked', async () => {
    serve(fetchMock, { draft: cleanDraft() });
    const setup = await renderStep(SetupSources, vi.fn());
    fireEvent.click(screen.getByRole('radio', { name: 'A PV inverter' }));

    fireEvent.change(byField('sources.inverter.power.entity'), { target: { value: 'sensor.solaredge_m1_ac_power' } });
    fireEvent.input(byField('sources.inverter.power.maxAge'), { target: { value: '90s' } });
    fireEvent.change(byField('sources.inverter.sign'), { target: { value: 'positive_import' } });

    expect(setup().draft.sources.inverter).toMatchObject({ power: { entity: 'sensor.solaredge_m1_ac_power', unit: 'W', maxAge: '90s' }, sign: 'positive_import' });
    expect(byField<HTMLSelectElement>('sources.inverter.power.unit').value).toBe('W');
  });

  it('explains unavailable_as_zero as an inverter asleep at night and writes it only when chosen', async () => {
    serve(fetchMock, { draft: cleanDraft() });
    const setup = await renderStep(SetupSources, vi.fn());
    fireEvent.click(screen.getByRole('radio', { name: 'A PV inverter' }));

    const asleep = screen.getByRole('checkbox', { name: 'Count an inverter asleep at night as 0 W' }) as HTMLInputElement;
    expect(asleep.checked).toBe(false);
    expect(asleep.closest('.loads-field')?.textContent).toContain('falls silent after reporting 0 W (asleep at night), its production counts as 0 W instead of unknown');
    expect(asleep.closest('.loads-field')?.textContent).toContain('Protection never reads the production.');

    fireEvent.click(asleep);
    expect(setup().draft.sources.inverter?.unavailableAsZero).toBe(true);
    fireEvent.click(asleep);
    expect(setup().draft.sources.inverter?.unavailableAsZero).toBeNull();
  });

  it('re-run shows the file’s inverter with live readouts and its per-phase currents', async () => {
    serve(fetchMock);
    await renderStep(SetupSources, vi.fn());
    await advance();

    expect(byField<HTMLSelectElement>('sources.inverter.power.entity').value).toBe('sensor.solaredge_ac_power');
    const row = document.querySelector('[data-path="sources.inverter.power"]')!;
    expect(row.querySelector('.value')?.textContent).toBe('1234 W');
    expect(row.querySelector('.age')?.textContent).toBe('reported 3 s ago');
    expect(['L1', 'L2', 'L3'].map((p) => byField<HTMLSelectElement>(`sources.inverter.phaseCurrents.${p}.entity`).value)).toEqual([
      'sensor.solaredge_ac_current_a',
      'sensor.solaredge_ac_current_b',
      'sensor.solaredge_ac_current_c',
    ]);
    expect((screen.getByRole('checkbox', { name: 'Count an inverter asleep at night as 0 W' }) as HTMLInputElement).checked).toBe(true);
  });

  it('brings the mapping back when the user chooses the inverter again after No PV', async () => {
    serve(fetchMock);
    const setup = await renderStep(SetupSources, vi.fn());

    fireEvent.click(screen.getByRole('radio', { name: 'No PV' }));
    expect(setup().draft.sources.inverter).toBeNull();
    fireEvent.click(screen.getByRole('radio', { name: 'A PV inverter' }));
    expect(setup().draft.sources.inverter?.power?.entity).toBe('sensor.solaredge_ac_power');
  });

  it('declares a single-phase inverter on its phase', async () => {
    serve(fetchMock);
    const setup = await renderStep(SetupSources, vi.fn());

    fireEvent.change(byField('sources.inverter.phase'), { target: { value: 'L2' } });
    expect(setup().draft.sources.inverter?.phase).toBe('L2');
    fireEvent.change(byField('sources.inverter.phase'), { target: { value: '' } });
    expect(setup().draft.sources.inverter?.phase).toBeNull();
  });

  it('offers the add-on’s suggested max age once the refresh is detected', async () => {
    serve(fetchMock);
    const setup = await renderStep(SetupSources, vi.fn());

    fireEvent.click(screen.getByRole('button', { name: 'Check the sources and continue' }));
    await advance();
    await advance();

    const row = document.querySelector('[data-path="sources.inverter.power"]')!;
    expect(row.querySelector('.loads-refresh')?.textContent).toContain('Refresh 30 s fits the max age 90 s.');
    fireEvent.click(within(row as HTMLElement).getByRole('button', { name: 'Use 180s' }));
    expect(setup().draft.sources.inverter?.power?.maxAge).toBe('180s');
  });
});

// A battery is a storage device (devices of type storage): added and edited here with the device
// form; capacity and SoC are stated as not used in this version rather than asked for.
describe('energy storage', () => {
  it('adds a battery as a storage device and says what this version does not use', async () => {
    serve(fetchMock, { draft: cleanDraft() });
    const setup = await renderStep(SetupSources, vi.fn());

    expect(screen.getByText('Capacity, charge power and SoC limits are not used in this version.')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Add a battery by hand' }));
    await advance();

    expect(setup().draft.devices).toHaveLength(1);
    expect(setup().draft.devices[0]).toMatchObject({ id: 'battery', type: 'storage', actuator: null, existing: false });
    const battery = document.querySelector('article[data-device="battery"]')!;
    expect(battery.textContent).toContain('Charging setpoint in A: minimum 0 or below');
    // The device parser gives storage the setpoint domains of a modulating device.
    expect(optionValues(byField<HTMLSelectElement>('devices[0].actuator'))).toEqual(['number.goe_422752_amp']);
    expect(document.activeElement).toBe(byField('devices[0].actuator'));
  });

  it('shows a storage device’s issues here, where its fields are', async () => {
    const view = structuredClone(samples.draftSample) as Payload & { draft: { devices: Payload[] } };
    view.draft.devices.push({ ...view.draft.devices[3]!, id: 'battery', name: 'Bateria', type: 'storage', min: 2, max: 10, draw: null });
    const issue: SetupIssue = { code: 'document_invalid', severity: 'error', field: 'devices[4].min', documentPath: 'devices[4].min', message: 'A storage range must include 0 (min ≤ 0).', args: {} };
    const goTo = vi.fn();
    serve(fetchMock, { draft: view, validation: () => ({ ...structuredClone(samples.validationSample), valid: false, issues: [issue] }) });
    await renderStep(SetupSources, goTo);

    fireEvent.click(screen.getByRole('button', { name: 'Check the sources and continue' }));
    await advance();
    await advance();

    expect(goTo).not.toHaveBeenCalled();
    expect(document.querySelector('.setup-issues [data-field="devices[4].min"]')).toBeTruthy();
    expect(document.activeElement).toBe(byField('devices[4].min'));
    // The loads on step 4 are not listed here.
    expect(document.querySelector('article[data-device="heater_l1"]')).toBeNull();
  });
});

// UI-05: the step continues only when no error remains at its own fields, and lands on the first.
describe('check and continue', () => {
  it('continue is refused naming the failed field', async () => {
    const issue: SetupIssue = { code: 'required_field', severity: 'error', field: 'sources.inverter.sign', documentPath: null, message: 'The inverter power’s sign convention is required.', args: {} };
    const goTo = vi.fn();
    serve(fetchMock, { validation: () => ({ ...structuredClone(samples.validationSample), valid: false, issues: [issue] }) });
    await renderStep(SetupSources, goTo);

    fireEvent.click(screen.getByRole('button', { name: 'Check the sources and continue' }));
    await advance();
    await advance();

    expect(goTo).not.toHaveBeenCalled();
    expect(document.querySelector('.setup-issues [role="alert"] [data-field="sources.inverter.sign"]')).toBeTruthy();
    expect(document.activeElement).toBe(byField('sources.inverter.sign'));
  });

  it('an error at another step’s field does not hold this step', async () => {
    const issue: SetupIssue = { code: 'required_field', severity: 'error', field: 'devices[0].phase', documentPath: null, message: 'required', args: {} };
    const goTo = vi.fn();
    serve(fetchMock, { validation: () => ({ ...structuredClone(samples.validationSample), valid: false, issues: [issue] }) });
    await renderStep(SetupSources, goTo);

    fireEvent.click(screen.getByRole('button', { name: 'Check the sources and continue' }));
    await advance();
    await advance();

    expect(goTo).toHaveBeenCalledWith('loads');
  });
});
