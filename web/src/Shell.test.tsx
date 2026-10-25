import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/preact';
import { App, parseRoute } from './App';
import { THEME_STORAGE_KEY } from './theme';

let fetchMock: Mock<typeof fetch>;

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

const ROUTES = ['#/', '#/flows', '#/devices', '#/devices/heater_l1', '#/guard', '#/log', '#/signals', '#/status'];

// The runtime shell: the mockup's navigation, the statement that nothing is switched and that this
// is not a protective device on every page, and the theme toggle (UI-13).
describe('shell', () => {
  it.each(ROUTES)('%s says Switchboard switches nothing in this version, and that it is not a protective device', (hash) => {
    window.location.hash = hash;
    render(<App lang="en" />);

    expect(screen.getByText('Shadow mode — Switchboard switches nothing in this version')).toBeTruthy();
    expect(screen.getByRole('note').textContent).toContain('Not a protective device.');
  });

  it('lists the mockup screens in order, setup and the day plan disabled, diagnostics after them', () => {
    render(<App lang="pl" />);
    const nav = within(screen.getByRole('navigation'));

    expect(nav.getAllByRole('link').map((a) => a.textContent)).toEqual([
      '01Pulpit',
      '02Przepływy',
      '04Karta urządzenia',
      '05Limity i przeciążenia',
      '07Dziennik decyzji',
      'Sygnały',
      'Stan',
    ]);
    for (const disabled of ['Konfiguracja wstępna', 'Plan doby']) {
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

  it('the shell itself requests nothing: only the page on screen polls', async () => {
    window.location.hash = '#/flows';
    render(<App lang="en" />);
    await advance();

    expect(fetchMock.mock.calls.map(([input]) => input)).toEqual(['api/power']);
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
