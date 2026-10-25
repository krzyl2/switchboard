import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/preact';
import { App, parseRoute } from './App';
import modeSample from './api/mode.sample.json';
import { MODE_REFRESH_INTERVAL_MS } from './config';
import { THEME_STORAGE_KEY } from './theme';

let fetchMock: Mock<typeof fetch>;

type ModePayload = typeof modeSample;

/** Fresh install: option shadow, control off (api/mode.sample.json with a valid configuration). */
const SHADOW: ModePayload = { ...modeSample, configurationValid: true, missing: ['option_shadow', 'control_off'] };
const ACTIVE: ModePayload = { ...modeSample, ceiling: 'active', controlAllowed: true, configurationValid: true, effective: 'active', missing: [] };

/** Answers api/mode (the shell's banner) with the given payload; every other request stays pending. */
function serveMode(mode: unknown, status = 200) {
  fetchMock.mockImplementation((input) =>
    input === 'api/mode'
      ? Promise.resolve({ ok: status === 200, status, json: () => Promise.resolve(structuredClone(mode)) } as Response)
      : new Promise<Response>(() => undefined),
  );
}

function banner(): HTMLElement {
  const element = document.querySelector<HTMLElement>('.pagehead [data-mode]');
  if (element === null) throw new Error('No mode banner in the page header.');
  return element;
}

async function advance(ms = 0): Promise<void> {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
  fetchMock = vi.fn<typeof fetch>(() => new Promise<Response>(() => undefined));
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.useRealTimers();
  window.location.hash = '';
  window.localStorage.clear();
  document.documentElement.removeAttribute('data-theme');
});

const ROUTES = ['#/', '#/flows', '#/setup', '#/setup/5', '#/devices', '#/devices/heater_l1', '#/guard', '#/log', '#/signals', '#/status'];

