import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/preact';
import { SetupLoads } from './SetupLoads';
import { cleanDraft, posted, renderStep, samples, serve, type Payload } from './SetupLoads.test-support';
import { emptyDraft, type SetupIssue } from './api/setup';
import { messages } from './i18n';
import { advance, respond } from './screens.test-support';
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
const card = (id: string) => document.querySelector(`article[data-device="${id}"]`) as HTMLElement;
const optionValues = (select: HTMLSelectElement) => [...select.options].map((o) => o.value).filter((v) => v !== '');

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

const refusal: SetupIssue = { code: 'required_field', severity: 'error', field: 'devices[0].phase', documentPath: null, message: 'required', args: {} };
const elsewhere: SetupIssue = { ...refusal, field: 'connection.voltageV' };

// UI-01 / UI-05: the step has its own heading in both languages and shows the add-on's refusals
// at its own fields, never another step's.
describe('SetupLoads', () => {
  it('has its heading in English and Polish', () => {
    serve(fetchMock);
    const { unmount } = render(<SetupLoads setup={state([])} t={messages.en} lang="en" goTo={() => {}} />);
    expect(screen.getByRole('heading', { name: 'Loads' })).toBeTruthy();
    unmount();

    render(<SetupLoads setup={state([])} t={messages.pl} lang="pl" goTo={() => {}} />);
    expect(screen.getByRole('heading', { name: 'Odbiorniki' })).toBeTruthy();
  });

  it('shows a refusal at one of its fields and not another step’s', () => {
    serve(fetchMock);
    render(<SetupLoads setup={state([refusal, elsewhere])} t={messages.en} lang="en" goTo={() => {}} />);

    const alert = screen.getByRole('alert');
    expect(alert.querySelectorAll('li')).toHaveLength(1);
    expect(alert.querySelector('li')?.getAttribute('data-field')).toBe('devices[0].phase');
    expect(alert.textContent).toContain('This field is required.');
  });
});

// UI-06: the configuration is editable later; re-running the wizard never loses an existing device.
// Every device of the file is listed with its own form, its id stays fixed, and nothing disappears
// unless the user confirms the removal.
describe('editability: re-running the wizard', () => {
  it('lists every device of the file with a badge and a read-only id', async () => {
    serve(fetchMock);
    await renderStep(SetupLoads, vi.fn());

    const ids = [...document.querySelectorAll('article[data-device]')].map((a) => a.getAttribute('data-device'));
    expect(ids).toEqual(['heater_l1', 'heater_l2', 'heater_l3', 'ev_charger']);
    expect(within(card('heater_l2')).getByText('from the file')).toBeTruthy();
    expect(byField('devices[1].id').readOnly).toBe(true);
    expect(byField('devices[1].id').value).toBe('heater_l2');
    expect(document.querySelector('.loads-added')?.textContent).toBe('Added: Grzałka CWU L1 (L1), Grzałka CWU L2 (L2), Grzałka CWU L3 (L3), Ładowarka go-e (L1)');
  });

  it('keeps every device on save when one is edited', async () => {
    serve(fetchMock);
    const setup = await renderStep(SetupLoads, vi.fn());

    fireEvent.input(byField('devices[0].name'), { target: { value: 'Grzałka łazienka' } });
    fireEvent.input(byField('devices[3].valuePerKwh'), { target: { value: '1,85' } });
    await act(async () => {
      await setup().save();
    });

    const sent = posted(fetchMock, 'api/setup/save')[0]!.draft as { devices: { id: string; name: string; valuePerKwh: number }[] };
    expect(sent.devices.map((d) => d.id)).toEqual(['heater_l1', 'heater_l2', 'heater_l3', 'ev_charger']);
    expect(sent.devices[0]!.name).toBe('Grzałka łazienka');
    expect(sent.devices[1]!.name).toBe('Grzałka CWU L2');
    // A comma is a decimal separator for Polish users.
    expect(sent.devices[3]!.valuePerKwh).toBe(1.85);
  });

  it('removing a device needs confirmation', async () => {
    serve(fetchMock);
    const setup = await renderStep(SetupLoads, vi.fn());

    fireEvent.click(within(card('heater_l2')).getByRole('button', { name: 'Remove device' }));
    expect(setup().draft.devices).toHaveLength(4);
    expect(within(card('heater_l2')).getByRole('alertdialog').textContent).toContain('Remove Grzałka CWU L2?');

    fireEvent.click(within(card('heater_l2')).getByRole('button', { name: 'Keep' }));
    expect(setup().draft.devices).toHaveLength(4);

    fireEvent.click(within(card('heater_l2')).getByRole('button', { name: 'Remove device' }));
    fireEvent.click(within(card('heater_l2')).getByRole('button', { name: 'Remove' }));
    expect(setup().draft.devices.map((d) => d.id)).toEqual(['heater_l1', 'heater_l3', 'ev_charger']);
  });

  it('shows a device the wizard cannot edit as kept as written, with removal only', async () => {
    const view = structuredClone(samples.draftSample) as Payload & { draft: { devices: Payload[] } };
    view.draft.devices.push({ ...view.draft.devices[0]!, id: 'boiler', name: null, type: 'stepped', editable: false, actuator: null, phase: null });
    serve(fetchMock, { draft: view });
    await renderStep(SetupLoads, vi.fn());

    const kept = card('boiler');
    expect(within(kept).getByText('kept as written')).toBeTruthy();
    expect(kept.querySelector('input, select')).toBeNull();
    expect(within(kept).getByRole('button', { name: 'Remove device' })).toBeTruthy();
  });

  it('leaves storage devices to step 3', async () => {
    const view = structuredClone(samples.draftSample) as Payload & { draft: { devices: Payload[] } };
    view.draft.devices.push({ ...view.draft.devices[3]!, id: 'battery', name: 'Bateria', type: 'storage', min: -10, max: 10, draw: null });
    serve(fetchMock, { draft: view });
    await renderStep(SetupLoads, vi.fn());

    expect(card('battery')).toBeNull();
    expect(document.querySelectorAll('article[data-device]')).toHaveLength(4);
  });
});

