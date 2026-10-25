import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/preact';
import { useReducer, useState } from 'preact/hooks';
import { SetupMode } from './SetupMode';
import { emptyDraft, parseDraftView, parseSaved, type DraftView, type SaveOutcome, type SetupDraft, type SetupIssue } from './api/setup';
import draftSample from './api/setup-draft.sample.json';
import modeSample from './api/mode.sample.json';
import savedSample from './api/setup-saved.sample.json';
import { messages } from './i18n';
import { draftReducer, issuesAt, issuesOfStep, newDevice, type StepName } from './setupDraft';
import { advance, respond } from './screens.test-support';
import type { SetupState } from './useSetup';

// Step 5 (Mode) and the finish flow. The rules under test: shadow is the only mode that can be
// chosen in this version (no command path exists); the wizard cannot finish without the
// not-a-protective-device acknowledgement (SAFE-06); a refused save says what to fix and where
// (UI-05); a save shows what it writes, keeps every device the user did not remove, and confirms the
// hot reload (UI-06); the mode choice is recorded only after a save, and only as shadow (MODE-02).

let fetchMock: Mock<typeof fetch>;
let goTo: Mock<(step: StepName) => void>;
let reload: Mock<() => void>;
let saves: SetupDraft[];
let calls: string[];

/** The add-on's GET and POST api/mode; every request is logged in order with the saves. */
function serveMode({ mode = modeSample as unknown, post = () => respond(structuredClone(modeSample)) }: { mode?: unknown; post?: () => Promise<Response> } = {}) {
  fetchMock.mockImplementation((input, init) => {
    if (String(input) !== 'api/mode') return respond({}, 404);
    if (init?.method === 'POST') {
      calls.push(`mode ${String(init.body)}`);
      return post();
    }
    return respond(structuredClone(mode));
  });
}

/** The wizard's file as loaded from deploy/examples: four devices, comments, a revision. */
function existingView(): DraftView {
  return parseDraftView(structuredClone(draftSample));
}

/** A clean install: no file yet. */
function cleanView(): DraftView {
  return { revision: null, fileState: 'missing', hasComments: false, unreadable: false, draft: emptyDraft(), preserved: [], fileIssues: [] };
}

function issue(code: SetupIssue['code'], field: string | null, args: Record<string, string> = {}, extra: Partial<SetupIssue> = {}): SetupIssue {
  return { code, severity: 'error', field, documentPath: null, message: `${code} (add-on text)`, args, ...extra };
}

type Saved = Extract<SaveOutcome, { kind: 'saved' }>;

function saved(overrides: Partial<Saved> = {}): Saved {
  return { ...(parseSaved(structuredClone(savedSample)) as Saved), reloaded: true, loadedStatus: 'valid', ...overrides };
}

interface Options {
  view?: DraftView;
  draft?: SetupDraft;
  issues?: SetupIssue[];
  answer?: () => Promise<SaveOutcome | null>;
  lang?: 'en' | 'pl';
}

/** The step inside a working copy of the wizard's shared state: real draft reducer, a scripted save. */
function Harness({ view, draft: initial, issues: initialIssues, answer, lang }: Required<Options>) {
  const [draft, dispatch] = useReducer(draftReducer, initial);
  const [issues, setIssues] = useState(initialIssues);
  const [outcome, setOutcome] = useState<SaveOutcome | null>(null);
  const [busy, setBusy] = useState<SetupState['busy']>('idle');
  const state: SetupState = {
    load: { kind: 'ready' },
    view,
    draft,
    templates: [],
    prefillVoltageV: 230,
    validation: null,
    issues,
    busy,
    outcome,
    unsaved: 'none',
    discardUnsaved: () => undefined,
    set: (path, value) => dispatch({ kind: 'set', path, value }),
    addDevice: (device) => dispatch({ kind: 'addDevice', device }),
    removeDevice: (index) => dispatch({ kind: 'removeDevice', index }),
    validate: () => Promise.resolve(null),
    save: async () => {
      saves.push(draft);
      calls.push('save');
      setBusy('saving');
      try {
        const result = await answer();
        if (result !== null) {
          setOutcome(result);
          if (result.kind !== 'saved') setIssues(result.issues);
        }
        return result;
      } finally {
        setBusy('idle');
      }
    },
    reload,
    issuesOf: (step) => issuesOfStep(issues, step),
    issuesAt: (field) => issuesAt(issues, field),
  };
  return <SetupMode setup={state} t={messages[lang]} lang={lang} goTo={goTo} />;
}

