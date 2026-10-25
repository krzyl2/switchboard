import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { act, cleanup, render, screen, within } from '@testing-library/preact';
import { App, parseRoute } from './App';
import { parseSetupStep, setupHref } from './Setup';
import draftSample from './api/setup-draft.sample.json';
import refusalSample from './api/setup-refusal.sample.json';
import savedSample from './api/setup-saved.sample.json';
import templatesSample from './api/setup-templates.sample.json';
import validationSample from './api/setup-validation.sample.json';
import { advance, respond } from './screens.test-support';
import { useSetup, type SetupState } from './useSetup';

let fetchMock: Mock<typeof fetch>;

/** The add-on as the wizard sees it: the draft and templates, and POST validate / save answers. */
function serve({ draft = () => respond(structuredClone(draftSample)), save = () => respond(structuredClone(savedSample)) }: { draft?: () => Promise<Response>; save?: () => Promise<Response> } = {}) {
  fetchMock.mockImplementation((input, init) => {
    const url = String(input).split('?')[0];
    if (url === 'api/setup/draft') return draft();
    if (url === 'api/setup/templates') return respond(structuredClone(templatesSample));
    if (url === 'api/setup/validate' && init?.method === 'POST') return respond(structuredClone(validationSample));
    if (url === 'api/setup/save' && init?.method === 'POST') return save();
    return respond({}, 404);
  });
}

async function open(hash = '#/setup', lang: 'en' | 'pl' = 'en') {
  window.location.hash = hash;
  render(<App lang={lang} />);
  await advance();
  await advance();
}

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
  window.location.hash = '';
});

// UI-01: a five-step wizard (meter → connection → sources and storage → loads → mode) at #/setup.
describe('setup wizard shell', () => {
  it('routes #/setup and #/setup/<n> to the wizard', () => {
    expect(parseRoute('#/setup')).toEqual({ page: 'setup', step: 'meter' });
    expect(parseRoute('#/setup/4')).toEqual({ page: 'setup', step: 'loads' });
    expect(parseSetupStep('#/setup/9')).toBe('meter');
    expect(setupHref('mode')).toBe('#/setup/5');
  });

  it('nav 03 links to #/setup', () => {
    render(<App lang="pl" />);

    const link = within(screen.getAllByRole('navigation')[0]!).getByText('Konfiguracja wstępna').closest('a');
    expect(link?.getAttribute('href')).toBe('#/setup');
  });

  it('five steps in mockup order', async () => {
    serve();
    await open('#/setup', 'pl');

    const steps = within(screen.getByRole('navigation', { name: 'Kroki konfiguracji' })).getAllByRole('link');
    expect(steps.map((a) => a.textContent)).toEqual(['1 · Pomiar', '2 · Przyłącze', '3 · Źródła', '4 · Odbiorniki', '5 · Tryb pracy']);
    expect(steps.map((a) => a.getAttribute('href'))).toEqual(['#/setup/1', '#/setup/2', '#/setup/3', '#/setup/4', '#/setup/5']);
    expect(steps[0]?.getAttribute('aria-current')).toBe('step');
    expect(screen.getByRole('heading', { name: 'Licznik główny' })).toBeTruthy();
  });

  it('opens the step the route names, with back and next', async () => {
    serve();
    await open('#/setup/3');

    expect(screen.getByRole('heading', { name: 'Sources and storage' })).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Back' }).getAttribute('href')).toBe('#/setup/2');
    expect(screen.getByRole('link', { name: 'Next' }).getAttribute('href')).toBe('#/setup/4');
  });

  it('says the file has comments a structured save cannot keep, and lists what is kept as written', async () => {
    serve();
    await open();

    expect(document.body.textContent).toContain('The configuration file has comments.');
    expect(document.body.textContent).not.toContain('There is no configuration file yet');
  });

  it('starts a new file on a clean install', async () => {
    serve({ draft: () => respond({ ...structuredClone(draftSample), revision: null, fileState: 'missing', hasComments: false, preserved: [], fileIssues: [], draft: { ...structuredClone(draftSample).draft, devices: [] } }) });
    await open();

    expect(document.body.textContent).toContain('There is no configuration file yet: the wizard starts a new one.');
  });

  it('withdraws the steps and offers a retry when the draft cannot be loaded', async () => {
    serve({ draft: () => respond({}, 500) });
    await open();

    expect(screen.getByRole('alert').textContent).toContain('The configuration cannot be loaded from the add-on.');
    expect(screen.queryByRole('heading', { name: 'Main meter' })).toBeNull();
  });
});

/** The hook alone, as every step uses it. */
function Probe({ onState }: { onState: (state: SetupState) => void }) {
  onState(useSetup());
  return null;
}