// UI-02: template cards prefill type, limits and lockouts from the add-on's templates endpoint (no
// literal in the page), and a new device declares nothing the user did not pick.
describe('templates', () => {
  it('template prefills type, limits and lockouts from the endpoint', async () => {
    const templates = structuredClone(samples.templatesSample);
    // A value only the endpoint knows: the page must take it from there.
    templates.templates[0]!.prefill.max = 32;
    serve(fetchMock, { draft: cleanDraft(), templates });
    const setup = await renderStep(SetupLoads, vi.fn());

    expect(screen.getByText('No device yet: pick a template above.')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /^EV charger/ }));
    await advance();

    const added = setup().draft.devices[0]!;
    expect(added).toMatchObject({ id: 'ev_charger', template: 'ev_charger', type: 'modulating', min: 6, max: 32, step: 1, existing: false, actuator: null, phase: null });
    expect(byField('devices[0].max').value).toBe('32');
    // Lockouts it inherits are shown, not written: a later default still applies.
    expect(byField('devices[0].reactionTime').placeholder).toBe('default 20s');
    expect(added.reactionTime).toBeNull();
    expect(within(card('ev_charger')).getByText('new')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /^Heat pump or AC/ }));
    await advance();
    expect(setup().draft.devices[1]).toMatchObject({ id: 'heat_pump', type: 'onoff', minOn: '10m', minOff: '10m', reactionTime: null });
    expect(byField('devices[1].minOn').value).toBe('10m');
  });

  it('gives a second device of a template its own id', async () => {
    serve(fetchMock);
    const setup = await renderStep(SetupLoads, vi.fn());

    fireEvent.click(screen.getByRole('button', { name: /^EV charger/ }));
    await advance();

    expect(setup().draft.devices.map((d) => d.id)).toEqual(['heater_l1', 'heater_l2', 'heater_l3', 'ev_charger', 'ev_charger_2']);
  });

  it('never suggests the id of a device removed from the file in this edit', async () => {
    // The add-on refuses a new device that reuses a file id: merged into the removed entry, the
    // new charger would keep its keys (a stop actuator, a shed priority).
    serve(fetchMock);
    const setup = await renderStep(SetupLoads, vi.fn());
    fireEvent.click(within(card('ev_charger')).getByRole('button', { name: 'Remove device' }));
    fireEvent.click(within(card('ev_charger')).getByRole('button', { name: 'Remove' }));
    await advance();

    fireEvent.click(screen.getByRole('button', { name: /^EV charger/ }));
    await advance();

    expect(setup().draft.devices.map((d) => d.id)).toEqual(['heater_l1', 'heater_l2', 'heater_l3', 'ev_charger_2']);
  });

  it('a draw takes its unit from the picked entity, so a kW sensor is never read as W', async () => {
    const discovery = structuredClone(samples.discoverySample) as unknown as { entities: Payload[] };
    discovery.entities.push({ ...discovery.entities.find((e) => e.entityId === 'sensor.goe_422752_nrg_7')!, entityId: 'sensor.charger_kw', unit: 'kW' });
    serve(fetchMock, { draft: cleanDraft(), discovery });
    const setup = await renderStep(SetupLoads, vi.fn());
    fireEvent.click(screen.getByRole('button', { name: /^EV charger/ }));
    await advance();

    fireEvent.change(byField('devices[0].draw.power'), { target: { value: 'sensor.charger_kw' } });
    fireEvent.change(byField('devices[0].draw.current'), { target: { value: 'sensor.goe_422752_nrg_4' } });

    expect(setup().draft.devices[0]!.draw).toMatchObject({ power: 'sensor.charger_kw', powerUnit: 'kW', current: 'sensor.goe_422752_nrg_4', currentUnit: 'A' });

    fireEvent.change(byField('devices[0].draw.power'), { target: { value: 'sensor.goe_422752_nrg_7' } });
    expect(setup().draft.devices[0]!.draw).toMatchObject({ power: 'sensor.goe_422752_nrg_7', powerUnit: 'W' });
  });

  it('actuator picker lists declarable domains and never preselects', async () => {
    serve(fetchMock, { draft: cleanDraft() });
    const setup = await renderStep(SetupLoads, vi.fn());

    fireEvent.click(screen.getByRole('button', { name: /^Water-heater element/ }));
    fireEvent.click(screen.getByRole('button', { name: /^EV charger/ }));
    await advance();

    const relay = byField<HTMLSelectElement>('devices[0].actuator');
    expect(relay.value).toBe('');
    expect(optionValues(relay)).toEqual(['switch.grzalka_1', 'switch.grzalka_2', 'switch.grzalka_3']);
    expect(optionValues(byField<HTMLSelectElement>('devices[1].actuator'))).toEqual(['number.goe_422752_amp']);
    expect(setup().draft.devices.every((d) => d.actuator === null)).toBe(true);

    fireEvent.change(relay, { target: { value: 'switch.grzalka_3' } });
    expect(setup().draft.devices[0]!.actuator).toBe('switch.grzalka_3');
  });

  it('offers a derived sensor for a draw disabled, with its platform as the reason', async () => {
    serve(fetchMock);
    await renderStep(SetupLoads, vi.fn());

    const power = byField<HTMLSelectElement>('devices[3].draw.power');
    const disabled = [...power.options].filter((o) => o.disabled).map((o) => o.value);
    expect(disabled).toEqual(expect.arrayContaining(['sensor.moc_grzalek_cwu', 'sensor.usage', 'sensor.kettle_power']));
    expect(power.value).toBe('sensor.goe_422752_nrg_7');
    expect(optionValues(byField<HTMLSelectElement>('devices[3].draw.current'))).not.toContain('sensor.goe_422752_nrg_7');
  });
});

