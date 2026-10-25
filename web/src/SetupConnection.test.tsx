import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/preact';
import { useReducer, useState } from 'preact/hooks';
import { SetupConnection } from './SetupConnection';
import { emptyDraft, parseDraft, parseValidation, type SetupDraft, type SetupIssue, type Validation } from './api/setup';
import draftSample from './api/setup-draft.sample.json';
import validationSample from './api/setup-validation.sample.json';
import { messages, type Lang } from './i18n';
import { advance } from './screens.test-support';
import { draftReducer, fieldId, issuesAt, issuesOfStep, type StepName } from './setupDraft';
import type { SetupState } from './useSetup';

/** The maintainer's connection as the example file declares it: 3 × 25 A, B, 230 V, 2500 W reserve per phase. */
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

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

interface Options {
  draft?: SetupDraft;
  /** What POST api/setup/validate answers; null when the add-on does not answer. */
  verdict?: (draft: SetupDraft) => Validation | null;
  lang?: Lang;
  issues?: SetupIssue[];
  prefillVoltageV?: number | null;
}

/** The step with the shared draft as the wizard holds it: every edit goes through the real reducer. */
function mount({ draft = example(), verdict = () => validation(), lang = 'en', issues: initial = [], prefillVoltageV = 230 }: Options = {}) {
  const goTo = vi.fn<(step: StepName) => void>();
  const validate = vi.fn<(draft: SetupDraft) => Validation | null>(verdict);
  let current: SetupState | undefined;
  function Harness() {
    const [state, dispatch] = useReducer(draftReducer, draft);
    const [issues, setIssues] = useState<SetupIssue[]>(initial);
    const [checked, setChecked] = useState<Validation | null>(null);
    const setup: SetupState = {
      load: { kind: 'ready' },
      view: null,
      draft: state,
      templates: [],
      prefillVoltageV,
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
        const result = validate(state);
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
    return <SetupConnection setup={setup} t={messages[lang]} lang={lang} goTo={goTo} />;
  }
  render(<Harness />);
  return { goTo, validate, draft: () => current!.draft };
}

async function settle() {
  await advance();
  await advance();
}

const arithmetic = () => document.querySelector('[data-part="arithmetic"]')!.textContent!;
const minimum = (phase: string) => document.querySelector(`[data-phase="${phase}"] [data-minimum]`);

// UI-01 / UI-05: the step has its own heading in both languages and shows the add-on's refusals
// at its own fields, never another step's.
describe('SetupConnection', () => {
  it('has its heading in English and Polish', () => {
    mount({ draft: emptyDraft() });
    expect(screen.getByRole('heading', { name: 'Connection parameters' })).toBeTruthy();
    cleanup();

    mount({ draft: emptyDraft(), lang: 'pl' });
    expect(screen.getByRole('heading', { name: 'Parametry przyłącza' })).toBeTruthy();
  });

  it('shows a refusal at one of its fields and not another step’s', () => {
    const refusal: SetupIssue = { code: 'required_field', severity: 'error', field: 'connection.reserveW.L1', documentPath: null, message: 'required', args: {} };
    mount({ draft: emptyDraft(), issues: [refusal, { ...refusal, field: 'meter.powerSign' }] });

    const alert = screen.getByRole('alert');
    expect(alert.querySelectorAll('li')).toHaveLength(1);
    expect(alert.querySelector('li')?.getAttribute('data-field')).toBe('connection.reserveW.L1');
    expect(alert.textContent).toContain('This field is required.');
  });
});

// CONN-04 and 04 §1: the reserve is explained with the user's own numbers, from the add-on's
// ReserveExplanation (the runtime's tier table), and the sentence follows the band the kettle lands
// in. 04 §1's "27.8 A = 1.11×" is not what the model computes for 2500 W: it is 22.8 A = 0.91×.
describe('kettle arithmetic', () => {
  it('reserve copy uses the user’s numbers', async () => {
    mount();
    await settle();

    expect(arithmetic()).toContain('25 A on a phase is 5.75 kW.');
    expect(arithmetic()).toContain('With a reserve of 2.50 kW, the system distributes at most 3.25 kW per phase among its own loads.');
    expect(arithmetic()).toContain('brings the current to about 22.8 A, i.e. 0.91 × the breaker rating.');
    expect(document.querySelector('[data-part="band"]')?.textContent).toBe('That stays under the breaker rating: nothing needs to be done.');
    expect(arithmetic()).toContain('the reserve may be smaller than the largest appliance');
    cleanup();

    mount({ lang: 'pl' });
    await settle();
    expect(arithmetic()).toContain('25 A na fazie to 5,75 kW.');
    expect(arithmetic()).toContain('da prąd około 22,8 A, czyli 0,91 × prąd bezpiecznika.');
  });

  it('a reserve at the minimum lands in a reduce band: tolerated for its time, a reduction planned, only recorded', async () => {
    const atMinimum = example();
    atMinimum.connection.reserveW = { L1: 631.5, L2: 631.5, L3: 631.5 };
    mount({
      draft: atMinimum,
      verdict: () =>
        validation((v) => {
          Object.assign(v.reserve!.explanation, { reserveW: 631.5, allocatableW: 5118.5, worstCaseA: 30.95, multiple: 1.238, reaction: 'reduce_next_cycle', allowedS: 180 });
        }),
    });
    await settle();

    expect(arithmetic()).toContain('about 31.0 A, i.e. 1.24 × the breaker rating.');
    expect(document.querySelector('[data-part="band"]')?.textContent).toBe(
      'The breaker curve tolerates that for up to 3 min; within that time the system plans a reduction (while control is not in force it only records it).',
    );
  });

  it('a reserve too small for one kettle says it would force a shed', async () => {
    mount({ verdict: () => validation((v) => Object.assign(v.reserve!.explanation, { reaction: 'shed_one_immediately', allowedS: 30 })) });
    await settle();

    expect(document.querySelector('[data-part="band"]')?.textContent).toBe('The reserve is too small: a kettle would force a shed (while control is not in force the system only records it).');
  });

  it('asks the add-on again shortly after a change, not on every keystroke', async () => {
    const { validate } = mount();
    await settle();
    expect(validate).toHaveBeenCalledTimes(1);

    fireEvent.input(screen.getByLabelText('Reserve on L1 (W)'), { target: { value: '2000' } });
    await advance(400);
    fireEvent.input(screen.getByLabelText('Reserve on L1 (W)'), { target: { value: '1800' } });
    await advance(400);
    expect(validate).toHaveBeenCalledTimes(1);
    expect(arithmetic()).toContain('Recalculating with your change…');

    await advance(800);
    expect(validate).toHaveBeenCalledTimes(2);
    expect(validate.mock.calls[1]![0].connection.reserveW).toEqual({ L1: 1800, L2: 2500, L3: 2500 });
  });

  it('quick edits of several phases never undo each other', async () => {
    // Autofill or a fast user changes all three before the page renders again; each edit must survive.
    const { draft } = mount();
    await settle();

    // Raw events, not testing-library's act-wrapped ones: no render happens between them.
    for (const [phase, value] of [['L1', '2100'], ['L2', '2200'], ['L3', '2300']]) {
      const input = screen.getByLabelText(`Reserve on ${phase} (W)`) as HTMLInputElement;
      input.value = value;
      input.dispatchEvent(new Event('input', { bubbles: true }));
    }
    await settle();

    expect(draft().connection.reserveW).toEqual({ L1: 2100, L2: 2200, L3: 2300 });
  });

  it('an empty reserve hides the arithmetic and is refused at its field', async () => {
    const empty = example();
    empty.connection.reserveW = null;
    const { goTo, validate } = mount({ draft: empty });
    await settle();

    expect(validate).not.toHaveBeenCalled();
    expect(arithmetic()).toContain('The arithmetic appears once the layout, breaker rating, characteristic, voltage and every reserve are entered.');

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Check and continue' }));
    });
    await settle();

    expect(goTo).not.toHaveBeenCalled();
    expect(document.querySelector('[data-refused]')?.textContent).toContain('Not continued: fix “Reserve on L1 (W)” first.');
    expect(document.activeElement?.id).toBe(fieldId('connection.reserveW.L1'));
  });
});

