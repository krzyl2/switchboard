import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/preact';
import { SetupLoads } from './SetupLoads';
import { cleanDraft, posted, readoutsFor, renderStep, samples, serve, type Payload } from './SetupLoads.test-support';
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

// Control stage (maintainer survey 2026-10-25, select stop 0.7.1): a modulating device may declare a
// stop actuator, a switch or a select that stops it below its minimum. Only the user declares it
// (CLAUDE.md): discovery suggests the switches and selects of the control entity's own Home
// Assistant device, and nothing is ever preselected, neither the entity nor its options.
describe('stop actuator', () => {
  type Entity = Payload & { entityId: string };

  /** The sample's go-e device (control entity, force-state select) gains its charging switch; Switchboard's own switch is added too. */
  function discoveryWith(change: (entities: Entity[]) => void = () => {}): Payload {
    const discovery = structuredClone(samples.discoverySample) as unknown as { entities: Entity[] };
    const amp = discovery.entities.find((e) => e.entityId === 'number.goe_422752_amp')!;
    const own = discovery.entities.find((e) => e.entityId === 'select.switchboard_ev_charger_override')!;
    discovery.entities.push(
      { ...amp, entityId: 'switch.goe_422752_charging', domain: 'switch', name: 'go-e charging allowed', deviceClass: 'switch', unit: null, state: 'on' },
      { ...own, entityId: 'switch.switchboard_control_allowed', domain: 'switch', name: 'Switchboard control allowed', state: 'off', options: null },
    );
    change(discovery.entities);
    return discovery as unknown as Payload;
  }

  /** The file's charger with a select stop and the given options. */
  function draftWithSelectStop(stopOption: string | null, resumeOption: string | null): Payload {
    const view = structuredClone(samples.draftSample) as unknown as { draft: { devices: Payload[] } };
    Object.assign(view.draft.devices[3]!, { stopActuator: 'select.goe_422752_frc', stopOption, resumeOption });
    return view as unknown as Payload;
  }

  /** Readouts as the add-on answers them, the force-state select reporting the given option. */
  const readoutsWithSelect = (state: string) => (url: string) => {
    const answer = readoutsFor(url) as { items: Payload[] };
    for (const item of answer.items) if (item.entityId === 'select.goe_422752_frc') Object.assign(item, { state, unit: null });
    return answer;
  };

  const suggestions = () => card('ev_charger').querySelector('.loads-stop-suggest') as HTMLElement;
  const stopOption = () => byField<HTMLSelectElement>('devices[3].stopOption');
  const resumeOption = () => byField<HTMLSelectElement>('devices[3].resumeOption');
  const fieldOf = (element: HTMLElement) => element.closest('.loads-field') as HTMLElement;

  it('offers selects of the charger’s device as suggestions, never preselected', async () => {
    serve(fetchMock, { discovery: discoveryWith() });
    const setup = await renderStep(SetupLoads, vi.fn());

    const picker = byField<HTMLSelectElement>('devices[3].stopActuator');
    expect(picker.value).toBe('');
    expect(setup().draft.devices[3]!.stopActuator).toBeNull();
    // The same-device select and switch lead the list; every relay and select entity stays pickable,
    // the control entity's own domain (number) does not.
    expect(optionValues(picker).slice(0, 2)).toEqual(['select.goe_422752_frc', 'switch.goe_422752_charging']);
    expect(optionValues(picker)).toEqual(expect.arrayContaining(['switch.grzalka_1', 'switch.grzalka_2', 'switch.grzalka_3']));
    expect(optionValues(picker)).not.toContain('number.goe_422752_amp');
    expect(suggestions().textContent).toContain('Switches and selects of the same Home Assistant device as number.goe_422752_amp:');
    expect([...suggestions().querySelectorAll('li')].map((li) => li.getAttribute('data-entity'))).toEqual(['select.goe_422752_frc', 'switch.goe_422752_charging']);
    // No option is asked before a select is picked, and an on/off device has no stop actuator at all
    // (the add-on writes it for modulating devices only).
    expect(stopOption()).toBeNull();
    expect(byField('devices[0].stopActuator')).toBeNull();

    fireEvent.click(within(suggestions()).getByRole('button', { name: 'Use switch.goe_422752_charging' }));
    expect(setup().draft.devices[3]!.stopActuator).toBe('switch.goe_422752_charging');
    // A switch is turned off to stop and on to resume: it takes no options.
    expect(stopOption()).toBeNull();
    await act(async () => {
      await setup().save();
    });
    const sent = posted(fetchMock, 'api/setup/save')[0]!.draft as { devices: Payload[] };
    expect(sent.devices[3]).toMatchObject({ stopActuator: 'switch.goe_422752_charging', stopOption: null, resumeOption: null });
  });

  it('asks for stop and resume options from the select’s options, none preselected, showing its state', async () => {
    // The user had set the select to another option themselves: the live readout shows it, and
    // nothing is chosen from it.
    serve(fetchMock, { discovery: discoveryWith(), readouts: readoutsWithSelect('2') });
    const setup = await renderStep(SetupLoads, vi.fn());

    fireEvent.click(within(suggestions()).getByRole('button', { name: 'Use select.goe_422752_frc' }));
    await advance();

    expect(setup().draft.devices[3]).toMatchObject({ stopActuator: 'select.goe_422752_frc', stopOption: null, resumeOption: null });
    for (const [select, label] of [
      [stopOption(), 'Stop option'],
      [resumeOption(), 'Resume option'],
    ] as const) {
      expect(document.querySelector(`label[for="${select.id}"]`)?.textContent).toBe(label);
      expect(optionValues(select)).toEqual(['0', '1', '2']);
      expect(select.value).toBe('');
      expect(select.options[0]!.textContent).toBe('Choose…');
      expect(select.required).toBe(true);
      expect(fieldOf(select).querySelector('.loads-stop-now')?.textContent).toBe('Now: 2');
    }

    fireEvent.change(stopOption(), { target: { value: '1' } });
    fireEvent.change(resumeOption(), { target: { value: '0' } });
    expect(setup().draft.devices[3]).toMatchObject({ stopOption: '1', resumeOption: '0' });
    await act(async () => {
      await setup().save();
    });
    const sent = posted(fetchMock, 'api/setup/save')[0]!.draft as { devices: Payload[] };
    expect(sent.devices[3]).toMatchObject({ stopActuator: 'select.goe_422752_frc', stopOption: '1', resumeOption: '0' });
  });

  it('names the options in Polish', async () => {
    serve(fetchMock, { discovery: discoveryWith(), draft: draftWithSelectStop(null, null), readouts: readoutsWithSelect('0') });
    await renderStep(SetupLoads, vi.fn(), 'pl');
    await advance();

    expect(document.querySelector(`label[for="${stopOption().id}"]`)?.textContent).toBe('Opcja zatrzymania');
    expect(document.querySelector(`label[for="${resumeOption().id}"]`)?.textContent).toBe('Opcja wznowienia');
    expect(stopOption().options[0]!.textContent).toBe('Wybierz…');
    expect(fieldOf(stopOption()).querySelector('.loads-stop-now')?.textContent).toBe('Teraz: 0');
  });

  it('keeps the file’s options', async () => {
    serve(fetchMock, { discovery: discoveryWith(), draft: draftWithSelectStop('1', '0') });
    const setup = await renderStep(SetupLoads, vi.fn());

    expect(byField<HTMLSelectElement>('devices[3].stopActuator').value).toBe('select.goe_422752_frc');
    expect(stopOption().value).toBe('1');
    expect(resumeOption().value).toBe('0');
    await act(async () => {
      await setup().save();
    });
    const sent = posted(fetchMock, 'api/setup/save')[0]!.draft as { devices: Payload[] };
    expect(sent.devices[3]).toMatchObject({ stopActuator: 'select.goe_422752_frc', stopOption: '1', resumeOption: '0' });
  });

  it('keeps a file option the entity does not offer, marked, with the add-on’s error at it', async () => {
    // A hand-written 1.0 is not the select's 1: Switchboard would refuse to send it, so the page
    // shows it as written (never blank, never lost) and the add-on's error lands on that field.
    const unknown: SetupIssue = {
      code: 'stop_option_unknown',
      severity: 'error',
      field: 'devices[3].stopOption',
      documentPath: null,
      message: 'select.goe_422752_frc does not offer the option 1.0.',
      args: { entity: 'select.goe_422752_frc', option: '1.0', options: '0, 1, 2' },
    };
    serve(fetchMock, {
      discovery: discoveryWith(),
      draft: draftWithSelectStop('1.0', '0'),
      validation: () => ({ ...structuredClone(samples.validationSample), valid: false, issues: [unknown] }),
    });
    const setup = await renderStep(SetupLoads, vi.fn());

    expect(stopOption().value).toBe('1.0');
    expect(stopOption().selectedOptions[0]!.textContent).toBe('1.0 (not offered by this entity)');
    expect(optionValues(stopOption())).toEqual(['0', '1', '2', '1.0']);

    fireEvent.click(screen.getByRole('button', { name: 'Check the loads and continue' }));
    await advance();
    await advance();
    expect(fieldOf(stopOption()).querySelector('.loads-field-issues')?.textContent).toBe(
      'select.goe_422752_frc does not offer the option 1.0 (it offers 0, 1, 2); Switchboard would never send it.',
    );
    expect(document.activeElement).toBe(stopOption());
    expect(stopOption().getAttribute('aria-invalid')).toBe('true');
    expect(setup().draft.devices[3]!.stopOption).toBe('1.0');
  });

  it('lands on the option the add-on names when an option is missing or both are the same', async () => {
    const issues: SetupIssue[] = [
      { code: 'required_field', severity: 'error', field: 'devices[3].stopOption', documentPath: null, message: 'required', args: { field: 'devices[3].stopOption' } },
      { code: 'stop_options_equal', severity: 'error', field: 'devices[3].resumeOption', documentPath: null, message: 'equal', args: { option: '1' } },
    ];
    serve(fetchMock, {
      discovery: discoveryWith(),
      draft: draftWithSelectStop(null, '1'),
      validation: () => ({ ...structuredClone(samples.validationSample), valid: false, issues }),
    });
    const goTo = vi.fn();
    await renderStep(SetupLoads, goTo);

    fireEvent.click(screen.getByRole('button', { name: 'Check the loads and continue' }));
    await advance();
    await advance();

    expect(goTo).not.toHaveBeenCalled();
    expect(document.activeElement).toBe(stopOption());
    expect(fieldOf(stopOption()).textContent).toContain('This field is required.');
    expect(fieldOf(resumeOption()).textContent).toContain('The resume option must differ from the stop option (1): one stops the device, the other lets it run.');
  });

  it('clears options when the stop changes to a switch', async () => {
    serve(fetchMock, { discovery: discoveryWith(), draft: draftWithSelectStop('1', '0') });
    const setup = await renderStep(SetupLoads, vi.fn());

    fireEvent.change(byField('devices[3].stopActuator'), { target: { value: 'switch.goe_422752_charging' } });
    expect(setup().draft.devices[3]).toMatchObject({ stopActuator: 'switch.goe_422752_charging', stopOption: null, resumeOption: null });
    expect(stopOption()).toBeNull();

    // Options belong to the entity they were chosen from: back on the select, nothing is chosen again.
    fireEvent.click(within(suggestions()).getByRole('button', { name: 'Use select.goe_422752_frc' }));
    expect(setup().draft.devices[3]).toMatchObject({ stopActuator: 'select.goe_422752_frc', stopOption: null, resumeOption: null });
    expect(stopOption().value).toBe('');
    expect(resumeOption().value).toBe('');
  });

  it('lets the user type options when HA reports none', async () => {
    serve(fetchMock, {
      discovery: discoveryWith((entities) => {
        entities.find((e) => e.entityId === 'select.goe_422752_frc')!.options = null;
      }),
    });
    const setup = await renderStep(SetupLoads, vi.fn());

    fireEvent.change(byField('devices[3].stopActuator'), { target: { value: 'select.goe_422752_frc' } });
    await advance();

    expect(stopOption().tagName).toBe('INPUT');
    expect(resumeOption().tagName).toBe('INPUT');
    expect(card('ev_charger').textContent).toContain('Home Assistant reports no options for this entity: type each option exactly as Home Assistant shows it.');
    fireEvent.input(stopOption(), { target: { value: '1' } });
    fireEvent.input(resumeOption(), { target: { value: '0' } });
    expect(setup().draft.devices[3]).toMatchObject({ stopActuator: 'select.goe_422752_frc', stopOption: '1', resumeOption: '0' });
  });

  it('still warns that a PV-surplus switch is not a stop, and that another select of the device is not one either', async () => {
    serve(fetchMock, { discovery: discoveryWith() });
    await renderStep(SetupLoads, vi.fn());

    expect(suggestions().textContent).toContain('Check that it really stops the device: a switch for PV surplus or access control is not a stop.');
    expect(suggestions().textContent).toContain('For a select, the stop option must stop the device; another select of the same device (a mode, phase switching) is not a stop.');
  });

  it('never offers Switchboard’s own entities as a control or stop entity', async () => {
    // Commanding one would loop back into Switchboard (its override select, its control switch), and
    // a "confirmed" stop through it would say nothing about the device's real draw.
    serve(fetchMock, { discovery: discoveryWith() });
    await renderStep(SetupLoads, vi.fn());

    const stopPicker = optionValues(byField<HTMLSelectElement>('devices[3].stopActuator'));
    expect(stopPicker).not.toContain('select.switchboard_ev_charger_override');
    expect(stopPicker).not.toContain('switch.switchboard_control_allowed');
    const actuatorPicker = optionValues(byField<HTMLSelectElement>('devices[0].actuator'));
    expect(actuatorPicker).not.toContain('switch.switchboard_control_allowed');
    expect(actuatorPicker).toContain('switch.grzalka_2');
  });

  it('shows the add-on’s refusal of an own entity at the stop field and its warning about a stop changed while reduced', async () => {
    // A hand-written file can still name Switchboard's own entity (the picker never offers it), and
    // changing the stop of a charger Switchboard holds reduced leaves it stopped: both must be seen.
    const issues: SetupIssue[] = [
      { code: 'own_entity_not_actuator', severity: 'error', field: 'devices[3].stopActuator', documentPath: null, message: 'own', args: { entity: 'select.switchboard_ev_charger_override' } },
      { code: 'stop_changed_while_reduced', severity: 'warning', field: 'devices[3].stopActuator', documentPath: null, message: 'reduced', args: { device: 'ev_charger', entity: 'select.goe_422752_frc' } },
    ];
    serve(fetchMock, { discovery: discoveryWith(), validation: () => ({ ...structuredClone(samples.validationSample), valid: false, issues }) });
    await renderStep(SetupLoads, vi.fn(), 'pl');

    fireEvent.click(screen.getByRole('button', { name: 'Sprawdź odbiorniki i przejdź dalej' }));
    await advance();
    await advance();

    expect(fieldOf(byField('devices[3].stopActuator')).querySelector('.loads-field-issues')?.textContent).toBe(
      'select.switchboard_ev_charger_override to encja samego Switchboard i Switchboard nie może nią sterować. Wybierz encję urządzenia.',
    );
    const warning = document.querySelector('.setup-issues li[data-issue="stop_changed_while_reduced"]');
    expect(warning?.getAttribute('data-field')).toBe('devices[3].stopActuator');
    expect(warning?.textContent).toContain('Switchboard trzyma ev_charger ograniczone przez select.goe_422752_frc');
  });

  it('says plainly when the charger’s device has no switch or select, instead of suggesting an unrelated one', async () => {
    serve(fetchMock, {
      discovery: discoveryWith((entities) => {
        for (const gone of ['select.goe_422752_frc', 'switch.goe_422752_charging']) entities.splice(entities.findIndex((e) => e.entityId === gone), 1);
      }),
    });
    await renderStep(SetupLoads, vi.fn());

    expect(suggestions()).toBeNull();
    expect(card('ev_charger').querySelector('.loads-stop-none')?.textContent).toBe('No switch or select of the same Home Assistant device as number.goe_422752_amp was found.');
    expect(byField<HTMLSelectElement>('devices[3].stopActuator').value).toBe('');
  });

  it('asks for the control entity first on a new charger: suggestions come from its device', async () => {
    serve(fetchMock, { draft: cleanDraft(), discovery: discoveryWith() });
    const setup = await renderStep(SetupLoads, vi.fn());
    fireEvent.click(screen.getByRole('button', { name: /^EV charger/ }));
    await advance();

    expect(card('ev_charger').textContent).toContain('Pick the control entity first: suggestions come from its Home Assistant device.');
    fireEvent.change(byField('devices[0].actuator'), { target: { value: 'number.goe_422752_amp' } });
    expect([...card('ev_charger').querySelectorAll('.loads-stop-suggest li')].map((li) => li.getAttribute('data-entity'))).toEqual(['select.goe_422752_frc', 'switch.goe_422752_charging']);
    expect(setup().draft.devices[0]).toMatchObject({ stopActuator: null, stopOption: null, resumeOption: null });
  });
});

