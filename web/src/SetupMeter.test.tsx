import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/preact';
import { useReducer, useState } from 'preact/hooks';
import { phaseFromName, SetupMeter, suggestMapping } from './SetupMeter';
import { emptyDraft, parseDiscovery, parseDraft, parseTemplates, parseValidation, type SetupDraft, type SetupIssue, type Validation } from './api/setup';
import discoverySample from './api/setup-discovery.sample.json';
import draftSample from './api/setup-draft.sample.json';
import selfTestSample from './api/setup-self-test.sample.json';
import startedSample from './api/setup-self-test-started.sample.json';
import templatesSample from './api/setup-templates.sample.json';
import validationSample from './api/setup-validation.sample.json';
import { messages, type Lang } from './i18n';
import { advance, respond } from './screens.test-support';
import { draftReducer, fieldId, issuesAt, issuesOfStep, type StepName } from './setupDraft';
import type { SetupState } from './useSetup';

const SESSION_KEY = 'switchboard.setup.selfTest';
const discovery = parseDiscovery(structuredClone(discoverySample));
const templates = parseTemplates(structuredClone(templatesSample)).templates;
/** The maintainer's example as GET api/setup/draft loads it: M1 currents unsigned, direction from the signed powers. */
const example = (): SetupDraft => parseDraft(structuredClone(draftSample).draft);
/** The validation sample as plain JSON, so a test can change any of its values before it is parsed. */
interface RawValidation {
  valid: boolean;
  issues: unknown[];
  checks: { refresh: unknown[] };
  reserve: { perPhase: Record<string, unknown>[]; explanation: Record<string, unknown> };
  selfTest: Record<string, unknown>[];
}
const validation = (change: (raw: RawValidation) => void = () => {}): Validation => {
  const raw = structuredClone(validationSample) as unknown as RawValidation;
  change(raw);
  return parseValidation(raw);
};
const refusalAt = (field: string, code: SetupIssue['code'] = 'entity_not_found'): SetupIssue => ({
  code,
  severity: 'error',
  field,
  documentPath: null,
  message: 'refused',
  args: { entity: 'sensor.solaredge_m1_ac_current_b' },
});

let fetchMock: Mock<typeof fetch>;
/** The live readout of each entity, as GET api/setup/readouts answers for it. */
let readings: Record<string, { state: string; unit: string | null; ageS: number; intervalS: number | null; samples?: number }>;
let session: () => Promise<Response>;
let start: () => Promise<Response>;