async function hook(): Promise<() => SetupState> {
  let current: SetupState | undefined;
  render(<Probe onState={(s) => (current = s)} />);
  await advance();
  await advance();
  return () => current!;
}

// UI-06: the configuration persists and is editable: re-running the wizard starts from the file,
// with every device, and a save sends every device back with the file's revision.
describe('shared draft state', () => {
  it('re-run loads existing devices and keeps them on save', async () => {
    serve();
    const state = await hook();
    expect(state().draft.devices.map((d) => d.id)).toEqual(['heater_l1', 'heater_l2', 'heater_l3', 'ev_charger']);

    act(() => state().set('devices[0].name', 'Grzałka łazienka'));
    await act(async () => {
      await state().save();
    });

    const [, init] = fetchMock.mock.calls.find(([url]) => String(url) === 'api/setup/save')!;
    const body = JSON.parse(String(init?.body));
    expect(body.baseRevision).toBe(draftSample.revision);
    expect(body.draft.devices.map((d: { id: string }) => d.id)).toEqual(['heater_l1', 'heater_l2', 'heater_l3', 'ev_charger']);
    expect(body.draft.devices[0].name).toBe('Grzałka łazienka');
    expect(state().outcome?.kind).toBe('saved');
  });

  it('a refused save shows its issues at their steps', async () => {
    serve({ save: () => respond(structuredClone(refusalSample), 422) });
    const state = await hook();

    await act(async () => {
      await state().save();
    });

    expect(state().outcome?.kind).toBe('refused');
    expect(state().issuesOf('mode').map((i) => i.field)).toContain('acknowledgements.notProtective');
    expect(state().issuesAt('acknowledgements.notProtective')[0]?.code).toBe('acknowledgement_required');
  });

  it('validation posts the draft and keeps the add-on’s verdict', async () => {
    serve();
    const state = await hook();

    await act(async () => {
      await state().validate();
    });

    expect(state().validation?.valid).toBe(true);
    expect(state().issuesOf('loads').length).toBeGreaterThan(0);
    expect(state().templates.map((t) => t.id)).toEqual(['ev_charger', 'water_heater_element', 'heat_pump', 'generic_onoff']);
  });
});

// UI-06: leaving the wizard (another page, or Home Assistant to switch a device for the self-test,
// which unloads the ingress page) never loses unsaved edits; they come back while the file is the
// one they were made on, and are set aside, never applied, once the file changed.
describe('unsaved edits', () => {
  const newHeater = () => ({ ...structuredClone(draftSample).draft.devices[0]!, id: 'heater_extra', existing: false, name: 'Nowa grzałka' });

  it('survive leaving the wizard and coming back', async () => {
    serve();
    const first = await hook();
    act(() => first().addDevice(newHeater()));
    cleanup();

    const again = await hook();

    expect(again().draft.devices.map((d) => d.id)).toEqual(['heater_l1', 'heater_l2', 'heater_l3', 'ev_charger', 'heater_extra']);
    expect(again().unsaved).toBe('restored');
  });

  it('are set aside when the file changed since they were made', async () => {
    serve();
    const first = await hook();
    act(() => first().addDevice(newHeater()));
    cleanup();

    serve({ draft: () => respond({ ...structuredClone(draftSample), revision: 'another-revision' }) });
    const again = await hook();

    expect(again().draft.devices.map((d) => d.id)).toEqual(['heater_l1', 'heater_l2', 'heater_l3', 'ev_charger']);
    expect(again().unsaved).toBe('discarded');
  });

  it('are dropped once saved, and on request', async () => {
    serve();
    const first = await hook();
    act(() => first().addDevice(newHeater()));
    await act(async () => {
      await first().save();
    });
    cleanup();

    const afterSave = await hook();
    expect(afterSave().unsaved).toBe('none');
    act(() => afterSave().addDevice(newHeater()));
    cleanup();

    const restored = await hook();
    expect(restored().unsaved).toBe('restored');
    act(() => restored().discardUnsaved());
    await advance();
    await advance();
    expect(restored().draft.devices).toHaveLength(4);
    expect(restored().unsaved).toBe('none');
  });

  it('the shell says that edits were restored and offers to discard them', async () => {
    serve();
    const first = await hook();
    act(() => first().set('devices[0].name', 'Grzałka łazienka'));
    cleanup();

    await open('#/setup/4');

    expect(document.querySelector('[data-unsaved="restored"]')?.textContent).toContain('Your unsaved changes from earlier in this tab are restored');
    expect(screen.getByRole('button', { name: 'Discard them and load the file again' })).toBeTruthy();
  });
});