// Start policy (maintainer survey 2026-10-25): restore_only is the default, so dispatch never starts
// a device the user had off; headroom keeps the earlier behaviour. A device without start: shows the
// default and its draft stays untouched (the add-on writes the key only when it differs).
describe('start policy', () => {
  it('shows only restore as the default and writes a choice only when the user makes one', async () => {
    serve(fetchMock);
    const setup = await renderStep(SetupLoads, vi.fn());

    const select = byField<HTMLSelectElement>('devices[0].start');
    expect(select.value).toBe('restore_only');
    expect(optionValues(select)).toEqual(['restore_only', 'headroom']);
    expect(select.options[0]!.textContent).toBe('Only restore (default)');
    expect(setup().draft.devices.every((d) => d.start === null)).toBe(true);
    // Every device of this step has one, the charger too.
    expect(byField<HTMLSelectElement>('devices[3].start').value).toBe('restore_only');

    fireEvent.change(byField('devices[3].start'), { target: { value: 'headroom' } });
    expect(setup().draft.devices[3]!.start).toBe('headroom');
    await act(async () => {
      await setup().save();
    });
    const sent = posted(fetchMock, 'api/setup/save')[0]!.draft as { devices: { start: string | null }[] };
    expect(sent.devices.map((d) => d.start)).toEqual([null, null, null, 'headroom']);
  });

  it('keeps a value it does not know, so a hand-written file is never silently changed', async () => {
    const view = structuredClone(samples.draftSample) as Payload & { draft: { devices: Payload[] } };
    view.draft.devices[1]!.start = 'surplus';
    serve(fetchMock, { draft: view });
    const setup = await renderStep(SetupLoads, vi.fn());

    expect(byField<HTMLSelectElement>('devices[1].start').value).toBe('surplus');
    expect(setup().draft.devices[1]!.start).toBe('surplus');
  });

  it('uses the Polish vocabulary of the survey', async () => {
    serve(fetchMock);
    await renderStep(SetupLoads, vi.fn(), 'pl');

    const labels = [...byField<HTMLSelectElement>('devices[0].start').options].map((o) => o.textContent);
    expect(labels).toEqual(['Tylko przywracanie (domyślnie)', 'Uruchamiaj przy zapasie mocy']);
  });
});