// UI-04 and CONN-02: every picked entity shows its live value and age, so a wrong pick is visible at
// once; each device sits on a phase (or all three), which drives every protection decision.
describe('device form', () => {
  it('shows the live value and age beside the state and the draw', async () => {
    serve(fetchMock);
    await renderStep(SetupLoads, vi.fn());
    await advance();

    const heater = card('heater_l1');
    expect(heater.querySelector('.loads-readout')?.textContent).toContain('switch.grzalka_2');
    expect(heater.querySelector('.loads-readout .value')?.textContent).toBe('off');
    expect(heater.querySelector('.loads-readout .age')?.textContent).toBe('reported 3 s ago');
    expect(card('ev_charger').querySelector('[data-entity="sensor.goe_422752_nrg_7"] .value')?.textContent).toBe('1234 W');
  });

  it('assigns a phase per device or all three', async () => {
    serve(fetchMock);
    const setup = await renderStep(SetupLoads, vi.fn());

    expect(optionValues(byField<HTMLSelectElement>('devices[0].phase'))).toEqual(['L1', 'L2', 'L3', 'three_phase']);
    fireEvent.change(byField('devices[0].phase'), { target: { value: 'three_phase' } });

    expect(setup().draft.devices[0]!.phase).toBe('three_phase');
    // A three-phase device cannot be judged by the passive single-phase self-test; the form says so.
    expect(card('heater_l1').querySelector('.loads-selftest')).toBeNull();
    expect(card('heater_l1').textContent).toContain('a current-controlled or three-phase device cannot be judged this way');
  });

  it('sets state reports, groups and the draw as the user enters them', async () => {
    serve(fetchMock, { draft: cleanDraft() });
    const setup = await renderStep(SetupLoads, vi.fn());
    fireEvent.click(screen.getByRole('button', { name: /^Water-heater element/ }));
    await advance();

    fireEvent.change(byField('devices[0].stateReports'), { target: { value: 'on_change' } });
    fireEvent.input(byField('devices[0].rotationGroup'), { target: { value: ' cwu_heaters ' } });
    fireEvent.change(byField('devices[0].draw.power'), { target: { value: 'sensor.goe_422752_nrg_7' } });

    expect(setup().draft.devices[0]).toMatchObject({ stateReports: 'on_change', rotationGroup: 'cwu_heaters', draw: { current: null, power: 'sensor.goe_422752_nrg_7', maxAge: null } });

    fireEvent.change(byField('devices[0].draw.power'), { target: { value: '' } });
    expect(setup().draft.devices[0]!.draw).toBeNull();
  });
});

