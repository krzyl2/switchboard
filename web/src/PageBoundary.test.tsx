import { afterEach, beforeEach, describe, expect, it, vi, type Mock, type MockInstance } from 'vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/preact';
import { App } from './App';
import guardSample from './api/guard.sample.json';
import modeSample from './api/mode.sample.json';
import { advance, respond } from './screens.test-support';

// One page that throws while rendering (a payload the page did not expect, a bug) must not take the
// rest of the panel with it: the rail, the mode banner, the not-a-protective-device statement and,
// above all, the kill switch's engage action stay. The decision log page stands in for any page: it
// throws at its first render while `page.broken` is set, or after an update (its button).

const ERROR = 'Cannot read properties of undefined (reading "at")';

const page = vi.hoisted(() => ({ broken: false }));

vi.mock('./DecisionLog', async () => {
  const { useState } = await import('preact/hooks');
  return {
    DecisionLog: () => {
      const [broken, setBroken] = useState(false);
      if (page.broken || broken) throw new Error(ERROR);
      return (
        <button type="button" onClick={() => setBroken(true)}>
          Break the page
        </button>
      );
    },
  };
});

let fetchMock: Mock<typeof fetch>;
let consoleError: MockInstance<typeof console.error>;

const ENGAGED = { ...structuredClone(guardSample), killSwitch: { engaged: true, changedAt: '2026-10-24T10:00:01.000+00:00', source: 'api', locked: false, switchesDevices: false } };

/** The shell's banner reads api/mode; a kill switch change is answered as evaluated; anything else is a 404. */
function serve() {
  fetchMock.mockImplementation((input, init) => {
    if (input === 'api/mode') return respond(structuredClone(modeSample));
    if (input === 'api/guard/kill-switch' && init?.method === 'POST') return respond(structuredClone(ENGAGED));
    return respond({}, 404);
  });
}

async function open(hash: string, lang: 'en' | 'pl' = 'en') {
  window.location.hash = hash;
  render(<App lang={lang} />);
  await advance();
}

async function navigate(label: string) {
  await act(async () => {
    // jsdom navigates to the fragment on its own task, as a browser does; wait for that.
    const navigated = new Promise((resolve) => window.addEventListener('hashchange', resolve, { once: true }));
    fireEvent.click(within(screen.getByRole('navigation')).getByRole('link', { name: label }));
    await navigated;
  });
  await advance();
}

const fallback = () => screen.queryByRole('heading', { name: 'This page could not be shown' })?.closest('section') ?? null;
const killCard = () => screen.getByRole('heading', { name: 'Kill switch' }).closest('section') as HTMLElement;
const killPosts = () => fetchMock.mock.calls.filter(([input]) => input === 'api/guard/kill-switch').map(([, init]) => init?.body);

/** The shell around the page: the rail on the page, the banner with the add-on's mode, the statement. */
function expectShell(current: string) {
  expect(within(screen.getByRole('navigation')).getByRole('link', { name: current }).getAttribute('aria-current')).toBe('page');
  expect(document.querySelector('.pagehead [data-mode]')?.getAttribute('data-mode')).toBe('shadow');
  expect(screen.getByRole('note').textContent).toContain('Not a protective device.');
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
  fetchMock = vi.fn<typeof fetch>();
  vi.stubGlobal('fetch', fetchMock);
  consoleError = vi.spyOn(console, 'error').mockImplementation(() => undefined);
  page.broken = false;
  serve();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.useRealTimers();
  consoleError.mockRestore();
  window.location.hash = '';
});

describe('a page that fails to render', () => {
  it('is replaced on its own: the shell stays, and the kill switch can be engaged but not released', async () => {
    page.broken = true;
    await open('#/log');

    expectShell('Decision log');
    expect(fallback()?.getAttribute('role')).toBe('alert');
    expect(fallback()?.textContent).toContain(ERROR);
    expect(fallback()?.textContent).toContain('The add-on is not affected and keeps running.');
    // The page reads no guard state, so engaging (always safe) is offered and releasing is not.
    expect(within(killCard()).getByRole('button', { name: 'Engage the kill switch' })).toBeTruthy();
    expect(within(killCard()).queryByRole('button', { name: 'Release the kill switch' })).toBeNull();
    // The stack is not lost: it goes to the browser console.
    expect(consoleError).toHaveBeenCalledWith(expect.objectContaining({ message: ERROR }));
  });

  it('engages the kill switch from the error page and says the add-on confirmed it', async () => {
    page.broken = true;
    await open('#/log');

    await act(async () => {
      fireEvent.click(within(killCard()).getByRole('button', { name: 'Engage the kill switch' }));
    });
    await advance();

    expect(killPosts()).toEqual(['{"engaged":true}']);
    expect(within(killCard()).getByRole('status').textContent).toBe('Engaged: the add-on confirmed it.');
  });

  it('is caught the same way when it happens after the page rendered', async () => {
    await open('#/log');
    expect(fallback()).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Break the page' }));
    await advance();

    expectShell('Decision log');
    expect(fallback()?.textContent).toContain(ERROR);
    expect(within(killCard()).getByRole('button', { name: 'Engage the kill switch' })).toBeTruthy();
  });

  it('leaves the other pages working, and a page opened again renders afresh', async () => {
    page.broken = true;
    await open('#/log');
    expect(fallback()).not.toBeNull();

    await navigate('Status');
    expect(fallback()).toBeNull();
    expect(screen.getByRole('heading', { name: 'Control' })).toBeTruthy();

    page.broken = false;
    await navigate('Decision log');
    expect(fallback()).toBeNull();
    expect(screen.getByRole('button', { name: 'Break the page' })).toBeTruthy();
  });

  it('says so in Polish, with the kill switch in the mockup vocabulary', async () => {
    page.broken = true;
    await open('#/log', 'pl');

    const card = screen.getByRole('heading', { name: 'Nie udało się wyświetlić tej strony' }).closest('section') as HTMLElement;
    expect(card.textContent).toContain('Dodatek nie jest tym dotknięty i działa dalej.');
    expect(screen.getByRole('button', { name: 'Włącz wstrzymanie sterowania' })).toBeTruthy();
    expect(screen.getByText('Stan wstrzymania sterowania nieznany')).toBeTruthy();
  });
});