function readoutsFor(url: string) {
  const entities = new URLSearchParams(url.split('?')[1] ?? '').getAll('entity');
  return {
    at: '2026-10-22T07:39:00+00:00',
    items: entities.map((entityId) => {
      const r = readings[entityId];
      return r === undefined
        ? { entityId, status: 'pending', state: null, unit: null, lastReported: null, ageS: null, refresh: { intervalS: null, samples: 0, needed: 8 } }
        : {
            entityId,
            status: 'found',
            state: r.state,
            unit: r.unit,
            lastReported: '2026-10-22T07:38:56+00:00',
            ageS: r.ageS,
            refresh: { intervalS: r.intervalS, samples: r.samples ?? 64, needed: 8 },
          };
    }),
  };
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
  window.localStorage.clear();
  window.sessionStorage.clear();
  readings = {};
  session = () => respond(structuredClone(selfTestSample));
  start = () => respond(structuredClone(startedSample), 202);
  fetchMock = vi.fn<typeof fetch>((input, init) => {
    const url = String(input);
    const path = url.split('?')[0]!;
    if (path === 'api/setup/discovery') return respond(structuredClone(discoverySample));
    if (path === 'api/setup/readouts') return respond(readoutsFor(url));
    if (path === 'api/setup/self-test' && init?.method === 'POST') return start();
    if (path.startsWith('api/setup/self-test/') && init?.method === 'DELETE') return respond({}, 204);
    if (path.startsWith('api/setup/self-test/')) return session();
    return respond({}, 404);
  });
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

interface Options {
  draft?: SetupDraft;
  /** What POST api/setup/validate answers for the draft; null when the add-on does not answer. */
  verdict?: (draft: SetupDraft) => Validation | null;
  lang?: Lang;
  issues?: SetupIssue[];
}

/** The step with the shared draft as the wizard holds it: every edit goes through the real reducer. */
function mount({ draft = emptyDraft(), verdict = () => validation(), lang = 'en', issues: initial = [] }: Options = {}) {
  const goTo = vi.fn<(step: StepName) => void>();
  let current: SetupState | undefined;
  function Harness() {
    const [state, dispatch] = useReducer(draftReducer, draft);
    const [issues, setIssues] = useState<SetupIssue[]>(initial);
    const [checked, setChecked] = useState<Validation | null>(null);
    const setup: SetupState = {
      load: { kind: 'ready' },
      view: null,
      draft: state,
      templates,
      prefillVoltageV: 230,
      validation: checked,
      issues,
      busy: 'idle',
      outcome: null,
      unsaved: 'none',
      discardUnsaved: () => undefined,
      set: (path, value) => dispatch({ kind: 'set', path, value }),
      addDevice: (device) => dispatch({ kind: 'addDevice', device }),
      removeDevice: (index) => dispatch({ kind: 'removeDevice', index }),
      validate: () => {
        const result = verdict(state);
        if (result !== null) {
          setChecked(result);
          setIssues(result.issues);
        }
        return Promise.resolve(result);
      },
      save: () => Promise.resolve(null),
      reload: () => {},
      issuesOf: (s) => issuesOfStep(issues, s),
      issuesAt: (f) => issuesAt(issues, f),
    };
    current = setup;
    return <SetupMeter setup={setup} t={messages[lang]} lang={lang} goTo={goTo} />;
  }
  render(<Harness />);
  return { goTo, draft: () => current!.draft };
}

async function settle() {
  await advance();
  await advance();
}

function optionsOf(label: string): HTMLOptionElement[] {
  return [...(screen.getByLabelText(label) as HTMLSelectElement).options];
}

function row(path: string): HTMLElement {
  return document.getElementById(fieldId(path))!;
}

async function click(name: string | RegExp) {
  await act(async () => {
    fireEvent.click(screen.getByRole('button', { name }));
  });
  await settle();
}

// UI-01 / UI-05: the step has its own heading in both languages and shows the add-on's refusals
// at its own fields, never another step's.
describe('SetupMeter', () => {
  it('has its heading in English and Polish', async () => {
    mount();
    await settle();
    expect(screen.getByRole('heading', { name: 'Main meter' })).toBeTruthy();
    cleanup();

    mount({ lang: 'pl' });
    await settle();
    expect(screen.getByRole('heading', { name: 'Licznik główny' })).toBeTruthy();
  });

  it('shows a refusal at one of its fields and not another step’s', async () => {
    const refusal: SetupIssue = { code: 'required_field', severity: 'error', field: 'meter.phases.L1.current.entity', documentPath: null, message: 'required', args: {} };
    mount({ issues: [refusal, { ...refusal, field: 'connection.voltageV' }] });
    await settle();

    const alert = screen.getAllByRole('alert')[0]!;
    expect(alert.querySelectorAll('li')).toHaveLength(1);
    expect(alert.querySelector('li')?.getAttribute('data-field')).toBe('meter.phases.L1.current.entity');
    expect(alert.textContent).toContain('This field is required.');
  });
});

// UI-03: pickers are fed by discovery, filtered by domain and device class (or unit), a derived
// sensor is listed but never selectable (07-PITFALLS), and nothing is mapped until the user picks.
describe('meter pickers', () => {
  it('picker offers only current-class sensors for phase rows', async () => {
    mount({ draft: { ...emptyDraft(), connection: { ...emptyDraft().connection, topology: 'three_phase' } } });
    await settle();

    const values = optionsOf('Current entity of L2').map((o) => o.value);
    expect(values).toContain('sensor.solaredge_m1_ac_current_b');
    expect(values).not.toContain('sensor.solaredge_m1_ac_power_b');
    expect(values).not.toContain('switch.grzalka_1');
    expect((screen.getByLabelText('Current entity of L2') as HTMLSelectElement).value).toBe('');
  });

  it('derived sensor listed disabled with platform as reason', async () => {
    mount();
    await settle();

    const usage = optionsOf('Power entity of L1').find((o) => o.value === 'sensor.usage')!;
    expect(usage.disabled).toBe(true);
    expect(usage.textContent).toBe('sensor.usage — computed by template, not an input');
    const powercalc = optionsOf('Total power entity').find((o) => o.value === 'sensor.kettle_power')!;
    expect(powercalc.disabled).toBe(true);
  });

  it('picking an entity takes its unit from Home Assistant', async () => {
    const { draft } = mount();
    await settle();

    fireEvent.change(screen.getByLabelText('Power entity of L1'), { target: { value: 'sensor.solaredge_m1_ac_power_a' } });
    await settle();

    expect(draft().meter.phases.L1?.power).toEqual({ entity: 'sensor.solaredge_m1_ac_power_a', unit: 'W', maxAge: null });
  });

  it('quick edits of one row’s unit and max age never undo each other', async () => {
    const { draft } = mount({ draft: example() });
    await settle();

    // Raw events, not testing-library's act-wrapped ones: no render happens between them.
    const unit = screen.getByLabelText('Current entity of L1 · Unit') as HTMLSelectElement;
    const maxAge = screen.getByLabelText('Current entity of L1 · Max age (max_age)') as HTMLInputElement;
    unit.value = 'mA';
    unit.dispatchEvent(new Event('change', { bubbles: true }));
    maxAge.value = '45s';
    maxAge.dispatchEvent(new Event('input', { bubbles: true }));
    await settle();

    expect(draft().meter.phases.L1?.current).toEqual({ entity: 'sensor.solaredge_m1_ac_current_a', unit: 'mA', maxAge: '45s' });
  });

  it('the meter device only narrows the lists; its suggestion fills empty fields only when applied', async () => {
    const start = emptyDraft();
    start.connection.topology = 'three_phase';
    start.meter.phases.L2 = { current: { entity: 'sensor.my_own_l2', unit: 'A', maxAge: '30s' }, power: null, voltage: null };
    const { draft } = mount({ draft: start });
    await settle();

    fireEvent.change(screen.getByLabelText('Metering device at the connection'), { target: { value: 'dev_m1' } });
    await settle();
    // Choosing the device narrowed the list and declared nothing.
    expect(optionsOf('Current entity of L1').map((o) => o.value)).toEqual(['', 'sensor.solaredge_m1_ac_current_a', 'sensor.solaredge_m1_ac_current_b', 'sensor.solaredge_m1_ac_current_c']);
    expect(draft().meter.phases.L1).toBeNull();
    expect(document.querySelector('[data-part="suggestion"]')?.textContent).toContain('Phase A as L1 is only a guess');

    await click('Fill in the suggested entities');

    expect(draft().meter.phases.L1?.current).toEqual({ entity: 'sensor.solaredge_m1_ac_current_a', unit: 'A', maxAge: null });
    expect(draft().meter.phases.L3?.voltage?.entity).toBe('sensor.solaredge_m1_ac_voltage_cn');
    expect(draft().meter.total.power?.entity).toBe('sensor.solaredge_m1_ac_power');
    // The user's own pick is never overwritten by a guess.
    expect(draft().meter.phases.L2?.current).toEqual({ entity: 'sensor.my_own_l2', unit: 'A', maxAge: '30s' });
  });

  it('suggests a phase only from a separate name token, and never a derived sensor', () => {
    expect(phaseFromName('sensor.solaredge_m1_ac_current_a')).toBe('L1');
    expect(phaseFromName('sensor.solaredge_m1_ac_voltage_bn')).toBe('L2');
    expect(phaseFromName('sensor.shellypro3em_phase_c_current')).toBe('L3');
    // "m1" names the meter, not phase 1; a total names no phase.
    expect(phaseFromName('sensor.solaredge_m1_ac_power')).toBeNull();
    expect(phaseFromName('sensor.meter_a_b_current')).toBe('several');

    const paths = suggestMapping(discovery.entities, ['L1', 'L2', 'L3']).map((s) => s.entity.entityId);
    expect(paths).not.toContain('sensor.usage');
    expect(paths).not.toContain('sensor.moc_grzalek_cwu');
  });
});

// UI-04, SIG-07: beside every pick its live value, the age of its last report (on the add-on's
// clock, ticking) and the detected refresh; the slowest phase's refresh is the step's refresh.
describe('live readouts', () => {
  it('readout shows value, age and refresh beside each pick', async () => {
    readings = {
      'sensor.solaredge_m1_ac_current_a': { state: '0.80', unit: 'A', ageS: 4, intervalS: 10 },
      'sensor.solaredge_m1_ac_current_b': { state: '1.67', unit: 'A', ageS: 4, intervalS: 10 },
      'sensor.solaredge_m1_ac_current_c': { state: '1.08', unit: 'A', ageS: 4, intervalS: 30 },
    };
    mount({ draft: example() });
    await settle();

    const l1 = row('meter.phases.L1.current');
    expect(l1.querySelector('.value')?.textContent).toBe('0.80 A');
    expect(l1.querySelector('.age')?.textContent).toBe('reported 4 s ago');
    expect(l1.querySelector('.refresh')?.textContent).toBe('every 10 s');
    expect(document.querySelector('[data-part="refresh"]')?.textContent).toContain('30 s (detected)');

    readings['sensor.solaredge_m1_ac_current_a']!.ageS = 6;
    await advance(2_000);
    expect(row('meter.phases.L1.current').querySelector('.age')?.textContent).toBe('reported 6 s ago');
  });

  it('a refresh still being detected says how many reports it has', async () => {
    readings = {
      'sensor.solaredge_m1_ac_current_a': { state: '0.80', unit: 'A', ageS: 4, intervalS: null, samples: 3 },
      'sensor.solaredge_m1_ac_current_b': { state: '1.67', unit: 'A', ageS: 4, intervalS: 10 },
      'sensor.solaredge_m1_ac_current_c': { state: '1.08', unit: 'A', ageS: 4, intervalS: 10 },
    };
    mount({ draft: example(), lang: 'pl' });
    await settle();

    expect(document.querySelector('[data-part="refresh"]')?.textContent).toContain('wykrywanie: 3 z 8 odczytów');
  });
});

// SIG-03 (07-PITFALLS "Unsigned current hides direction") and CONN-01: the direction is asked
// exactly when the current is unsigned, and the layout decides which rows are required.
describe('sign, direction and layout', () => {
  it('asks the direction only for an unsigned current', async () => {
    const signed = example();
    signed.meter.currentSign = 'positive_import';
    signed.meter.currentDirection = null;
    const { draft } = mount({ draft: signed });
    await settle();

    expect(screen.queryByRole('combobox', { name: 'Flow direction entity' })).toBeNull();
    expect(document.body.textContent).toContain('Not needed: the current carries its sign.');

    fireEvent.change(screen.getByLabelText('Direction and sign'), { target: { value: 'unsigned' } });
    await settle();

    const direction = screen.getByRole('combobox', { name: 'Flow direction entity' }) as HTMLSelectElement;
    expect([...direction.options].map((o) => o.textContent)).toEqual([
      'choose',
      'From the sign of each phase’s power (sensor.solaredge_m1_ac_power_a, sensor.solaredge_m1_ac_power_b, sensor.solaredge_m1_ac_power_c)',
      'I have none — cautious mode',
    ]);
    expect(draft().meter.currentDirection).toBeNull();

    fireEvent.change(screen.getByLabelText('Direction and sign'), { target: { value: 'positive_export' } });
    await settle();
    expect(draft().meter.currentDirection).toBeNull();
  });

  it('summed topology requires total current, not phase rows', async () => {
    mount({ draft: { ...emptyDraft(), connection: { ...emptyDraft().connection, topology: 'three_phase_summed' } } });
    await settle();

    expect(within(row('meter.total.current')).getByText('required')).toBeTruthy();
    expect(within(row('meter.phases.L1.current')).getByText('optional')).toBeTruthy();
    expect(document.body.textContent).toContain('a total current entity is needed');
  });

  it('single-phase asks for L1 only and offers to remove a mapped phase outside it', async () => {
    const single = example();
    single.connection.topology = 'single_phase';
    const { draft } = mount({ draft: single });
    await settle();

    expect(within(row('meter.phases.L1.current')).getByText('required')).toBeTruthy();
    expect(row('meter.phases.L3.current').textContent).toContain('Not part of this layout');

    fireEvent.click(within(row('meter.phases.L3.current')).getByRole('button', { name: 'Remove' }));
    await settle();
    expect(draft().meter.phases.L3?.current).toBeNull();
    expect(row('meter.phases.L3.current')).toBeNull();
  });
});

// CONN-06 / UI-05: "Check the measurement and continue" lets the add-on judge the draft, moves on
// only when no step-1 field is refused, and otherwise says which field to fix and focuses it.
describe('check and continue', () => {
  it('continue is refused naming the failed field', async () => {
    const { goTo } = mount({ draft: example(), verdict: () => validation((v) => v.issues.push(refusalAt('meter.phases.L2.current.entity'))) });
    await settle();

    await click('Check the measurement and continue');

    expect(goTo).not.toHaveBeenCalled();
    expect(document.querySelector('[data-refused]')?.textContent).toBe('Not continued: fix “Current entity of L2” first (every field to fix is listed below).');
    expect(document.activeElement?.id).toBe(fieldId('meter.phases.L2.current.entity'));
    expect(row('meter.phases.L2.current').getAttribute('data-invalid')).toBe('true');
    expect(document.querySelector('li[data-field="meter.phases.L2.current.entity"]')?.textContent).toContain('Home Assistant has no entity sensor.solaredge_m1_ac_current_b.');
  });

  it('names the refused field the user meets first on the page, not the add-on’s listing order', async () => {
    // The add-on maps powers before currents, so it lists a power's refusal first.
    const { goTo } = mount({
      draft: example(),
      verdict: () => validation((v) => v.issues.push(refusalAt('meter.total.power.maxAge', 'required_field'), refusalAt('meter.phases.L3.power.maxAge', 'required_field'), refusalAt('meter.phases.L2.current.maxAge', 'required_field'))),
    });
    await settle();

    await click('Check the measurement and continue');

    expect(goTo).not.toHaveBeenCalled();
    expect(document.querySelector('[data-refused]')?.getAttribute('data-refused')).toBe('meter.phases.L2.current.maxAge');
    expect(document.activeElement?.id).toBe(fieldId('meter.phases.L2.current.maxAge'));
  });

  it('continues when only warnings remain on this step', async () => {
    const { goTo } = mount({ draft: example() });
    await settle();

    await click('Check the measurement and continue');

    expect(goTo).toHaveBeenCalledWith('connection');
  });

  it('another step’s refusal does not hold the meter step', async () => {
    const { goTo } = mount({ draft: example(), verdict: () => validation((v) => v.issues.push(refusalAt('connection.voltageV', 'required_field'))) });
    await settle();

    await click('Check the measurement and continue');

    expect(goTo).toHaveBeenCalledWith('connection');
  });

  it('says so when the add-on does not answer, and stays', async () => {
    const { goTo } = mount({ draft: example(), verdict: () => null });
    await settle();

    await click('Check the measurement and continue');

    expect(goTo).not.toHaveBeenCalled();
    expect(document.body.textContent).toContain('The check could not be run: the add-on did not answer.');
  });

  it('a skipped phase sum is shown as skipped, never as passed', async () => {
    mount({ draft: example() });
    await settle();
    await click('Check the measurement and continue');

    const sum = document.querySelector('[data-phase-sum]')!;
    expect(sum.getAttribute('data-phase-sum')).toBe('skipped');
    expect(sum.textContent).toContain('Phase sum: skipped, not passed.');
    expect(sum.textContent).not.toContain('sum to the total:');
  });

  it('a max age far above the detected refresh is a warning at its row that does not hold the step, with the suggestion', async () => {
    // CONN-06 both ways: 30 min on a 10 s meter keeps trusting a frozen poll for half an hour.
    const loose = example();
    loose.meter.phases.L1!.current!.maxAge = '30m';
    const { draft, goTo } = mount({
      draft: loose,
      verdict: () =>
        validation((v) => {
          v.checks.refresh[0] = { field: 'meter.phases.L1.current', detectedS: 10, maxAgeS: 1800, state: 'above_refresh', suggestedMaxAgeS: 60 };
          v.issues.push({
            code: 'max_age_above_refresh',
            severity: 'warning',
            field: 'meter.phases.L1.current.maxAge',
            documentPath: null,
            message: 'too long',
            args: { entity: 'sensor.solaredge_m1_ac_current_a', maxAgeS: '1800', refreshS: '10', suggestedS: '60' },
          });
        }),
    });
    await settle();
    await click('Check the measurement and continue');

    expect(goTo).toHaveBeenCalledWith('connection');
    expect(row('meter.phases.L1.current').querySelector('[data-refresh]')?.textContent).toBe('much longer than the refresh: a frozen value would stay trusted that long');
    fireEvent.click(within(row('meter.phases.L1.current')).getByRole('button', { name: 'Use 60s' }));
    await settle();
    expect(draft().meter.phases.L1?.current?.maxAge).toBe('60s');
  });

  it('a max age shorter than the detected refresh is flagged at its row, and the suggestion fills it', async () => {
    const short = example();
    short.meter.phases.L1!.current!.maxAge = '5s';
    const { draft } = mount({
      draft: short,
      verdict: () =>
        validation((v) => {
          v.checks.refresh[0] = { field: 'meter.phases.L1.current', detectedS: 10, maxAgeS: 5, state: 'below_refresh', suggestedMaxAgeS: 60 };
          v.issues.push({ ...refusalAt('meter.phases.L1.current.maxAge', 'max_age_below_refresh'), args: { entity: 'sensor.solaredge_m1_ac_current_a' } });
          v.valid = false;
        }),
    });
    await settle();
    await click('Check the measurement and continue');

    expect(row('meter.phases.L1.current').querySelector('[data-refresh]')?.textContent).toBe('shorter than the refresh: the value would go stale between reports');
    expect(document.activeElement?.id).toBe(fieldId('meter.phases.L1.current.maxAge'));

    fireEvent.click(within(row('meter.phases.L1.current')).getByRole('button', { name: 'Use 60s' }));
    await settle();
    expect(draft().meter.phases.L1?.current?.maxAge).toBe('60s');
  });
});

/** The POST requests the page sent, by path. */
function posts(): { path: string; body: Record<string, unknown> }[] {
  return fetchMock.mock.calls
    .filter(([, init]) => init?.method === 'POST')
    .map(([url, init]) => ({ path: String(url), body: JSON.parse(String(init!.body)) as Record<string, unknown> }));
}

// CONN-05 (binding: passive). The test is on step 1, never hidden; Switchboard switches nothing and
// says so; the verdict names the phase that stepped; inconclusive is never shown as a pass.
describe('phase assignment test', () => {
  it('says plainly that Switchboard does not switch the device for the test', async () => {
    mount({ draft: example() });
    await settle();

    expect(screen.getByRole('heading', { name: 'Phase assignment test' })).toBeTruthy();
    expect(document.querySelector('[data-part="passive"]')?.textContent).toBe(
      'Switchboard does not switch the device for this test. Switch it yourself in Home Assistant or by hand while the wizard watches the meter.',
    );
    cleanup();

    mount({ draft: example(), lang: 'pl' });
    await settle();
    expect(document.querySelector('[data-part="passive"]')?.textContent).toContain('Switchboard nie przełącza urządzenia na potrzeby tego testu.');
  });

  it('watching a declared device posts the draft and its index; nothing else is sent', async () => {
    session = () => respond({ ...structuredClone(selfTestSample), status: 'watching', observations: [], verdict: { ...selfTestSample.verdict, verdict: 'inconclusive', confirmed: 0 } });
    mount({ draft: example() });
    await settle();

    // Only on/off devices on one phase can be judged; the charger is not offered.
    expect(optionsOf('What you will switch').map((o) => o.textContent)).toEqual([
      'Grzałka CWU L1 (declared on L1)',
      'Grzałka CWU L2 (declared on L2)',
      'Grzałka CWU L3 (declared on L3)',
      'Another on/off load (enter it below)',
    ]);
    fireEvent.change(screen.getByLabelText('What you will switch'), { target: { value: 'device:1' } });
    await click('Start watching');

    expect(posts().map((p) => p.path)).toEqual(['api/setup/self-test']);
    expect(posts()[0]!.body.device).toEqual({ index: 1 });
    expect((posts()[0]!.body.draft as SetupDraft).devices).toHaveLength(4);
    expect(document.querySelector('[data-part="session"]')?.textContent).toContain('Watching switch.grzalka_1 on L2: switch it on now, wait about a minute, then switch it off.');
    expect(screen.getByRole('button', { name: 'Stop watching' })).toBeTruthy();
    expect(window.localStorage.getItem(SESSION_KEY)).toBe(startedSample.id);
  });

  it('a confirmed switching shows the phase that stepped', async () => {
    window.localStorage.setItem(SESSION_KEY, selfTestSample.id);
    mount({ draft: example() });
    await settle();

    const verdict = document.querySelector('[data-part="verdict"]')!;
    expect(verdict.textContent).toContain('confirmed');
    expect(verdict.textContent).toContain('The step showed on the meter’s L2, as declared.');
    expect(document.querySelector('.sm-deltas')?.textContent).toBe('L1 -12 W · L2 +1,701 W · L3 -19 W');
  });

  it('declared L2 but stepped on L3 is a mismatch naming L3', async () => {
    const swapped = structuredClone(selfTestSample);
    swapped.observations[0]!.deltas = { L1: -5, L2: 3, L3: 1702 };
    Object.assign(swapped.observations[0]!, { verdict: 'mismatch', observedPhase: 'L3' });
    Object.assign(swapped.verdict, { verdict: 'mismatch', observedPhase: 'L3', confirmed: 0, mismatched: 1 });
    session = () => respond(swapped);
    window.localStorage.setItem(SESSION_KEY, swapped.id);
    mount({ draft: example() });
    await settle();

    const verdict = document.querySelector('[data-part="verdict"]')!;
    expect(verdict.querySelector('[data-verdict]')?.getAttribute('data-verdict')).toBe('mismatch');
    expect(verdict.textContent).toContain('The step showed on the meter’s L3, not L2: the device is on L3, or the meter’s phase entities are swapped.');
  });

  it('an inconclusive watch is never shown as a pass', async () => {
    const expired = structuredClone(selfTestSample);
    Object.assign(expired, { status: 'expired', observations: [] });
    Object.assign(expired.verdict, { verdict: 'inconclusive', reason: 'timed_out', confirmed: 0 });
    session = () => respond(expired);
    window.localStorage.setItem(SESSION_KEY, expired.id);
    mount({ draft: example() });
    await settle();

    const text = document.querySelector('[data-part="session"]')!.textContent!;
    expect(text).toContain('inconclusive — not a pass');
    expect(text).toContain('Not judged: no switching was seen before the watch ended.');
    expect(text).not.toContain('confirmed');
  });

  it('a test load is judged by the add-on, which names the missing field', async () => {
    start = () =>
      respond(
        { code: 'invalid', issues: [{ code: 'required_field', severity: 'error', field: 'testLoad.ratedPowerW', documentPath: null, message: 'The rating is required.', args: {} }], currentRevision: null },
        422,
      );
    mount({ draft: example() });
    await settle();

    fireEvent.change(screen.getByLabelText('What you will switch'), { target: { value: 'testLoad' } });
    await settle();
    fireEvent.change(screen.getByLabelText('Entity of the load you will switch'), { target: { value: 'switch.grzalka_3' } });
    fireEvent.change(screen.getByLabelText('Its phase, from the switchboard’s labels'), { target: { value: 'L3' } });
    await click('Start watching');

    expect(posts()[0]!.body.testLoad).toEqual({ stateEntity: 'switch.grzalka_3', phase: 'L3', ratedPowerW: null });
    expect(document.querySelector('[data-part="refusal"] li')?.getAttribute('data-field')).toBe('testLoad.ratedPowerW');
    expect(window.localStorage.getItem(SESSION_KEY)).toBeNull();
  });

  it('a test load filled in quickly keeps every part of it', async () => {
    mount({ draft: example() });
    await settle();
    fireEvent.change(screen.getByLabelText('What you will switch'), { target: { value: 'testLoad' } });
    await settle();

    // Raw events, not testing-library's act-wrapped ones: no render happens between them (autofill).
    const entity = screen.getByLabelText('Entity of the load you will switch') as HTMLSelectElement;
    const phase = screen.getByLabelText('Its phase, from the switchboard’s labels') as HTMLSelectElement;
    const power = screen.getByLabelText('Its rated power (W)') as HTMLInputElement;
    entity.value = 'switch.grzalka_1';
    entity.dispatchEvent(new Event('change', { bubbles: true }));
    phase.value = 'L3';
    phase.dispatchEvent(new Event('change', { bubbles: true }));
    power.value = '1700';
    power.dispatchEvent(new Event('input', { bubbles: true }));
    await settle();
    await click('Start watching');

    expect(posts()[0]!.body.testLoad).toEqual({ stateEntity: 'switch.grzalka_1', phase: 'L3', ratedPowerW: 1700 });
  });

  it('stopping the watch cancels the session on the add-on', async () => {
    session = () => respond({ ...structuredClone(selfTestSample), status: 'watching', observations: [] });
    window.localStorage.setItem(SESSION_KEY, selfTestSample.id);
    mount({ draft: example() });
    await settle();

    await click('Stop watching');

    expect(fetchMock.mock.calls.some(([url, init]) => String(url) === `api/setup/self-test/${selfTestSample.id}` && init?.method === 'DELETE')).toBe(true);
  });

  it('a watch the add-on no longer has is reported, never shown as running', async () => {
    session = () => respond({}, 404);
    window.localStorage.setItem(SESSION_KEY, 'gone');
    mount({ draft: example() });
    await settle();

    expect(document.body.textContent).toContain('This test can no longer be read from the add-on');
    expect(window.localStorage.getItem(SESSION_KEY)).toBeNull();
  });

  it('recorded switchings are judged against this draft: a swapped phase shows as a mismatch', async () => {
    const swapped = example();
    swapped.devices[1]!.phase = 'L3';
    mount({
      draft: swapped,
      verdict: () =>
        validation((v) => {
          Object.assign(v.selfTest[1]!, { verdict: 'mismatch', observedPhase: 'L2', reason: null, confirmed: 0, mismatched: 2 });
        }),
    });
    await settle();
    expect(document.querySelector('[data-part="evidence"]')?.textContent).toContain('Run the check above');

    await click('Check the measurement and continue');

    const item = document.querySelector('[data-part="evidence"] li[data-field="devices[1].phase"]')!;
    expect(item.textContent).toContain('The step showed on the meter’s L2, not L3');
    expect(item.textContent).toContain('Grzałka CWU L2 on L3: confirmed 0, mismatched 2, inconclusive 0.');
    // A device nothing was recorded for has no verdict at all, and certainly no pass.
    const empty = document.querySelector('[data-part="evidence"] li[data-field="devices[0].phase"]')!;
    expect(empty.textContent).toContain('No switching recorded yet.');
    expect(empty.querySelector('[data-verdict]')).toBeNull();
  });
});