// UI-05: the step continues only when the add-on found no error at its own fields, and otherwise
// names the field and lands on it.
describe('check and continue', () => {
  const mismatch: SetupIssue = {
    code: 'phase_self_test_mismatch',
    severity: 'error',
    field: 'devices[1].phase',
    documentPath: null,
    message: 'When switch.grzalka_1 switched, the meter’s L2 rose, not L3.',
    args: { device: 'Grzałka CWU L2', declared: 'L3', observed: 'L2' },
  };

  it('continue is refused naming the failed field', async () => {
    const goTo = vi.fn();
    serve(fetchMock, { validation: () => ({ ...structuredClone(samples.validationSample), valid: false, issues: [mismatch] }) });
    await renderStep(SetupLoads, goTo);

    fireEvent.click(screen.getByRole('button', { name: 'Check the loads and continue' }));
    await advance();
    await advance();

    expect(goTo).not.toHaveBeenCalled();
    const alert = screen.getAllByRole('alert').find((a) => a.closest('.setup-issues'))!;
    expect(alert.querySelector('li')?.getAttribute('data-field')).toBe('devices[1].phase');
    expect(alert.textContent).toContain('the meter’s L2 moved with it, not L3');
    expect(document.activeElement).toBe(byField('devices[1].phase'));
    expect(byField('devices[1].phase').getAttribute('aria-invalid')).toBe('true');
  });

  it('continues to the mode step when only warnings remain', async () => {
    const goTo = vi.fn();
    serve(fetchMock);
    await renderStep(SetupLoads, goTo);

    fireEvent.click(screen.getByRole('button', { name: 'Check the loads and continue' }));
    await advance();
    await advance();

    expect(goTo).toHaveBeenCalledWith('mode');
  });
});