// The runtime shell: the mockup's navigation, on every page the mode banner (MODE-02: whether
// Switchboard switches anything right now, as the add-on reports it) and the statement that this is
// not a protective device, and the theme toggle (UI-13).
describe('shell', () => {
  // A banner that guessed would be a lie in one of the two directions: "switches nothing" while it
  // sheds, or "active" while nothing goes out. Until api/mode answers it says it is reading.
  it.each(ROUTES)('%s shows the mode only once the add-on reported it, and that Switchboard is not a protective device', async (hash) => {
    serveMode(SHADOW);
    window.location.hash = hash;
    render(<App lang="en" />);

    expect(banner().textContent).toBe('Reading the mode…');
    expect(screen.getByRole('note').textContent).toContain('Not a protective device.');

    await advance();
    expect([banner().getAttribute('data-mode'), banner().textContent]).toEqual(['shadow', 'Shadow mode — Switchboard switches nothing']);
  });

  it('says active control only when the add-on reports the mode in force as active', async () => {
    serveMode(ACTIVE);
    render(<App lang="en" />);
    await advance();

    expect([banner().getAttribute('data-mode'), banner().getAttribute('data-tone'), banner().textContent]).toEqual([
      'active',
      'ok',
      'Active control — Switchboard switches the declared devices',
    ]);
  });

  // An active option with the switch on and an invalid configuration is not shadow: the add-on still
  // sends the safe-state switch-offs to the devices of the last valid file, and the banner says so.
  it('says only switch-offs go out when control is allowed but the configuration is invalid', async () => {
    serveMode({ ...ACTIVE, configurationValid: false, effective: 'shadow', missing: ['configuration_invalid'] });
    render(<App lang="en" />);
    await advance();

    expect([banner().getAttribute('data-mode'), banner().getAttribute('data-tone'), banner().textContent]).toEqual([
      'offs_only',
      'crit',
      'Configuration invalid — Switchboard only switches devices off',
    ]);
  });

  it('an invalid configuration with control off is plain shadow: nothing is sent', async () => {
    serveMode({ ...ACTIVE, controlAllowed: false, configurationValid: false, effective: 'shadow', missing: ['control_off', 'configuration_invalid'] });
    render(<App lang="en" />);
    await advance();

    expect(banner().getAttribute('data-mode')).toBe('shadow');
  });

  it.each([
    ['an HTTP error', () => serveMode({}, 500)],
    ['an answer it does not understand', () => serveMode({ ...ACTIVE, missing: ['control_off'] })],
  ])('says the mode is unknown after %s, never shadow or active', async (_case, arrange) => {
    arrange();
    render(<App lang="en" />);
    await advance();

    expect([banner().getAttribute('data-mode'), banner().textContent]).toEqual(['unknown', 'Mode unknown — the add-on does not answer']);
  });

  it('follows a change of the mode at its next poll, and leads to the control block on the Status page', async () => {
    serveMode(SHADOW);
    render(<App lang="en" />);
    await advance();
    expect(banner().getAttribute('data-mode')).toBe('shadow');
    expect(banner().getAttribute('href')).toBe('#/status');

    serveMode(ACTIVE);
    await advance(MODE_REFRESH_INTERVAL_MS);
    expect(banner().getAttribute('data-mode')).toBe('active');
  });

  it('uses the mockup’s Polish names', async () => {
    serveMode(ACTIVE);
    render(<App lang="pl" />);
    await advance();
    expect(banner().textContent).toBe('Sterowanie aktywne — Switchboard przełącza zadeklarowane urządzenia');

    cleanup();
    serveMode(SHADOW);
    render(<App lang="pl" />);
    await advance();
    expect(banner().textContent).toBe('Obserwacja — Switchboard niczego nie przełącza');
  });

  it('lists the mockup screens in order, the day plan disabled, diagnostics after them', () => {
    render(<App lang="pl" />);
    const nav = within(screen.getByRole('navigation'));

    expect(nav.getAllByRole('link').map((a) => a.textContent)).toEqual([
      '01Pulpit',
      '02Przepływy',
      '03Konfiguracja wstępna',
      '04Karta urządzenia',
      '05Limity i przeciążenia',
      '07Dziennik decyzji',
      'Sygnały',
      'Stan',
    ]);
    for (const disabled of ['Plan doby']) {
      const item = nav.getByText(disabled);
      expect(item.closest('a')).toBeNull();
      expect(item.getAttribute('aria-disabled')).toBe('true');
      expect(item.textContent).toContain('nie w tej wersji');
    }
  });

  it('marks the page on screen in the navigation', () => {
    window.location.hash = '#/flows';
    render(<App lang="en" />);

    expect(within(screen.getByRole('navigation')).getByText('Flows').getAttribute('aria-current')).toBe('page');
    expect(within(screen.getByRole('navigation')).getByText('Dashboard').getAttribute('aria-current')).toBeNull();
  });

  // The shell asks for nothing but the mode (relative, so it stays under the ingress prefix); every
  // other request belongs to the page on screen.
  it('the shell requests only api/mode: every other request is the page on screen polling', async () => {
    window.location.hash = '#/flows';
    render(<App lang="en" />);
    await advance();

    expect(fetchMock.mock.calls.map(([input]) => input).sort()).toEqual(['api/mode', 'api/power']);
  });

  it('toggles the theme system → light → dark → system and keeps the choice for this browser', async () => {
    render(<App lang="en" />);
    const toggle = screen.getByRole('button', { name: /Change the colour theme/ });
    expect(toggle.textContent).toBe('Theme: system');

    fireEvent.click(toggle);
    expect([toggle.textContent, document.documentElement.getAttribute('data-theme'), window.localStorage.getItem(THEME_STORAGE_KEY)]).toEqual(['Theme: light', 'light', 'light']);
    fireEvent.click(toggle);
    expect([toggle.textContent, document.documentElement.getAttribute('data-theme')]).toEqual(['Theme: dark', 'dark']);
    fireEvent.click(toggle);
    expect([toggle.textContent, document.documentElement.hasAttribute('data-theme'), window.localStorage.getItem(THEME_STORAGE_KEY)]).toEqual(['Theme: system', false, null]);
  });

  it('starts with the stored choice', () => {
    window.localStorage.setItem(THEME_STORAGE_KEY, 'dark');
    render(<App lang="pl" />);

    expect(screen.getByRole('button', { name: /Zmień motyw/ }).textContent).toBe('Motyw: ciemny');
  });
});

describe('parseRoute', () => {
  it.each([
    ['', { page: 'dashboard' }],
    ['#/', { page: 'dashboard' }],
    ['#/nowhere', { page: 'dashboard' }],
    ['#/flows', { page: 'flows' }],
    ['#/devices', { page: 'device', id: null }],
    ['#/devices/', { page: 'device', id: null }],
    ['#/devices/ev_charger', { page: 'device', id: 'ev_charger' }],
    ['#/devices/%E2%82%AC', { page: 'device', id: '€' }],
    ['#/devices/%E2', { page: 'device', id: null }],
    ['#/guard', { page: 'limits' }],
    ['#/log', { page: 'log' }],
    ['#/signals', { page: 'signals' }],
    ['#/status', { page: 'status' }],
  ])('%s', (hash, route) => {
    expect(parseRoute(hash)).toEqual(route);
  });
});