// SIG-07: the minimum safe reserve derived from the detected refresh is shown beside each reserve,
// with where it came from; below it is flagged and never enforced.
describe('minimum safe reserve', () => {
  it('minimum safe reserve shown next to reserve, with the refresh it is derived from', async () => {
    mount({
      verdict: () =>
        validation((v) => {
          Object.assign(v.reserve!.perPhase[2]!, { minimumSafeW: 1321.5, refreshS: 90, refreshDetected: false });
        }),
    });
    await settle();

    expect(minimum('L1')?.textContent).toBe('Minimum safe reserve 631.5 W, from a refresh every 10 s (detected).');
    expect(minimum('L3')?.textContent).toBe('Minimum safe reserve 1,321.5 W, from the max age of 90 s (refresh not detected yet: the worst case).');
  });

  it('a reserve below the minimum is flagged, never enforced', async () => {
    const low = example();
    low.connection.reserveW = { L1: 400, L2: 2500, L3: 2500 };
    const { goTo } = mount({
      draft: low,
      verdict: () =>
        validation((v) => {
          Object.assign(v.reserve!.perPhase[0]!, { reserveW: 400, belowMinimum: true });
          v.issues.push({ code: 'reserve_below_minimum', severity: 'warning', field: 'connection.reserveW.L1', documentPath: null, message: '', args: { phase: 'L1', reserveW: '400', minimumW: '632' } });
        }),
    });
    await settle();

    expect(minimum('L1')?.textContent).toContain('below the minimum safe reserve — shown, never enforced');
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Check and continue' }));
    });
    await settle();
    expect(goTo).toHaveBeenCalledWith('sources');
  });
});