// CONN-05, passive (binding): Switchboard never switches anything for the self-test. The user
// switches the device while the wizard watches; the result names the phase that rose, by how much,
// and a mismatch against the declared phase. Inconclusive is never shown as a pass.
describe('phase self-test', () => {
  const watching = () => ({ ...structuredClone(samples.selfTestSample), status: 'watching', observations: [], verdict: { verdict: 'inconclusive', observedPhase: null, reason: 'timed_out', confirmed: 0, mismatched: 0, inconclusive: 0 } });

  function swapped() {
    const test = structuredClone(samples.selfTestSample) as unknown as { expected: Payload; observations: Payload[]; verdict: Payload };
    test.expected.phase = 'L3';
    Object.assign(test.observations[0]!, { verdict: 'mismatch', observedPhase: 'L2', ratingRatio: 1.0007 });
    test.verdict = { verdict: 'mismatch', observedPhase: 'L2', reason: null, confirmed: 0, mismatched: 1, inconclusive: 0 };
    return test;
  }

  it('says plainly that Switchboard does not switch the device for the test', async () => {
    serve(fetchMock);
    await renderStep(SetupLoads, vi.fn());

    for (const id of ['heater_l1', 'heater_l2', 'heater_l3']) {
      expect(card(id).querySelector('.loads-selftest-passive')?.textContent).toBe(
        'Switchboard does not switch the device for this test. Switch it yourself in Home Assistant or by hand while the wizard watches which meter phase steps.',
      );
    }
    // The charger modulates: the passive on/off test cannot judge it.
    expect(card('ev_charger').querySelector('.loads-selftest')).toBeNull();
  });

  it('watches while the user switches and shows the phase that rose, by how much, and the verdict', async () => {
    serve(fetchMock, { selfTests: [watching(), samples.selfTestSample] });
    await renderStep(SetupLoads, vi.fn());

    fireEvent.click(within(card('heater_l2')).getByRole('button', { name: 'Watch while I switch it' }));
    await advance();
    await advance();

    const [body] = posted(fetchMock, 'api/setup/self-test');
    expect(body!.device).toEqual({ index: 1 });
    expect((body!.draft as { devices: unknown[] }).devices).toHaveLength(4);
    const watch = () => card('heater_l2').querySelector('.loads-selftest-watch')!;
    expect(watch().textContent).toContain('Watching switch.grzalka_1 until');
    // Entity IDs are always shown in monospace.
    expect(watch().querySelector('p .mono')?.textContent).toBe('switch.grzalka_1');
    expect(within(card('heater_l2')).getByRole('button', { name: 'Stop watching' })).toBeTruthy();

    await advance(2_000);
    await advance();

    expect(watch().textContent).toContain('The watch is finished.');
    const row = watch().querySelector('tbody tr')!;
    expect(row.textContent).toContain('L2 +1,701 W');
    expect(row.textContent).toContain('switched on');
    expect(row.querySelector('[data-verdict="confirmed"]')?.textContent).toBe('phase confirmed: L2');
    // A finished watch asks the add-on to judge the draft again (the issues and the history update).
    expect(posted(fetchMock, 'api/setup/validate').length).toBeGreaterThan(0);
  });

  it('a swapped L2/L3 mapping is caught: the step rose on L2 while the draft declares L3', async () => {
    serve(fetchMock, { selfTests: [swapped()] });
    const setup = await renderStep(SetupLoads, vi.fn());

    fireEvent.change(byField('devices[1].phase'), { target: { value: 'L3' } });
    fireEvent.click(within(card('heater_l2')).getByRole('button', { name: 'Watch while I switch it' }));
    await advance();
    await advance();
    await advance();

    expect((posted(fetchMock, 'api/setup/self-test')[0]!.draft as { devices: { phase: string }[] }).devices[1]!.phase).toBe('L3');
    const watch = card('heater_l2').querySelector('.loads-selftest-watch')!;
    expect(watch.querySelector('p [data-verdict="mismatch"]')?.textContent).toBe('mismatch: the step was on L2, declared L3');
    expect(watch.textContent).toContain('Either the device is on L2, or the meter’s phase entities are swapped (step 1).');
    expect(setup().draft.devices[1]!.phase).toBe('L3');
  });

  it('inconclusive is never shown as a pass', async () => {
    const expired = { ...watching(), status: 'expired', verdict: { verdict: 'inconclusive', observedPhase: null, reason: 'no_step', confirmed: 0, mismatched: 0, inconclusive: 1 } };
    serve(fetchMock, { selfTests: [expired] });
    await renderStep(SetupLoads, vi.fn());

    fireEvent.click(within(card('heater_l3')).getByRole('button', { name: 'Watch while I switch it' }));
    await advance();
    await advance();
    await advance();

    const watch = card('heater_l3').querySelector('.loads-selftest-watch')!;
    expect(watch.textContent).toContain('The watch ended without a verdict.');
    expect(watch.querySelector('[data-verdict]')?.getAttribute('data-verdict')).toBe('inconclusive');
    expect(watch.textContent).toContain('not confirmed: no phase stepped (a thermostat may have been open)');
    expect(watch.textContent).not.toContain('phase confirmed');
    expect(within(card('heater_l3')).getByRole('button', { name: 'Watch again' })).toBeTruthy();
  });

  it('runs one watch at a time and offers to stop the other', async () => {
    serve(fetchMock, { start: () => respond({ code: 'self_test_busy', id: 'abc' }, 409) });
    await renderStep(SetupLoads, vi.fn());

    fireEvent.click(within(card('heater_l1')).getByRole('button', { name: 'Watch while I switch it' }));
    await advance();
    expect(card('heater_l1').textContent).toContain('Another self-test is already watching; only one runs at a time.');

    fireEvent.click(within(card('heater_l1')).getByRole('button', { name: 'Stop the other test' }));
    await advance();
    expect(fetchMock.mock.calls.some(([url, init]) => String(url) === 'api/setup/self-test/abc' && init?.method === 'DELETE')).toBe(true);
    expect(within(card('heater_l1')).getByRole('button', { name: 'Watch while I switch it' })).toBeTruthy();
  });

  it('shows why the add-on refused to start a watch, at the field it names', async () => {
    const issue = { code: 'required_field', severity: 'error', field: 'devices[0].ratedPowerW', documentPath: null, message: 'The rating is required.', args: {} };
    serve(fetchMock, { start: () => respond({ code: 'invalid', issues: [issue], currentRevision: null }, 422) });
    await renderStep(SetupLoads, vi.fn());

    fireEvent.click(within(card('heater_l1')).getByRole('button', { name: 'Watch while I switch it' }));
    await advance();

    const refused = card('heater_l1').querySelector('.loads-selftest [data-issue="required_field"]')!;
    expect(refused.getAttribute('data-field')).toBe('devices[0].ratedPowerW');
  });

  it('shows the add-on’s verdict over recorded switchings for this draft, and drops it after an edit', async () => {
    const validation = structuredClone(samples.validationSample) as unknown as { selfTest: Payload[] };
    validation.selfTest[1] = { ...validation.selfTest[1]!, verdict: 'confirmed', observedPhase: 'L2', reason: null, confirmed: 5, mismatched: 0, inconclusive: 1 };
    serve(fetchMock, { validation: () => validation });
    await renderStep(SetupLoads, vi.fn());

    const verdict = () => card('heater_l2').querySelector('.loads-selftest-verdict')!;
    expect(verdict().textContent).toBe('Not judged against this draft yet.');
    fireEvent.click(within(card('heater_l2')).getByRole('button', { name: 'Judge against this draft' }));
    await advance();
    await advance();

    expect(verdict().textContent).toContain('phase confirmed: L2');
    expect(verdict().textContent).toContain('5 confirmed, 0 mismatched, 1 inconclusive');

    // The verdict was for L2; declared on L3 it would claim something nobody judged.
    fireEvent.change(byField('devices[1].phase'), { target: { value: 'L3' } });
    expect(verdict().textContent).toBe('Not judged against this draft yet.');
  });

  it('lists the switchings the running add-on recorded', async () => {
    serve(fetchMock);
    await renderStep(SetupLoads, vi.fn());

    const history = card('heater_l2').querySelector('.loads-selftest-history')!;
    // The sample holds the guided watch's switch on and off and the add-on's own recorded pair.
    const rows = [...history.querySelectorAll('tbody tr')];
    expect(rows).toHaveLength(4);
    const recorded = rows.filter((row) => row.textContent!.includes('recorded by the add-on'));
    expect(recorded).toHaveLength(2);
    expect(recorded[0]!.textContent).toContain('house load (PV taken out)');
    expect(card('heater_l3').querySelector('.loads-selftest-history')!.textContent).toContain('The add-on has not recorded a switching of switch.grzalka_3 since it started.');
  });
});