// CONN-05, automatic (maintainer survey 2026-10-25: "if we have headroom, Switchboard may switch the
// device on and off to identify phases"). The add-on decides whether the test may start; the page
// shows its reasons and enables the button only when it may, asks once more before anything is
// switched, follows the test through on, the measured step per phase, off and the verdict, and
// confirms the device is back off as it was. It coexists with the passive watch.
describe('automatic phase self-test', () => {
  const ready = () => ({ ok: true, refusals: [], expectedDurationS: 422 });
  const active = () => card('heater_l1').querySelector('.loads-active') as HTMLElement;
  const startButton = (name = 'Test automatically') => within(active()).getByRole('button', { name }) as HTMLButtonElement;
  const steps = () => Object.fromEntries([...active().querySelectorAll('.loads-active-steps li')].map((li) => [li.getAttribute('data-step'), li.getAttribute('data-state')]));

  /** The sample's finished test, walked back to an earlier phase. */
  function phase(name: 'pending_on' | 'on' | 'pending_off' | 'judging'): Payload {
    const done = structuredClone(samples.activeTestSample) as unknown as Payload & { observations: Payload[] };
    const at = { onAt: name === 'pending_on' ? null : done.onAt, offAt: name === 'judging' ? done.offAt : null };
    const observations = name === 'pending_off' || name === 'judging' ? [done.observations[0]] : [];
    return { ...done, state: 'running', phase: name, ...at, verdict: null, observations };
  }

  function swapped(): Payload {
    const test = structuredClone(samples.activeTestSample) as unknown as Payload & { observations: Payload[] };
    test.observations = test.observations.map((o) => ({ ...o, verdict: 'mismatch', observedPhase: 'L2' }));
    test.verdict = { verdict: 'mismatch', observedPhase: 'L2', reason: null, confirmed: 0, mismatched: 2, inconclusive: 0 };
    return test;
  }

  it('is refused with the add-on’s reasons, and nothing can be switched while it is', async () => {
    serve(fetchMock);
    await renderStep(SetupLoads, vi.fn());
    await advance();

    const reasons = [...active().querySelectorAll('.loads-active-refusals li')].map((li) => li.getAttribute('data-refusal'));
    expect(reasons).toEqual(['control_not_active', 'not_declared']);
    expect(active().textContent).toContain('Control is not active: it needs the add-on option active, Control allowed switched on and a valid configuration (step 5).');
    expect(startButton().disabled).toBe(true);
    expect(posted(fetchMock, 'api/setup/active-test')).toEqual([]);
    // The charger modulates: it cannot be switched on and off for a phase test.
    expect(card('ev_charger').querySelector('.loads-active')).toBeNull();
  });

  it('names the phase without headroom and how long the minimum off time still runs', async () => {
    const refused = {
      ok: false,
      refusals: [
        { code: 'headroom_insufficient', phase: 'L2', remainingS: null },
        { code: 'lockout_min_off', phase: null, remainingS: 74.2 },
      ],
      expectedDurationS: 497,
    };
    serve(fetchMock, { activePreconditions: () => refused });
    await renderStep(SetupLoads, vi.fn());
    await advance();

    expect(active().textContent).toContain('Not enough headroom on L2 for the device’s rated current with the reserve kept free.');
    expect(active().textContent).toContain('Its minimum off time still runs for 75 s.');
    expect(active().textContent).toContain('Once it can start, the test takes about 8 min.');
    expect(startButton().disabled).toBe(true);
  });

  it('a device only in the draft must be saved first, and the add-on is not asked about it', async () => {
    serve(fetchMock, { activePreconditions: ready });
    await renderStep(SetupLoads, vi.fn());
    fireEvent.click(screen.getByRole('button', { name: /^Water-heater element/ }));
    await advance(2_000);

    const added = card('water_heater_element');
    fireEvent.change(within(added).getByLabelText('Phase'), { target: { value: 'L2' } });
    await advance(2_000);
    expect(added.querySelector('.loads-active-save')?.textContent).toBe('Save the configuration first (step 5): the automatic test switches only a device of the configuration in force.');
    expect(within(added.querySelector('.loads-active') as HTMLElement).queryByRole('button', { name: 'Test automatically' })).toBeNull();
    expect(fetchMock.mock.calls.some(([url]) => String(url).includes('active-test/preconditions?device=water_heater_element'))).toBe(false);
  });

  it('asks once more, then follows the test through on, the step per phase, off and the verdict, and the device is back off', async () => {
    serve(fetchMock, {
      activePreconditions: ready,
      activeStart: () => respond({ id: samples.activeTestSample.id, expectedDurationS: 422 }, 202),
      activeTests: [phase('pending_on'), phase('on'), phase('pending_off'), phase('judging'), samples.activeTestSample],
    });
    await renderStep(SetupLoads, vi.fn());
    await advance();

    expect(active().textContent).toContain('Ready: the test takes about 7 min.');
    fireEvent.click(startButton());
    // Nothing is switched before the user confirms what will be switched and for how long.
    const confirm = within(active()).getByRole('alertdialog');
    expect(confirm.textContent).toContain('Switchboard will switch switch.grzalka_2 on and off again; the test takes about 7 min. Start it?');
    expect(confirm.querySelector('.mono')?.textContent).toBe('switch.grzalka_2');
    expect(posted(fetchMock, 'api/setup/active-test')).toEqual([]);

    fireEvent.click(within(confirm).getByRole('button', { name: 'Start the test' }));
    await advance();
    await advance();
    expect(posted(fetchMock, 'api/setup/active-test')).toEqual([{ device: 'heater_l1', stage: 1 }]);
    expect(active().textContent).toContain('The test is running. Switching on and waiting for the device to report on…');
    expect(steps()).toEqual({ on: 'current', measureOn: 'pending', off: 'pending', judge: 'pending' });
    expect(within(active()).getByRole('button', { name: 'Stop the test' })).toBeTruthy();
    expect(within(active()).queryByRole('button', { name: 'Test automatically' })).toBeNull();

    await advance(2_000);
    expect(steps()).toEqual({ on: 'done', measureOn: 'current', off: 'pending', judge: 'pending' });
    await advance(2_000);
    expect(steps()).toEqual({ on: 'done', measureOn: 'done', off: 'current', judge: 'pending' });
    // The step measured at the on, per phase, before the verdict.
    expect(active().querySelector('.loads-observations tbody tr')?.textContent).toContain('L1 +1,694 W');
    await advance(2_000);
    expect(steps()).toEqual({ on: 'done', measureOn: 'done', off: 'done', judge: 'current' });

    await advance(2_000);
    await advance();
    expect(active().textContent).toContain('The test is finished.');
    expect(steps()).toEqual({ on: 'done', measureOn: 'done', off: 'done', judge: 'done' });
    const rows = [...active().querySelectorAll('.loads-observations tbody tr')];
    expect(rows).toHaveLength(2);
    expect(rows.every((row) => row.textContent!.includes('switched by the active self-test'))).toBe(true);
    expect(rows[1]!.textContent).toContain('L1 -1,712 W');
    expect(active().querySelector('.loads-active-verdict [data-verdict="confirmed"]')?.textContent).toBe('phase confirmed: L1');
    expect(active().querySelector('.loads-active-return')?.getAttribute('data-restored')).toBe('true');
    expect(active().querySelector('.loads-active-return')?.textContent).toContain('Back to its prior state: off, as before the test');
    // A finished test asks the add-on to judge the draft again, so the verdict and history include it.
    expect(posted(fetchMock, 'api/setup/validate').length).toBeGreaterThan(0);
    expect(startButton('Test automatically again')).toBeTruthy();
  });

  it('a deliberately swapped phase is caught: the step was on L2 while the configuration declares L1', async () => {
    serve(fetchMock, {
      activePreconditions: ready,
      activeStart: () => respond({ id: samples.activeTestSample.id, expectedDurationS: 422 }, 202),
      activeTests: [swapped()],
    });
    await renderStep(SetupLoads, vi.fn());
    await advance();

    fireEvent.click(startButton());
    fireEvent.click(within(active()).getByRole('button', { name: 'Start the test' }));
    await advance();
    await advance();

    expect(active().querySelector('.loads-active-verdict [data-verdict="mismatch"]')?.textContent).toBe('mismatch: the step was on L2, declared L1');
    expect(active().textContent).toContain('Either the device is on L2, or the meter’s phase entities are swapped (step 1).');
    expect(active().textContent).not.toContain('phase confirmed');
  });

  it('an aborted test says why, and confirms the device is off again only when it reports off', async () => {
    const aborted = { ...phase('on'), state: 'aborted', phase: 'done', abortReason: 'guard_acted' };
    let heater: 'unavailable' | 'on' | 'off' = 'unavailable';
    const readouts = (url: string) => {
      const answer = readoutsFor(url) as { items: Payload[] };
      for (const item of answer.items) {
        if (item.entityId !== 'switch.grzalka_2') continue;
        if (heater === 'unavailable') Object.assign(item, { status: 'unavailable', state: null });
        else item.state = heater;
      }
      return answer;
    };
    // A test started earlier in this tab is shown again when the user comes back to the step.
    window.sessionStorage.setItem(`switchboard.setup.activeTest.heater_l1`, samples.activeTestSample.id);
    serve(fetchMock, { readouts, activeTests: [aborted] });
    await renderStep(SetupLoads, vi.fn());
    await advance();
    await advance();

    expect(active().textContent).toContain('The test was stopped: the guard reduced a load, and protection comes first.');
    const back = () => active().querySelector('.loads-active-return')!;
    // An unknown state is never reported as "back off".
    expect(back().getAttribute('data-restored')).toBe('false');
    expect(back().textContent).toBe('Waiting for the device to report off, as it was before the test.');

    heater = 'on';
    await advance(2_000);
    expect(back().getAttribute('data-restored')).toBe('false');
    expect(back().textContent).toBe('The device still reports on: Switchboard asks for the off until it reports it. If it stays on, switch it off in Home Assistant.');

    heater = 'off';
    await advance(2_000);
    expect(back().getAttribute('data-restored')).toBe('true');
    expect(back().textContent).toBe('Back to its prior state: the device reports off, as before the test.');
  });

  it('after an abort the device is not called back off while the add-on still sees it on, whatever the readout says', async () => {
    // The off after an abort waits for min_on unless protection holds the device: the add-on's own
    // view (device_on) outranks a readout that may lag behind it.
    const aborted = { ...phase('on'), state: 'aborted', phase: 'done', abortReason: 'increases_stopped' };
    let addOnSeesOn = true;
    window.sessionStorage.setItem('switchboard.setup.activeTest.heater_l1', samples.activeTestSample.id);
    serve(fetchMock, {
      activeTests: [aborted],
      activePreconditions: () => ({ ok: false, refusals: addOnSeesOn ? [{ code: 'device_on', phase: null, remainingS: null }] : [{ code: 'lockout_min_off', phase: null, remainingS: 80 }], expectedDurationS: 502 }),
    });
    await renderStep(SetupLoads, vi.fn());
    await advance();
    await advance();

    const back = () => active().querySelector('.loads-active-return')!;
    expect(active().textContent).toContain('The test was stopped: the guard stopped adding load.');
    expect(back().getAttribute('data-restored')).toBe('false');
    expect(back().textContent).toContain('The device still reports on');

    addOnSeesOn = false;
    await advance(2_000);
    expect(back().getAttribute('data-restored')).toBe('true');
  });

  it('the user can stop a running test: the add-on is asked to stop it and switch the device off', async () => {
    const cancelled = { ...phase('on'), state: 'aborted', phase: 'done', abortReason: 'cancelled' };
    serve(fetchMock, {
      activePreconditions: ready,
      activeStart: () => respond({ id: samples.activeTestSample.id, expectedDurationS: 422 }, 202),
      activeTests: [phase('on'), phase('on'), cancelled],
    });
    await renderStep(SetupLoads, vi.fn());
    await advance();
    fireEvent.click(startButton());
    fireEvent.click(within(active()).getByRole('button', { name: 'Start the test' }));
    await advance();
    await advance();

    // A running test is kept for this tab: leaving the step (to look at Home Assistant) loses nothing.
    expect(window.sessionStorage.getItem('switchboard.setup.activeTest.heater_l1')).toBe(samples.activeTestSample.id);
    fireEvent.click(within(active()).getByRole('button', { name: 'Stop the test' }));
    await advance();
    expect(fetchMock.mock.calls.some(([url, init]) => String(url) === `api/setup/active-test/${samples.activeTestSample.id}` && init?.method === 'DELETE')).toBe(true);
    await advance(2_000);
    await advance(2_000);
    expect(active().textContent).toContain('The test was stopped: you stopped it.');
    expect(window.sessionStorage.getItem('switchboard.setup.activeTest.heater_l1')).toBeNull();
  });

  it('a confirmation open while the conditions change can no longer start the test', async () => {
    let headroom = true;
    serve(fetchMock, { activePreconditions: () => (headroom ? ready() : { ok: false, refusals: [{ code: 'headroom_insufficient', phase: 'L3', remainingS: null }], expectedDurationS: 422 }) });
    await renderStep(SetupLoads, vi.fn());
    await advance();

    fireEvent.click(startButton());
    const confirmStart = () => within(active()).getByRole('button', { name: 'Start the test' }) as HTMLButtonElement;
    expect(confirmStart().disabled).toBe(false);
    headroom = false;
    await advance(2_000);
    expect(confirmStart().disabled).toBe(true);
    expect(active().textContent).toContain('Not enough headroom on L3');
  });

  it('shows the add-on’s reasons when it refuses a start the page thought possible', async () => {
    serve(fetchMock, { activePreconditions: ready });
    await renderStep(SetupLoads, vi.fn());
    await advance();

    fireEvent.click(startButton());
    fireEvent.click(within(active()).getByRole('button', { name: 'Start the test' }));
    await advance();

    const refused = active().querySelector('[role="alert"]')!;
    expect(refused.textContent).toContain('The add-on refused to start the test:');
    expect([...refused.querySelectorAll('li')].map((li) => li.getAttribute('data-refusal'))).toEqual(['control_not_active', 'not_declared']);
  });

  it('coexists with the passive watch, which still switches nothing', async () => {
    serve(fetchMock);
    await renderStep(SetupLoads, vi.fn(), 'pl');
    await advance();

    const heater = card('heater_l1');
    expect(within(heater).getByRole('button', { name: 'Testuj automatycznie' })).toBeTruthy();
    expect(within(heater).getByRole('button', { name: 'Obserwuj, gdy przełączam' })).toBeTruthy();
    expect(heater.querySelector('.loads-selftest-passive')?.textContent).toContain('Switchboard nie przełącza urządzenia podczas tego testu.');
  });
});