// CONN-01: the layout and breaker rating (asked first in step 1) are summarized with a way back;
// the voltage starts at the add-on's prefill, the characteristic is never prefilled.
describe('connection fields', () => {
  it('voltage prefilled, characteristic never', async () => {
    const fresh = emptyDraft();
    fresh.connection.topology = 'three_phase';
    const { draft } = mount({ draft: fresh });
    await settle();

    expect(draft().connection.voltageV).toBe(230);
    expect((screen.getByLabelText('Phase voltage') as HTMLInputElement).value).toBe('230');
    expect(draft().connection.characteristic).toBeNull();
    expect((screen.getByLabelText('Characteristic') as HTMLSelectElement).value).toBe('');
  });

  it('summarizes the layout and breaker rating with a way back to step 1', async () => {
    const { goTo } = mount();
    await settle();

    const layout = document.querySelector('[data-part="layout"]')!.textContent!;
    expect(layout).toContain('Three-phase, a limit on each phase');
    expect(layout).toContain('25 A per phase');
    fireEvent.click(screen.getByRole('button', { name: 'Change in step 1' }));
    expect(goTo).toHaveBeenCalledWith('meter');
  });

  it('single-phase asks the reserve of L1 only', async () => {
    const single = example();
    single.connection.topology = 'single_phase';
    mount({ draft: single });
    await settle();

    expect(screen.getByLabelText('Reserve on L1 (W)')).toBeTruthy();
    expect(screen.queryByLabelText('Reserve on L2 (W)')).toBeNull();
  });

  it('continue is refused naming the failed step-2 field', async () => {
    const { goTo } = mount({
      verdict: () =>
        validation((v) => {
          v.issues.push({ code: 'document_invalid', severity: 'error', field: 'connection.characteristic', documentPath: 'connection.characteristic', message: 'not a characteristic', args: {} });
        }),
    });
    await settle();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Check and continue' }));
    });
    await settle();

    expect(goTo).not.toHaveBeenCalled();
    expect(document.querySelector('[data-refused]')?.textContent).toBe('Not continued: fix “Characteristic” first.');
    expect(document.activeElement?.id).toBe(fieldId('connection.characteristic'));
  });

  it('says so when the add-on does not answer, and stays', async () => {
    const { goTo } = mount({ verdict: () => null });
    await settle();

    expect(arithmetic()).toContain('The add-on did not answer, so the numbers cannot be shown.');
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Check and continue' }));
    });
    await settle();
    expect(goTo).not.toHaveBeenCalled();
  });
});