async function mount({ view = existingView(), draft, issues = [], answer = () => Promise.resolve(saved()), lang = 'en' }: Options = {}) {
  render(<Harness view={view} draft={draft ?? view.draft} issues={issues} answer={answer} lang={lang} />);
  await advance();
}

const finishButton = (name = 'Finish setup') => screen.getByRole('button', { name }) as HTMLButtonElement;
const checkbox = (name: RegExp) => screen.getByRole('checkbox', { name }) as HTMLInputElement;

async function finish() {
  fireEvent.click(finishButton());
  await advance();
  await advance();
  await advance();
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
  fetchMock = vi.fn<typeof fetch>();
  vi.stubGlobal('fetch', fetchMock);
  goTo = vi.fn<(step: StepName) => void>();
  reload = vi.fn<() => void>();
  saves = [];
  calls = [];
  serveMode();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

// UI-01: the step has its own heading in both languages, with the mockup's vocabulary.
describe('SetupMode', () => {
  it('has its heading in English and Polish', async () => {
    await mount();
    expect(screen.getByRole('heading', { name: 'Operating mode' })).toBeTruthy();
    cleanup();

    await mount({ lang: 'pl' });
    expect(screen.getByRole('heading', { name: 'Tryb pracy' })).toBeTruthy();
    expect(finishButton('Zakończ konfigurację')).toBeTruthy();
  });

  // UI-05: a refusal at one of this step's fields is shown here; another step's is not.
  it('shows a refusal at one of its fields and not another step’s', async () => {
    await mount({ issues: [issue('required_field', 'acknowledgements.notProtective'), issue('required_field', 'connection.voltageV')] });

    const alert = screen.getByRole('alert');
    expect(alert.querySelectorAll('li')).toHaveLength(1);
    expect(alert.querySelector('li')?.getAttribute('data-field')).toBe('acknowledgements.notProtective');
    expect(alert.textContent).toContain('This field is required.');
  });

  it('a refusal at its own field focuses that field in place', async () => {
    await mount({ issues: [issue('acknowledgement_required', 'acknowledgements.notProtective')] });

    fireEvent.click(within(screen.getByRole('alert')).getByRole('link'));

    expect(document.activeElement).toBe(checkbox(/Switchboard is not a protective device/));
    expect(checkbox(/Switchboard is not a protective device/).getAttribute('aria-invalid')).toBe('true');
    expect(goTo).not.toHaveBeenCalled();
  });
});

// Hard constraint: no command path exists, so active is shown (the product has it) but can never be
// chosen, and the reason is said; shadow is the default and the only choice.
describe('mode choice', () => {
  it('shadow is chosen; active is visible but disabled, with the reason', async () => {
    await mount();

    const shadow = screen.getByRole('radio', { name: /Shadow — recommended to start/ }) as HTMLInputElement;
    const active = screen.getByRole('radio', { name: /Active control/ }) as HTMLInputElement;
    expect(shadow.checked).toBe(true);
    expect(active.disabled).toBe(true);
    expect(active.checked).toBe(false);
    const reason = document.getElementById(active.getAttribute('aria-describedby')!);
    expect(reason?.textContent).toContain('Not available in this version: commanding is not enabled');
    // Nothing on the step can ask the add-on for active: no mode request is made before a save.
    expect(calls).toEqual([]);
  });

  it('uses the mockup’s Polish names for the two modes', async () => {
    await mount({ lang: 'pl' });

    expect((screen.getByRole('radio', { name: /Obserwacja — zalecane na start/ }) as HTMLInputElement).checked).toBe(true);
    expect((screen.getByRole('radio', { name: /Sterowanie aktywne/ }) as HTMLInputElement).disabled).toBe(true);
  });

  // MODE-02: the choice and the effective mode are shown as the add-on reports them, so an active
  // add-on option never reads as active control.
  it('shows the add-on option, the choice and the mode in force as reported', async () => {
    serveMode({ mode: { ceiling: 'active', uiChoice: 'shadow', effective: 'shadow', note: 'active_not_enabled' } });
    await mount();

    expect(document.querySelector('[data-mode]')?.textContent).toBe('In force now: Shadow · add-on option: Active · your choice: Shadow');
  });

  it('says so when the mode cannot be read, without guessing one', async () => {
    fetchMock.mockImplementation(() => respond({}, 500));
    await mount();

    expect(document.querySelector('[data-mode]')?.getAttribute('data-mode')).toBe('unavailable');
    expect(document.body.textContent).not.toContain('In force now');
  });
});

// SAFE-06: the wizard cannot be completed without acknowledging that Switchboard is not a
// protective device; a file whose comments or content a save would drop needs its own confirmation.
describe('acknowledgements before finishing', () => {
  it('finish is disabled until the not-a-protective-device statement is acknowledged', async () => {
    await mount({ view: cleanView() });

    expect(screen.getByRole('note').textContent).toContain('This is not a protective device.');
    expect(screen.getByRole('note').textContent).toContain('in this version it only records what it would do');
    expect(finishButton().disabled).toBe(true);
    expect(document.getElementById('smode-finish-needs')?.textContent).toContain('confirm the statement that Switchboard is not a protective device');

    fireEvent.click(checkbox(/Switchboard is not a protective device/));
    await advance();

    expect(finishButton().disabled).toBe(false);
    expect(document.getElementById('smode-finish-needs')).toBeNull();
  });

  it('a file with comments also needs the comments confirmation; the backup keeps them', async () => {
    await mount();

    expect(document.body.textContent).toContain('the original, with its comments, is kept next to it as a backup');
    fireEvent.click(checkbox(/Switchboard is not a protective device/));
    await advance();
    expect(finishButton().disabled).toBe(true);
    expect(document.getElementById('smode-finish-needs')?.textContent).toContain('confirm that the comments are not kept');

    fireEvent.click(checkbox(/Save without the comments/));
    await advance();
    expect(finishButton().disabled).toBe(false);
  });

  it('a file without comments asks for no comments confirmation', async () => {
    await mount({ view: cleanView() });

    expect(screen.queryByRole('checkbox', { name: /Save without the comments/ })).toBeNull();
  });

  it('an unreadable file needs the confirmation that saving replaces it', async () => {
    await mount({ view: { ...cleanView(), revision: 'ABC', fileState: 'invalid', unreadable: true } });

    fireEvent.click(checkbox(/Switchboard is not a protective device/));
    await advance();
    expect(finishButton().disabled).toBe(true);

    fireEvent.click(checkbox(/Replace the unreadable file/));
    await advance();
    expect(finishButton().disabled).toBe(false);
  });
});

async function acknowledgeAll() {
  fireEvent.click(checkbox(/Switchboard is not a protective device/));
  const comments = screen.queryByRole('checkbox', { name: /Save without the comments/ });
  if (comments !== null) fireEvent.click(comments);
  await advance();
}

// UI-06 and MODE-02: finishing writes the file through the add-on, says where the old one went and
// whether the running add-on loaded it, then records the (shadow) mode choice.
describe('finishing', () => {
  it('saves with the acknowledgements, then records shadow, and confirms the hot reload and the backup', async () => {
    await mount();
    await acknowledgeAll();

    await finish();

    expect(calls).toEqual(['save', 'mode {"uiChoice":"shadow"}']);
    expect(saves[0]?.acknowledgements).toEqual({ notProtective: true, commentsDropped: true, replaceUnreadable: false });
    expect(saves[0]?.devices.map((d) => d.id)).toEqual(['heater_l1', 'heater_l2', 'heater_l3', 'ev_charger']);
    const status = screen.getByRole('status', { name: /Configuration saved/ });
    expect(status.textContent).toContain('The running add-on loaded it without a restart.');
    expect(within(status).getByText(savedSample.backup).classList.contains('mono')).toBe(true);
    expect(status.textContent).toContain('Mode recorded: Shadow. In force: Shadow.');
    expect(screen.queryByRole('button', { name: 'Finish setup' })).toBeNull();

    fireEvent.click(within(status).getByRole('button', { name: 'Edit again (loads the saved file)' }));
    expect(reload).toHaveBeenCalledOnce();
  });

  it('a save the add-on has not confirmed loading says so and never claims it loaded', async () => {
    await mount({ answer: () => Promise.resolve(saved({ reloaded: false, loadedStatus: null })) });
    await acknowledgeAll();

    await finish();

    const status = screen.getByRole('status', { name: /Configuration saved/ });
    expect(status.querySelector('[data-reload]')?.getAttribute('data-reload')).toBe('not_confirmed');
    expect(status.textContent).toContain('has not confirmed loading it yet');
    expect(status.textContent).not.toContain('loaded it without a restart');
  });

  it('a clean install has no backup to name', async () => {
    await mount({ view: cleanView(), answer: () => Promise.resolve(saved({ backup: null })) });
    await acknowledgeAll();

    await finish();

    expect(screen.getByRole('status', { name: /Configuration saved/ }).textContent).toContain('There was no previous file, so there is no backup.');
  });

  it('an unchanged save says nothing was written', async () => {
    const view = existingView();
    await mount({ view, answer: () => Promise.resolve(saved({ revision: view.revision!, backup: null })) });
    await acknowledgeAll();

    await finish();

    const status = screen.getByRole('status', { name: /Configuration saved/ });
    expect(status.textContent).toContain('Nothing had changed');
    expect(status.querySelector('[data-reload]')).toBeNull();
  });

  // UI-05: a refused save writes nothing and lists every error with the step it is on; each one opens
  // that step and its field. Warnings do not block, so they are not part of the refusal.
  it('a refused save names each field and its step, opens it, and records no mode', async () => {
    const refusal: SetupIssue[] = [
      issue('phase_self_test_mismatch', 'devices[1].phase', { device: 'Grzałka CWU L2', observed: 'L3', declared: 'L2' }),
      issue('entity_not_found', 'meter.phases.L1.current.entity', { entity: 'sensor.solaredge_m1_ac_current_x' }),
      issue('document_invalid', null, { issue: 'tier_envelope_violated' }, { documentPath: 'control.breaker.tiers', message: 'The tier table lies outside the safety envelope.' }),
      issue('phase_sum_skipped', 'meter.total', {}, { severity: 'warning' }),
      issue('acknowledgement_required', 'acknowledgements.notProtective'),
    ];
    await mount({ answer: () => Promise.resolve({ kind: 'refused', issues: refusal }) });
    await acknowledgeAll();

    await finish();

    expect(calls).toEqual(['save']);
    const alert = screen.getAllByRole('alert').find((a) => a.textContent?.includes('Not saved'))!;
    const items = [...alert.querySelectorAll('li')];
    expect(items.map((li) => li.getAttribute('data-issue'))).toEqual(['phase_self_test_mismatch', 'entity_not_found', 'document_invalid']);
    expect(items[0]?.textContent).toContain('When Grzałka CWU L2 was switched on and off, the meter’s L3 moved with it, not L2');
    expect(items[0]?.textContent).toContain('4 · Loads');
    expect(items[1]?.textContent).toContain('1 · Meter');
    expect(items[2]?.textContent).toContain('in the file');
    expect(items[2]?.textContent).toContain('control.breaker.tiers');
    expect(items[2]?.textContent).toContain('The tier table lies outside the safety envelope.');
    // This step's own refused field stands at its checkbox, not in the list of other steps' fields.
    expect(checkbox(/Switchboard is not a protective device/).getAttribute('aria-invalid')).toBe('true');
    expect(screen.queryByRole('status', { name: /Configuration saved/ })).toBeNull();

    fireEvent.click(within(items[0]!).getByRole('link'));
    expect(goTo).toHaveBeenCalledWith('loads');
  });

  it('a revision conflict offers to load the file again', async () => {
    await mount({ answer: () => Promise.resolve({ kind: 'conflict', currentRevision: 'NEW', issues: [issue('revision_conflict', null)] }) });
    await acknowledgeAll();

    await finish();

    const alert = screen.getAllByRole('alert').find((a) => a.textContent?.includes('Not saved'))!;
    expect(alert.textContent).toContain('The configuration file changed since the wizard loaded it');
    fireEvent.click(within(alert).getByRole('button', { name: 'Load the configuration again' }));
    expect(reload).toHaveBeenCalledOnce();
    expect(calls).toEqual(['save']);
  });

  it('no answer from the add-on says it is unknown whether it saved, and records no mode', async () => {
    await mount({ answer: () => Promise.resolve(null) });
    await acknowledgeAll();

    await finish();

    const alert = screen.getByRole('alert');
    expect(alert.textContent).toContain('it is not known whether the file was saved');
    expect(within(alert).getByRole('button', { name: 'Load the configuration again' })).toBeTruthy();
    expect(calls).toEqual(['save']);
    expect(finishButton().disabled).toBe(false);
  });

  it('a mode choice the add-on refuses is reported, and the save still stands', async () => {
    serveMode({ post: () => respond({ code: 'active_not_enabled' }, 409) });
    await mount();
    await acknowledgeAll();

    await finish();

    const status = screen.getByRole('status', { name: /Configuration saved/ });
    expect(status.textContent).toContain('The add-on did not record the mode choice (active_not_enabled).');
  });
});

// UI-06: before writing, the user sees what will be written; a re-run never loses a device silently.
describe('review of what will be saved', () => {
  it('lists every device of a re-run with its id, marks a new one, and names a removed one', async () => {
    const view = existingView();
    const draft: SetupDraft = {
      ...view.draft,
      devices: [...view.draft.devices.filter((d) => d.id !== 'heater_l3'), { ...newDevice({ type: 'onoff' }, 'generic_onoff'), id: 'pump', name: 'Pompa', phase: 'three_phase', actuator: 'switch.pompa' }],
    };
    await mount({ view, draft });

    const review = screen.getByRole('region', { name: 'What will be saved' });
    expect(review.textContent).toContain('switchboard.yaml in the add-on configuration folder is replaced');
    const rows = [...review.querySelectorAll('[data-review="devices"] li[data-device]')];
    expect(rows.map((li) => li.getAttribute('data-device'))).toEqual(['heater_l1', 'heater_l2', 'ev_charger', 'pump']);
    expect(rows[0]?.textContent).toContain('already in the file');
    expect(rows[0]?.textContent).toContain('on/off · L1');
    expect(within(rows[0] as HTMLElement).getByText('switch.grzalka_2').classList.contains('mono')).toBe(true);
    expect(rows[3]?.textContent).toContain('new');
    expect(rows[3]?.textContent).toContain('three-phase');
    expect(review.querySelector('[data-removed]')?.textContent).toBe('Removed from the file: heater_l3. The backup keeps them.');
    expect(within(review).getByText('sensor.solaredge_m1_ac_current_a').classList.contains('mono')).toBe(true);
    expect(review.querySelector('[data-review="reserve"]')?.textContent).toBe('L1 2,500 W · L2 2,500 W · L3 2,500 W');
    expect(review.textContent).toContain('The mode is not written to the file');
  });

  it('a clean install says a new file is created and inherited values stay defaults', async () => {
    await mount({ view: cleanView() });

    const review = screen.getByRole('region', { name: 'What will be saved' });
    expect(review.textContent).toContain('A new switchboard.yaml is created');
    expect(review.querySelector('[data-review="reserve"]')?.textContent).toBe('default');
    expect(review.querySelector('[data-review="devices"]')?.textContent).toContain('No load is declared');
    expect(review.querySelector('[data-removed]')).toBeNull();
  });

  // A preserved section the runtime refuses would block every save at a path the wizard does not
  // edit; it can be removed here (confirmed, the backup keeps it). A valid one offers no removal.
  it('an invalid kept section can be removed with confirmation; a valid one cannot', async () => {
    const view: DraftView = {
      ...cleanView(),
      revision: 'ABC',
      fileState: 'invalid',
      preserved: [
        { path: 'control', summary: 'control overrides' },
        { path: 'circuit_groups', summary: '1 circuit group' },
      ],
    };
    const invalid = issue('document_invalid', null, { issue: 'tier_envelope_violated' }, { documentPath: 'control.breaker.tiers' });
    await mount({ view, issues: [invalid] });

    const kept = screen.getByRole('region', { name: 'What will be saved' }).querySelector('[data-review="preserved"]')!;
    expect(kept.querySelector('li[data-path="circuit_groups"] input')).toBeNull();
    const drop = within(kept.querySelector('li[data-path="control"]') as HTMLElement).getByRole('checkbox', { name: /Remove this section when saving/ });

    fireEvent.click(drop);
    fireEvent.click(checkbox(/Switchboard is not a protective device/));
    await advance();
    await finish();

    expect(saves[0]?.dropPreserved).toEqual(['control']);
  });
});
